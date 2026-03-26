using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace Kusto.Cli;

public static partial class QueryTextResolver
{
    private const int FileScanBufferSize = 4096;

    private static readonly SearchValues<char> NewLineSearchValues = SearchValues.Create("\r\n");
    private static readonly UTF8Encoding DefaultFileEncoding = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<string> ResolveAsync(
        string? queryArgument,
        string? queryFileReference,
        bool isInputRedirected,
        TextReader stdin,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(queryFileReference))
        {
            if (!string.IsNullOrWhiteSpace(queryArgument))
            {
                throw new UserFacingException("Provide either an inline query argument or --file, but not both.");
            }

            var normalizedFileReference = queryFileReference.Trim();
            if (!TryParseFileReference(normalizedFileReference, out var fileReference, out var parseError))
            {
                if (File.Exists(normalizedFileReference))
                {
                    return await ReadQueryFromFileAsync(new QueryFileReference(normalizedFileReference, null), cancellationToken);
                }

                throw parseError;
            }

            if (!File.Exists(fileReference.Path))
            {
                throw new UserFacingException($"Query file '{fileReference.Path}' was not found.");
            }

            return await ReadQueryFromFileAsync(fileReference, cancellationToken);
        }

        if (string.Equals(queryArgument, "-", StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stdinText = await stdin.ReadToEndAsync();
            return stdinText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(queryArgument))
        {
            return queryArgument;
        }

        if (isInputRedirected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stdinText = await stdin.ReadToEndAsync();
            return stdinText.Trim();
        }

        throw new UserFacingException("No query text was provided. Supply an argument, --file, or '-' to read from stdin.");
    }

    internal static QueryFileReference ParseFileReference(string queryFileReference)
    {
        if (string.IsNullOrWhiteSpace(queryFileReference))
        {
            throw new UserFacingException("Query file path can't be empty.");
        }

        var trimmedReference = queryFileReference.Trim();
        if (TryParseFileReference(trimmedReference, out var fileReference, out var parseError))
        {
            return fileReference;
        }

        throw parseError;
    }

    private static bool IsWindowsDriveSeparator(string queryFileReference, int separatorIndex) =>
        separatorIndex == 1 &&
        char.IsAsciiLetter(queryFileReference[0]) &&
        (OperatingSystem.IsWindows() ||
            (queryFileReference.Length > 2 &&
             (queryFileReference[2] == '\\' || queryFileReference[2] == '/')));

    private static bool TryParseFileReference(
        string queryFileReference,
        out QueryFileReference fileReference,
        [NotNullWhen(false)]
        out UserFacingException? parseError)
    {
        var rangeSeparatorIndex = queryFileReference.LastIndexOf(':');
        if (rangeSeparatorIndex < 0 || IsWindowsDriveSeparator(queryFileReference, rangeSeparatorIndex))
        {
            fileReference = new QueryFileReference(queryFileReference, null);
            parseError = null;
            return true;
        }

        var filePath = queryFileReference[..rangeSeparatorIndex];
        if (string.IsNullOrWhiteSpace(filePath))
        {
            fileReference = default;
            parseError = new UserFacingException("Query file path can't be empty.");
            return false;
        }

        var rangeText = queryFileReference[(rangeSeparatorIndex + 1)..];
        if (TryParseLineRange(rangeText, out var lineRange, out parseError))
        {
            fileReference = new QueryFileReference(filePath, lineRange);
            return true;
        }

        fileReference = default;
        return false;
    }

    private static bool TryParseLineRange(
        string rangeText,
        out QueryLineRange lineRange,
        [NotNullWhen(false)]
        out UserFacingException? parseError)
    {
        var match = QueryFileLineRangePattern().Match(rangeText);
        if (!match.Success)
        {
            lineRange = default;
            parseError = new UserFacingException($"Query file range '{rangeText}' is invalid. Use '<path>:<start>-<end>'.");
            return false;
        }

        if (!int.TryParse(match.Groups["start"].Value, out var startLine) ||
            !int.TryParse(match.Groups["end"].Value, out var endLine) ||
            startLine <= 0 ||
            endLine <= 0)
        {
            lineRange = default;
            parseError = new UserFacingException("Query file line numbers must be positive integers.");
            return false;
        }

        if (endLine < startLine)
        {
            lineRange = default;
            parseError = new UserFacingException(
                $"Query file range '{rangeText}' is invalid. The end line must be greater than or equal to the start line.");
            return false;
        }

        lineRange = new QueryLineRange(startLine, endLine);
        parseError = null;
        return true;
    }

    private static async Task<string> ReadQueryFromFileAsync(
        QueryFileReference fileReference,
        CancellationToken cancellationToken)
    {
        if (fileReference.LineRange is null)
        {
            return (await File.ReadAllTextAsync(fileReference.Path, cancellationToken)).Trim();
        }

        return await ReadQueryLineRangeAsync(fileReference.Path, fileReference.LineRange.Value, cancellationToken);
    }

    private static async Task<string> ReadQueryLineRangeAsync(
        string filePath,
        QueryLineRange lineRange,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileScanBufferSize,
            FileOptions.SequentialScan);

        var detectedEncoding = await DetectFileEncodingAsync(stream, cancellationToken);
        var locatedRange = await LocateLineRangeAsync(stream, detectedEncoding, lineRange, filePath, cancellationToken);
        var rangeText = await ReadLocatedRangeAsync(stream, detectedEncoding.Encoding, locatedRange, cancellationToken);
        return rangeText.Trim();
    }

    private static async Task<DetectedFileEncoding> DetectFileEncodingAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        byte[] prefixBuffer = new byte[4];
        var bytesRead = await stream.ReadAsync(prefixBuffer.AsMemory(), cancellationToken);
        var detectedEncoding = DetectFileEncoding(prefixBuffer.AsSpan(0, bytesRead));
        stream.Seek(detectedEncoding.PreambleLength, SeekOrigin.Begin);
        return detectedEncoding;
    }

    private static DetectedFileEncoding DetectFileEncoding(ReadOnlySpan<byte> prefix)
    {
        if (HasPrefix(prefix, [0xEF, 0xBB, 0xBF]))
        {
            return new DetectedFileEncoding(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 3);
        }

        if (HasPrefix(prefix, [0xFF, 0xFE, 0x00, 0x00]))
        {
            return new DetectedFileEncoding(new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4);
        }

        if (HasPrefix(prefix, [0x00, 0x00, 0xFE, 0xFF]))
        {
            return new DetectedFileEncoding(new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        }

        if (HasPrefix(prefix, [0xFF, 0xFE]))
        {
            return new DetectedFileEncoding(Encoding.Unicode, 2);
        }

        if (HasPrefix(prefix, [0xFE, 0xFF]))
        {
            return new DetectedFileEncoding(Encoding.BigEndianUnicode, 2);
        }

        return new DetectedFileEncoding(DefaultFileEncoding, 0);
    }

    private static bool HasPrefix(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix) =>
        value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);

    private static async Task<LocatedLineRange> LocateLineRangeAsync(
        FileStream stream,
        DetectedFileEncoding detectedEncoding,
        QueryLineRange lineRange,
        string filePath,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            detectedEncoding.Encoding,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: FileScanBufferSize,
            leaveOpen: true);

        char[] charBuffer = ArrayPool<char>.Shared.Rent(FileScanBufferSize);
        try
        {
            var scanState = new LineRangeScanState(lineRange, detectedEncoding.PreambleLength);
            while (true)
            {
                var charsRead = await reader.ReadBlockAsync(charBuffer.AsMemory(0, FileScanBufferSize), cancellationToken);
                if (charsRead == 0)
                {
                    break;
                }

                if (scanState.ProcessBuffer(charBuffer.AsSpan(0, charsRead), detectedEncoding.Encoding))
                {
                    break;
                }
            }

            return scanState.Validate(filePath, stream.Length);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charBuffer);
        }
    }

    private static async Task<string> ReadLocatedRangeAsync(
        FileStream stream,
        Encoding encoding,
        LocatedLineRange locatedRange,
        CancellationToken cancellationToken)
    {
        stream.Seek(locatedRange.StartOffset, SeekOrigin.Begin);

        var remainingBytes = locatedRange.EndOffset - locatedRange.StartOffset;
        byte[] byteBuffer = ArrayPool<byte>.Shared.Rent(FileScanBufferSize);
        char[] charBuffer = ArrayPool<char>.Shared.Rent(encoding.GetMaxCharCount(FileScanBufferSize));
        var decoder = encoding.GetDecoder();
        var builder = new StringBuilder();

        try
        {
            while (remainingBytes > 0)
            {
                var bytesToRead = (int)Math.Min(byteBuffer.Length, remainingBytes);
                var bytesRead = await stream.ReadAsync(byteBuffer.AsMemory(0, bytesToRead), cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                var isFinalBlock = bytesRead == remainingBytes;
                decoder.Convert(
                    byteBuffer,
                    0,
                    bytesRead,
                    charBuffer,
                    0,
                    charBuffer.Length,
                    isFinalBlock,
                    out var bytesUsed,
                    out var charsUsed,
                    out _);

                if (bytesUsed != bytesRead)
                {
                    throw new InvalidOperationException("Failed to decode the selected query file range.");
                }

                builder.Append(charBuffer, 0, charsUsed);
                remainingBytes -= bytesRead;
            }

            return builder.ToString();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(byteBuffer);
            ArrayPool<char>.Shared.Return(charBuffer);
        }
    }

    private sealed class LineRangeScanState
    {
        private readonly QueryLineRange _lineRange;
        private readonly long _contentStartOffset;

        private bool _sawAnyCharacters;
        private bool _pendingLineStart;
        private long _pendingLineStartOffset;
        private bool _pendingCarriageReturn;

        public LineRangeScanState(QueryLineRange lineRange, long contentStartOffset)
        {
            _lineRange = lineRange;
            _contentStartOffset = contentStartOffset;
            _pendingLineStartOffset = contentStartOffset;
            CurrentByteOffset = contentStartOffset;
        }

        public int ValidLineCount { get; private set; }

        public long CurrentByteOffset { get; private set; }

        public long? RangeStartOffset { get; private set; }

        public long? RangeEndOffset { get; private set; }

        public bool ProcessBuffer(ReadOnlySpan<char> buffer, Encoding encoding)
        {
            var bufferIndex = 0;
            if (_pendingCarriageReturn)
            {
                if (!buffer.IsEmpty && buffer[0] == '\n')
                {
                    CurrentByteOffset += encoding.GetByteCount(buffer[..1]);
                    _pendingLineStartOffset = CurrentByteOffset;
                    bufferIndex = 1;
                }

                _pendingCarriageReturn = false;
            }

            if (bufferIndex >= buffer.Length)
            {
                return false;
            }

            EnsureLineStarted();
            if (RangeEndOffset is not null)
            {
                return true;
            }

            var remaining = buffer[bufferIndex..];
            var segmentStart = 0;

            while (segmentStart < remaining.Length)
            {
                var newlineIndex = remaining[segmentStart..].IndexOfAny(NewLineSearchValues);
                if (newlineIndex < 0)
                {
                    CurrentByteOffset += encoding.GetByteCount(remaining[segmentStart..]);
                    break;
                }

                newlineIndex += segmentStart;
                CurrentByteOffset += encoding.GetByteCount(remaining[segmentStart..newlineIndex]);
                CurrentByteOffset += encoding.GetByteCount(remaining.Slice(newlineIndex, 1));

                var nextSegmentStart = newlineIndex + 1;
                if (remaining[newlineIndex] == '\r')
                {
                    if (nextSegmentStart < remaining.Length && remaining[nextSegmentStart] == '\n')
                    {
                        CurrentByteOffset += encoding.GetByteCount(remaining.Slice(nextSegmentStart, 1));
                        nextSegmentStart++;
                    }
                    else if (nextSegmentStart == remaining.Length)
                    {
                        _pendingCarriageReturn = true;
                    }
                }

                _pendingLineStart = true;
                _pendingLineStartOffset = CurrentByteOffset;
                segmentStart = nextSegmentStart;

                if (_pendingCarriageReturn)
                {
                    break;
                }

                if (segmentStart < remaining.Length)
                {
                    EnsureLineStarted();
                    if (RangeEndOffset is not null)
                    {
                        return true;
                    }
                }
            }

            return RangeEndOffset is not null;
        }

        public LocatedLineRange Validate(string filePath, long fileLength)
        {
            if (RangeStartOffset is null || _lineRange.EndLine > ValidLineCount)
            {
                throw new UserFacingException(
                    $"Query file range '{_lineRange.StartLine}-{_lineRange.EndLine}' is out of range for '{filePath}', which has {ValidLineCount} line{(ValidLineCount == 1 ? string.Empty : "s")}.");
            }

        return new LocatedLineRange(RangeStartOffset.Value, RangeEndOffset ?? fileLength);
    }

        private void EnsureLineStarted()
        {
            long lineStartOffset;
            if (!_sawAnyCharacters)
            {
                _sawAnyCharacters = true;
                lineStartOffset = _contentStartOffset;
            }
            else if (_pendingLineStart)
            {
                lineStartOffset = _pendingLineStartOffset;
                _pendingLineStart = false;
            }
            else
            {
                return;
            }

            ValidLineCount++;
            if (ValidLineCount == _lineRange.StartLine)
            {
                RangeStartOffset = lineStartOffset;
            }

            if (ValidLineCount == _lineRange.EndLine + 1)
            {
                RangeEndOffset = lineStartOffset;
            }
        }
    }

    private readonly record struct DetectedFileEncoding(Encoding Encoding, int PreambleLength);

    private readonly record struct LocatedLineRange(long StartOffset, long EndOffset);

    [GeneratedRegex("^(?<start>-?\\d+)-(?<end>-?\\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex QueryFileLineRangePattern();
}
