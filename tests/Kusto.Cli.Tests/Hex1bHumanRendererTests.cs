namespace Kusto.Cli.Tests;

public sealed class Hex1bHumanRendererTests
{
    [Fact]
    public void FormatDataTable_QueryResults_RightAlignsNumericColumns()
    {
        var table = new TabularData(
            ["Name", "Count"],
            [
                ["alpha", "42"],
                ["beta", "7"]
            ]);

        var rendered = Hex1bHumanRenderer.FormatDataTable(table, isQueryResultTable: true);

        Assert.Contains("Name   Count", rendered, StringComparison.Ordinal);
        Assert.Contains("alpha     42", rendered, StringComparison.Ordinal);
        Assert.Contains("beta       7", rendered, StringComparison.Ordinal);
        Assert.Contains("─────", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHyperlink_WithAnsi_EmitsOsc8Sequence()
    {
        var rendered = Hex1bHumanRenderer.RenderHyperlink("Open in Web Explorer", "https://example.com", useAnsi: true);

        Assert.Contains("\u001b]8;;https://example.com", rendered, StringComparison.Ordinal);
        Assert.Contains("Open in Web Explorer", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHyperlink_WithoutAnsi_FallsBackToText()
    {
        var rendered = Hex1bHumanRenderer.RenderHyperlink("Open in Web Explorer", "https://example.com", useAnsi: false);

        Assert.Contains("Open in Web Explorer: https://example.com", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b]8;;", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderText_PreservesWideUnicodeCharacters()
    {
        var rendered = Hex1bHumanRenderer.RenderText("abc漢字");

        Assert.Equal("abc漢字", rendered);
    }

    [Fact]
    public void FormatDataTable_PreservesMultilineCellValues()
    {
        var table = new TabularData(
            ["Name", "Value"],
            [
                ["alpha", "line1\nline2"]
            ]);

        var rendered = Hex1bHumanRenderer.FormatDataTable(table, isQueryResultTable: false);

        Assert.Contains("line1", rendered, StringComparison.Ordinal);
        Assert.Contains("line2", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("line1 line2", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDataTable_WithMaxWidth_FitsWithinRequestedWidth()
    {
        var table = new TabularData(
            ["Section", "Example"],
            [
                ["Run KQL", "kusto query \"StormEvents | take 5\" --cluster help --database Samples"]
            ]);

        var rendered = Hex1bHumanRenderer.FormatDataTable(table, isQueryResultTable: false, maxWidth: 40);

        Assert.All(rendered.Split(Environment.NewLine), line => Assert.True(line.Length <= 40, $"Line exceeded width: {line}"));
        Assert.Contains("--cluster", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDataTable_WideQueryResults_FallBackToRecordLayout()
    {
        var columns = Enumerable.Range(1, 10).Select(index => $"Column{index}").ToArray();
        var row = Enumerable.Range(1, 10).Select(index => new string((char)('A' + index - 1), 12)).ToArray();
        var table = new TabularData(columns, [row]);

        var rendered = Hex1bHumanRenderer.FormatDataTable(table, isQueryResultTable: true, maxWidth: 80);

        Assert.Contains("Row 1", rendered, StringComparison.Ordinal);
        Assert.Contains("Column", rendered, StringComparison.Ordinal);
        Assert.Contains("Value", rendered, StringComparison.Ordinal);
        Assert.Contains("Column10", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatQueryValuesForDisplay_IntegerColumn_AddsThousandsSeparators()
    {
        var table = new TabularData(
            ["Name", "RowCount"],
            [
                ["Events", "66380993"],
                ["Metrics", "19639740"]
            ]);

        var formatted = Hex1bHumanRenderer.FormatQueryValuesForDisplay(table);

        Assert.Equal("66,380,993", formatted.Rows[0][1]);
        Assert.Equal("19,639,740", formatted.Rows[1][1]);
        Assert.Equal("Events", formatted.Rows[0][0]);
    }

    [Fact]
    public void FormatQueryValuesForDisplay_SmallIntegers_NoUnnecessarySeparators()
    {
        var table = new TabularData(
            ["Name", "Count"],
            [
                ["alpha", "42"],
                ["beta", "0"],
                ["gamma", "-1"]
            ]);

        var formatted = Hex1bHumanRenderer.FormatQueryValuesForDisplay(table);

        Assert.Equal("42", formatted.Rows[0][1]);
        Assert.Equal("0", formatted.Rows[1][1]);
        Assert.Equal("-1", formatted.Rows[2][1]);
    }

    [Fact]
    public void FormatQueryValuesForDisplay_NegativeIntegers_FormattedWithSeparators()
    {
        var table = new TabularData(
            ["Metric", "Value"],
            [
                ["delta", "-1234567"]
            ]);

        var formatted = Hex1bHumanRenderer.FormatQueryValuesForDisplay(table);

        Assert.Equal("-1,234,567", formatted.Rows[0][1]);
    }

    [Fact]
    public void FormatQueryValuesForDisplay_LeadingZeroStrings_NotFormatted()
    {
        var table = new TabularData(
            ["Id"],
            [
                ["00123"],
                ["00456"]
            ]);

        var formatted = Hex1bHumanRenderer.FormatQueryValuesForDisplay(table);

        Assert.Equal("00123", formatted.Rows[0][0]);
        Assert.Equal("00456", formatted.Rows[1][0]);
    }

    [Fact]
    public void FormatQueryValuesForDisplay_MidnightDateTimes_StripsTimeComponent()
    {
        var table = new TabularData(
            ["Day", "RowCount"],
            [
                ["2026-02-27T00:00:00Z", "66380993"],
                ["2026-02-28T00:00:00Z", "19639740"]
            ]);

        var formatted = Hex1bHumanRenderer.FormatQueryValuesForDisplay(table);

        Assert.Equal("2026-02-27", formatted.Rows[0][0]);
        Assert.Equal("2026-02-28", formatted.Rows[1][0]);
        Assert.Equal("66,380,993", formatted.Rows[0][1]);
    }

    [Fact]
    public void FormatQueryValuesForDisplay_NonMidnightDateTimes_ShowsReadableFormat()
    {
        var table = new TabularData(
            ["Timestamp"],
            [
                ["2026-03-10T14:30:15Z"],
                ["2026-03-10T08:00:00Z"]
            ]);

        var formatted = Hex1bHumanRenderer.FormatQueryValuesForDisplay(table);

        Assert.Equal("2026-03-10 14:30:15", formatted.Rows[0][0]);
        Assert.Equal("2026-03-10 08:00:00", formatted.Rows[1][0]);
    }

    [Fact]
    public void FormatQueryValuesForDisplay_DateTimesWithFractionalSeconds_PreservesFractionalPart()
    {
        var table = new TabularData(
            ["Timestamp"],
            [
                ["2026-03-10T14:30:15.1234567Z"]
            ]);

        var formatted = Hex1bHumanRenderer.FormatQueryValuesForDisplay(table);

        Assert.Equal("2026-03-10 14:30:15.1234567", formatted.Rows[0][0]);
    }

    [Fact]
    public void FormatQueryValuesForDisplay_MixedColumnTypes_LeavesNonUniformColumnsUnformatted()
    {
        var table = new TabularData(
            ["Value"],
            [
                ["66380993"],
                ["not-a-number"]
            ]);

        var formatted = Hex1bHumanRenderer.FormatQueryValuesForDisplay(table);

        Assert.Equal("66380993", formatted.Rows[0][0]);
        Assert.Equal("not-a-number", formatted.Rows[1][0]);
    }

    [Fact]
    public void FormatQueryValuesForDisplay_NullAndEmptyValues_HandledGracefully()
    {
        var table = new TabularData(
            ["Day", "Count"],
            [
                ["2026-02-27T00:00:00Z", "66380993"],
                [null, null],
                ["2026-03-01T00:00:00Z", ""]
            ]);

        var formatted = Hex1bHumanRenderer.FormatQueryValuesForDisplay(table);

        Assert.Equal("2026-02-27", formatted.Rows[0][0]);
        Assert.Equal("66,380,993", formatted.Rows[0][1]);
        Assert.Null(formatted.Rows[1][0]);
        Assert.Null(formatted.Rows[1][1]);
        Assert.Equal("2026-03-01", formatted.Rows[2][0]);
        Assert.Equal("", formatted.Rows[2][1]);
    }

    [Fact]
    public void FormatQueryValuesForDisplay_DoubleValues_LeftUnformatted()
    {
        var table = new TabularData(
            ["Average"],
            [
                ["66380993.456"],
                ["1.23"]
            ]);

        var formatted = Hex1bHumanRenderer.FormatQueryValuesForDisplay(table);

        Assert.Equal("66380993.456", formatted.Rows[0][0]);
        Assert.Equal("1.23", formatted.Rows[1][0]);
    }

    [Fact]
    public void FormatQueryValuesForDisplay_EmptyTable_ReturnsOriginal()
    {
        var table = new TabularData(["Name", "Count"], []);

        var formatted = Hex1bHumanRenderer.FormatQueryValuesForDisplay(table);

        Assert.Same(table, formatted);
    }

    [Fact]
    public void FormatQueryValuesForDisplay_NonQueryTable_NotFormatted()
    {
        // Non-query tables (IsQueryResultTable = false) go through FormatDataTable,
        // not FormatCompactQueryTable, so formatting is NOT applied
        var table = new TabularData(
            ["Name", "Count"],
            [
                ["Events", "66380993"]
            ]);

        var rendered = Hex1bHumanRenderer.FormatDataTable(table, isQueryResultTable: false);

        Assert.Contains("66380993", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("66,380,993", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDataTable_QueryResults_FormatsIntegersWithThousandsSeparators()
    {
        var table = new TabularData(
            ["Day", "RowCount"],
            [
                ["2026-02-27T00:00:00Z", "66380993"],
                ["2026-02-28T00:00:00Z", "19639740"]
            ]);

        var rendered = Hex1bHumanRenderer.FormatDataTable(table, isQueryResultTable: true);

        Assert.Contains("2026-02-27", rendered, StringComparison.Ordinal);
        Assert.Contains("66,380,993", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("T00:00:00Z", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDataTable_QueryResults_FormattedIntegersRemainRightAligned()
    {
        var table = new TabularData(
            ["Name", "Count"],
            [
                ["alpha", "1234567"],
                ["beta", "42"]
            ]);

        var rendered = Hex1bHumanRenderer.FormatDataTable(table, isQueryResultTable: true);

        Assert.Contains("1,234,567", rendered, StringComparison.Ordinal);
        Assert.Contains("42", rendered, StringComparison.Ordinal);
        // Verify right-alignment: "42" should have leading spaces
        Assert.Contains("       42", rendered, StringComparison.Ordinal);
    }
}
