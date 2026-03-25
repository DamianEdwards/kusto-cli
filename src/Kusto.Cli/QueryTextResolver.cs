using System.Text.RegularExpressions;

namespace Kusto.Cli;

public static class QueryTextResolver
{
    private static readonly Regex QueryFileLineRangePattern = new(
        "^(?<start>-?\\d+)-(?<end>-?\\d+)$",
        RegexOptions.CultureInvariant);

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

                throw parseError!;
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

        throw parseError!;
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
        out UserFacingException? parseError)
    {
        var match = QueryFileLineRangePattern.Match(rangeText);
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

        var lineRange = fileReference.LineRange.Value;
        var lines = await File.ReadAllLinesAsync(fileReference.Path, cancellationToken);
        if (lineRange.EndLine > lines.Length)
        {
            throw new UserFacingException(
                $"Query file range '{lineRange.StartLine}-{lineRange.EndLine}' is out of range for '{fileReference.Path}', which has {lines.Length} line{(lines.Length == 1 ? string.Empty : "s")}.");
        }

        return string.Join(
                Environment.NewLine,
                lines.Skip(lineRange.StartLine - 1).Take(lineRange.LineCount))
            .Trim();
    }
}
