using System.Diagnostics;
using System.Globalization;
using System.Text;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Widgets;

namespace Kusto.Cli;

internal static class Hex1bHumanRenderer
{
    private const int RenderTimeoutMilliseconds = 5000;
    private const int RenderPollIntervalMilliseconds = 25;
    private static readonly TerminalAnsiOptions AnsiOptions = new()
    {
        IncludeClearScreen = false,
        IncludeCursorPosition = false,
        IncludeTrailingNewline = false,
        RenderNullAsSpace = true,
        ResetAtEnd = true
    };

    public static string RenderText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return Render(
            ctx => ctx.Text(text),
            CalculateTextDimensions(text),
            captureAnsi: false);
    }

    public static string RenderHyperlink(string text, string url, bool useAnsi)
    {
        if (!useAnsi)
        {
            return RenderText($"{text}: {url}");
        }

        return $"\u001b]8;;{url}\u001b\\{RenderText(text)}\u001b]8;;\u001b\\";
    }

    public static string FormatKeyValueTable(IReadOnlyDictionary<string, string?> properties, int? maxWidth = null)
    {
        return FormatTable(
            ["Property", "Value"],
            properties
                .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => (IReadOnlyList<string?>)[pair.Key, pair.Value])
                .ToArray(),
            [false, false],
            includeRowSeparators: false,
            maxWidth: maxWidth);
    }

    public static string FormatDataTable(TabularData data, bool isQueryResultTable, int? maxWidth = null)
    {
        if (isQueryResultTable)
        {
            return FormatCompactQueryTable(data, maxWidth);
        }

        if (data.Columns.Count == 0)
        {
            return FormatTable(
                ["Value"],
                data.Rows.Select(row => (IReadOnlyList<string?>)[string.Join(", ", row.Select(value => value ?? string.Empty))]).ToArray(),
                [false],
                includeRowSeparators: isQueryResultTable,
                maxWidth: maxWidth);
        }

        var rightAlignedColumns = new bool[data.Columns.Count];
        for (var i = 0; i < data.Columns.Count; i++)
        {
            rightAlignedColumns[i] = isQueryResultTable && ShouldRightAlignColumn(data, i);
        }

        return FormatTable(
            data.Columns,
            data.Rows.Select(row => row.Select(value => (string?)value).ToArray()).ToArray(),
            rightAlignedColumns,
            includeRowSeparators: isQueryResultTable,
            maxWidth: maxWidth);
    }

    private static string FormatCompactQueryTable(TabularData data, int? maxWidth)
    {
        if (data.Columns.Count == 0)
        {
            return FormatDataTable(new TabularData(["Value"], data.Rows), isQueryResultTable: false, maxWidth);
        }

        data = FormatQueryValuesForDisplay(data);

        var columnCount = data.Columns.Count;
        var headerLines = data.Columns
            .Select(header => SplitCellLines(header).AsReadOnly())
            .ToArray();
        var normalizedRows = data.Rows
            .Select(row => Enumerable.Range(0, columnCount)
                .Select(index => index < row.Count ? SplitCellLines(row[index]) : [string.Empty])
                .ToArray())
            .ToArray();

        var widths = new int[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            widths[i] = headerLines[i].Max(GetDisplayWidth);
        }

        foreach (var row in normalizedRows)
        {
            for (var i = 0; i < columnCount; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Max(GetDisplayWidth));
            }
        }

        var rightAlignedColumns = new bool[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            rightAlignedColumns[i] = ShouldRightAlignColumn(data, i);
        }

        widths = FitWidths(widths, maxWidth, minimumWidth: 3, frameWidth: GetCompactTableFrameWidth(columnCount));
        if (ShouldUseRecordLayout(data, widths, maxWidth))
        {
            return FormatQueryRowsAsRecords(data, maxWidth);
        }

        var wrappedHeaders = WrapCells(headerLines, widths);
        var displayRows = normalizedRows
            .Select(row => TruncateCells(row, widths))
            .ToArray();

        var builder = new StringBuilder();
        AppendCompactRow(builder, wrappedHeaders, widths, Enumerable.Repeat(false, columnCount).ToArray());
        AppendCompactSeparator(builder, widths);
        for (var rowIndex = 0; rowIndex < displayRows.Length; rowIndex++)
        {
            AppendCompactRow(builder, displayRows[rowIndex], widths, rightAlignedColumns);
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatQueryRowsAsRecords(TabularData data, int? maxWidth)
    {
        var sections = new List<string>(data.Rows.Count);
        for (var rowIndex = 0; rowIndex < data.Rows.Count; rowIndex++)
        {
            var row = data.Rows[rowIndex];
            var properties = Enumerable.Range(0, data.Columns.Count)
                .Select(index => (IReadOnlyList<string?>)
                [
                    data.Columns[index],
                    index < row.Count ? row[index] : string.Empty
                ])
                .ToArray();

            var builder = new StringBuilder();
            builder.AppendLine($"Row {rowIndex + 1}");
            builder.AppendLine();
            builder.Append(FormatTable(
                ["Column", "Value"],
                properties,
                [false, false],
                includeRowSeparators: false,
                maxWidth: maxWidth));
            sections.Add(builder.ToString().TrimEnd());
        }

        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    internal static string FormatTable(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string?>> rows,
        IReadOnlyList<bool> rightAlignedColumns,
        bool includeRowSeparators,
        int? maxWidth = null)
    {
        var columnCount = headers.Count;
        var headerLines = headers
            .Select(header => SplitCellLines(header).AsReadOnly())
            .ToArray();
        var normalizedRows = rows
            .Select(row => Enumerable.Range(0, columnCount)
                .Select(index => index < row.Count ? SplitCellLines(row[index]) : [string.Empty])
                .ToArray())
            .ToArray();

        var widths = new int[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            widths[i] = headerLines[i].Max(GetDisplayWidth);
        }

        foreach (var row in normalizedRows)
        {
            for (var i = 0; i < columnCount; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Max(GetDisplayWidth));
            }
        }

        widths = FitWidths(widths, maxWidth, minimumWidth: 2, frameWidth: GetTableFrameWidth(widths.Length));
        var wrappedHeaders = WrapCells(headerLines, widths);
        var wrappedRows = normalizedRows
            .Select(row => WrapCells(row, widths))
            .ToArray();

        var builder = new StringBuilder();
        AppendBorder(builder, widths, '┌', '┬', '┐');
        AppendRow(builder, wrappedHeaders, widths, Enumerable.Repeat(false, columnCount).ToArray());
        AppendBorder(builder, widths, '├', '┼', '┤');

        for (var rowIndex = 0; rowIndex < wrappedRows.Length; rowIndex++)
        {
            AppendRow(builder, wrappedRows[rowIndex], widths, rightAlignedColumns);
            if (includeRowSeparators && rowIndex < wrappedRows.Length - 1)
            {
                AppendBorder(builder, widths, '├', '┼', '┤');
            }
        }

        AppendBorder(builder, widths, '└', '┴', '┘');
        return builder.ToString().TrimEnd();
    }

    private static string Render(
        Func<RootContext, Hex1bWidget> buildWidget,
        (int Width, int Height) dimensions,
        bool captureAnsi)
    {
        return RenderAsync(buildWidget, dimensions, captureAnsi, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task<string> RenderAsync(
        Func<RootContext, Hex1bWidget> buildWidget,
        (int Width, int Height) dimensions,
        bool captureAnsi,
        CancellationToken cancellationToken)
    {
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(dimensions.Width, dimensions.Height)
            .WithHex1bApp((app, options) =>
            {
                options.EnableDefaultCtrlCExit = false;
                return ctx => buildWidget(ctx);
            })
            .Build();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runTask = terminal.RunAsync(cts.Token);
        try
        {
            return await WaitForRenderedOutputAsync(terminal, runTask, captureAnsi, cancellationToken);
        }
        finally
        {
            cts.Cancel();
            try
            {
                await runTask;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<string> WaitForRenderedOutputAsync(
        Hex1bTerminal terminal,
        Task runTask,
        bool captureAnsi,
        CancellationToken cancellationToken)
    {
        string? previous = null;
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < RenderTimeoutMilliseconds)
        {
            if (runTask.IsFaulted)
            {
                await runTask;
            }

            using var snapshot = terminal.CreateSnapshot();
            var current = captureAnsi
                ? snapshot.ToAnsi(AnsiOptions)
                : NormalizeRenderedText(snapshot.GetScreenText());

            if (!string.IsNullOrEmpty(current) && string.Equals(current, previous, StringComparison.Ordinal))
            {
                return current;
            }

            previous = current;
            await Task.Delay(RenderPollIntervalMilliseconds, cancellationToken);
        }

        return previous ?? string.Empty;
    }

    private static (int Width, int Height) CalculateTextDimensions(string text)
    {
        var lines = SplitLines(text);
        var width = Math.Max(1, lines.Max(GetDisplayWidth));
        var height = Math.Max(1, lines.Length);
        return (width, height);
    }

    private static string NormalizeRenderedText(string screenText)
    {
        var lines = SplitLines(screenText);
        var builder = new StringBuilder();

        var start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        var end = lines.Length - 1;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        for (var i = start; i <= end; i++)
        {
            builder.AppendLine(lines[i].TrimEnd());
        }

        return builder.ToString().TrimEnd();
    }

    private static string[] SplitLines(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }

    private static void AppendBorder(StringBuilder builder, IReadOnlyList<int> widths, char left, char middle, char right)
    {
        builder.Append(left);
        for (var i = 0; i < widths.Count; i++)
        {
            builder.Append('─', widths[i] + 2);
            builder.Append(i == widths.Count - 1 ? right : middle);
        }

        builder.AppendLine();
    }

    private static void AppendRow(
        StringBuilder builder,
        IReadOnlyList<IReadOnlyList<string>> values,
        IReadOnlyList<int> widths,
        IReadOnlyList<bool> rightAlignedColumns)
    {
        var rowHeight = values.Max(static value => value.Count);
        for (var lineIndex = 0; lineIndex < rowHeight; lineIndex++)
        {
            builder.Append('│');
            for (var columnIndex = 0; columnIndex < widths.Count; columnIndex++)
            {
                builder.Append(' ');
                var line = lineIndex < values[columnIndex].Count
                    ? values[columnIndex][lineIndex]
                    : string.Empty;
                builder.Append(Align(line, widths[columnIndex], rightAlignedColumns[columnIndex]));
                builder.Append(' ');
                builder.Append('│');
            }

            builder.AppendLine();
        }
    }

    private static string Align(string value, int width, bool rightAligned)
    {
        var padding = Math.Max(0, width - GetDisplayWidth(value));
        return rightAligned
            ? string.Concat(new string(' ', padding), value)
            : string.Concat(value, new string(' ', padding));
    }

    private static bool ShouldRightAlignColumn(TabularData data, int columnIndex)
    {
        var hasValue = false;
        foreach (var row in data.Rows)
        {
            if (columnIndex >= row.Count)
            {
                continue;
            }

            var value = row[columnIndex];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            hasValue = true;
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return hasValue;
    }

    private static readonly string[] KustoDateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"
    ];

    internal static TabularData FormatQueryValuesForDisplay(TabularData data)
    {
        if (data.Rows.Count == 0 || data.Columns.Count == 0)
        {
            return data;
        }

        var columnCount = data.Columns.Count;
        var formats = new ColumnDisplayFormat[columnCount];
        var anyFormatted = false;

        for (var i = 0; i < columnCount; i++)
        {
            formats[i] = DetectColumnDisplayFormat(data, i);
            if (formats[i] != ColumnDisplayFormat.None)
            {
                anyFormatted = true;
            }
        }

        if (!anyFormatted)
        {
            return data;
        }

        var formattedRows = new IReadOnlyList<string?>[data.Rows.Count];
        for (var rowIndex = 0; rowIndex < data.Rows.Count; rowIndex++)
        {
            var row = data.Rows[rowIndex];
            var formatted = new string?[columnCount];
            for (var i = 0; i < columnCount; i++)
            {
                var value = i < row.Count ? row[i] : null;
                formatted[i] = formats[i] switch
                {
                    ColumnDisplayFormat.Integer => FormatIntegerValue(value),
                    ColumnDisplayFormat.DateTime => FormatDateTimeValue(value),
                    _ => value
                };
            }

            formattedRows[rowIndex] = formatted;
        }

        return new TabularData(data.Columns, formattedRows);
    }

    private enum ColumnDisplayFormat
    {
        None,
        Integer,
        DateTime
    }

    private static ColumnDisplayFormat DetectColumnDisplayFormat(TabularData data, int columnIndex)
    {
        var couldBeInteger = true;
        var couldBeDateTime = true;
        var hasValue = false;

        foreach (var row in data.Rows)
        {
            if (columnIndex >= row.Count)
            {
                continue;
            }

            var value = row[columnIndex];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            hasValue = true;

            if (couldBeInteger)
            {
                couldBeInteger = IsFormattableInteger(value);
            }

            if (couldBeDateTime)
            {
                couldBeDateTime = IsKustoDateTimeFormat(value);
            }

            if (!couldBeInteger && !couldBeDateTime)
            {
                break;
            }
        }

        if (!hasValue)
        {
            return ColumnDisplayFormat.None;
        }

        if (couldBeDateTime)
        {
            return ColumnDisplayFormat.DateTime;
        }

        if (couldBeInteger)
        {
            return ColumnDisplayFormat.Integer;
        }

        return ColumnDisplayFormat.None;
    }

    private static bool IsFormattableInteger(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        // Reject leading '+' (could be a non-numeric identifier)
        if (value[0] == '+')
        {
            return false;
        }

        // Reject leading zeros (could be an ID like "00123"), except "0" and "-0"
        var digitStart = value[0] == '-' ? 1 : 0;
        if (digitStart < value.Length - 1 && value[digitStart] == '0')
        {
            return false;
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsKustoDateTimeFormat(string value)
    {
        return DateTimeOffset.TryParseExact(
            value,
            KustoDateTimeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);
    }

    private static string? FormatIntegerValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return value;
        }

        return number.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string? FormatDateTimeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (!DateTimeOffset.TryParseExact(value, KustoDateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return value;
        }

        if (dt.TimeOfDay == TimeSpan.Zero)
        {
            return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var hasFractionalSeconds = dt.TimeOfDay.Ticks % TimeSpan.TicksPerSecond != 0;
        return hasFractionalSeconds
            ? dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static int[] FitWidths(int[] naturalWidths, int? maxWidth, int minimumWidth, int frameWidth)
    {
        if (maxWidth is null)
        {
            return naturalWidths;
        }

        var widths = naturalWidths.ToArray();
        var availableContentWidth = maxWidth.Value - frameWidth;
        if (availableContentWidth <= 0)
        {
            return widths;
        }

        while (widths.Sum() > availableContentWidth)
        {
            var shrinkIndex = -1;
            for (var i = 0; i < widths.Length; i++)
            {
                if (widths[i] <= minimumWidth)
                {
                    continue;
                }

                if (shrinkIndex < 0 || widths[i] > widths[shrinkIndex])
                {
                    shrinkIndex = i;
                }
            }

            if (shrinkIndex < 0)
            {
                break;
            }

            widths[shrinkIndex]--;
        }

        return widths;
    }

    private static string[] SplitCellLines(string? value)
    {
        var normalized = (value ?? string.Empty)
            .ReplaceLineEndings("\n")
            .Replace("\t", "    ", StringComparison.Ordinal);
        return normalized.Split('\n');
    }

    private static int GetDisplayWidth(string value)
    {
        var width = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            width += GetRuneWidth(rune);
        }

        return width;
    }

    private static IReadOnlyList<IReadOnlyList<string>> WrapCells(IReadOnlyList<IReadOnlyList<string>> cells, IReadOnlyList<int> widths)
    {
        var wrapped = new IReadOnlyList<string>[cells.Count];
        for (var i = 0; i < cells.Count; i++)
        {
            wrapped[i] = WrapCellLines(cells[i], widths[i]);
        }

        return wrapped;
    }

    private static IReadOnlyList<IReadOnlyList<string>> TruncateCells(IReadOnlyList<IReadOnlyList<string>> cells, IReadOnlyList<int> widths)
    {
        var truncated = new IReadOnlyList<string>[cells.Count];
        for (var i = 0; i < cells.Count; i++)
        {
            truncated[i] = TruncateCellLines(cells[i], widths[i]);
        }

        return truncated;
    }

    private static IReadOnlyList<string> WrapCellLines(IReadOnlyList<string> lines, int width)
    {
        var wrapped = new List<string>();
        foreach (var line in lines)
        {
            wrapped.AddRange(WrapLine(line, width));
        }

        if (wrapped.Count == 0)
        {
            wrapped.Add(string.Empty);
        }

        return wrapped;
    }

    private static IReadOnlyList<string> TruncateCellLines(IReadOnlyList<string> lines, int width)
    {
        var truncated = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            truncated.Add(EllipsizeLine(line, width));
        }

        if (truncated.Count == 0)
        {
            truncated.Add(string.Empty);
        }

        return truncated;
    }

    private static IEnumerable<string> WrapLine(string line, int width)
    {
        if (width <= 0)
        {
            yield return string.Empty;
            yield break;
        }

        if (string.IsNullOrEmpty(line))
        {
            yield return string.Empty;
            yield break;
        }

        var segmentStart = 0;
        var segmentWidth = 0;
        var index = 0;

        while (index < line.Length)
        {
            var rune = Rune.GetRuneAt(line, index);
            var runeWidth = Math.Max(1, GetRuneWidth(rune));
            if (segmentWidth > 0 && segmentWidth + runeWidth > width)
            {
                yield return line.Substring(segmentStart, index - segmentStart);
                segmentStart = index;
                segmentWidth = 0;
                continue;
            }

            segmentWidth += runeWidth;
            index += rune.Utf16SequenceLength;
        }

        yield return line.Substring(segmentStart);
    }

    private static int GetTableFrameWidth(int columnCount)
    {
        return (columnCount * 3) + 1;
    }

    private static void AppendCompactRow(
        StringBuilder builder,
        IReadOnlyList<IReadOnlyList<string>> values,
        IReadOnlyList<int> widths,
        IReadOnlyList<bool> rightAlignedColumns)
    {
        var rowHeight = values.Max(static value => value.Count);
        for (var lineIndex = 0; lineIndex < rowHeight; lineIndex++)
        {
            for (var columnIndex = 0; columnIndex < widths.Count; columnIndex++)
            {
                if (columnIndex > 0)
                {
                    builder.Append("  ");
                }

                var line = lineIndex < values[columnIndex].Count
                    ? values[columnIndex][lineIndex]
                    : string.Empty;
                builder.Append(Align(line, widths[columnIndex], rightAlignedColumns[columnIndex]));
            }

            builder.AppendLine();
        }
    }

    private static void AppendCompactSeparator(StringBuilder builder, IReadOnlyList<int> widths)
    {
        for (var columnIndex = 0; columnIndex < widths.Count; columnIndex++)
        {
            if (columnIndex > 0)
            {
                builder.Append("  ");
            }

            builder.Append('─', widths[columnIndex]);
        }

        builder.AppendLine();
    }

    private static int GetCompactTableFrameWidth(int columnCount)
    {
        return (columnCount - 1) * 2;
    }

    private static bool ShouldUseRecordLayout(TabularData data, IReadOnlyList<int> widths, int? maxWidth)
    {
        if (maxWidth is null || data.Rows.Count == 0 || data.Columns.Count < 10)
        {
            return false;
        }

        return widths.Average() < 8;
    }

    private static string EllipsizeLine(string line, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (GetDisplayWidth(line) <= width)
        {
            return line;
        }

        if (width == 1)
        {
            return "…";
        }

        var builder = new StringBuilder();
        var currentWidth = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var runeWidth = GetRuneWidth(rune);
            if (currentWidth + Math.Max(1, runeWidth) > width - 1)
            {
                break;
            }

            builder.Append(rune.ToString());
            currentWidth += Math.Max(1, runeWidth);
        }

        builder.Append('…');
        return builder.ToString();
    }

    private static int GetRuneWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark ||
            rune.Value is 0x200D or >= 0xFE00 and <= 0xFE0F)
        {
            return 0;
        }

        if (Rune.IsControl(rune))
        {
            return 0;
        }

        return IsWideRune(rune) ? 2 : 1;
    }

    private static bool IsWideRune(Rune rune)
    {
        return rune.Value switch
        {
            >= 0x1100 and <= 0x115F => true,
            >= 0x2329 and <= 0x232A => true,
            >= 0x2E80 and <= 0xA4CF => true,
            >= 0xAC00 and <= 0xD7A3 => true,
            >= 0xF900 and <= 0xFAFF => true,
            >= 0xFE10 and <= 0xFE19 => true,
            >= 0xFE30 and <= 0xFE6F => true,
            >= 0xFF00 and <= 0xFF60 => true,
            >= 0xFFE0 and <= 0xFFE6 => true,
            >= 0x1F300 and <= 0x1FAFF => true,
            >= 0x20000 and <= 0x3FFFD => true,
            _ => false
        };
    }
}
