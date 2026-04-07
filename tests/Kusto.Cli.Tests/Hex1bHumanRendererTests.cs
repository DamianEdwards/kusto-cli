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
    public void FormatDataTable_QueryResults_FormatsLargeIntegersWithThousandSeparators()
    {
        var table = new TabularData(
            ["Day", "RowCount"],
            [
                ["2026-02-27T00:00:00Z", "66380993"],
                ["2026-02-28T00:00:00Z", "19639740"]
            ]);

        var rendered = Hex1bHumanRenderer.FormatDataTable(table, isQueryResultTable: true);

        Assert.Contains("66,380,993", rendered, StringComparison.Ordinal);
        Assert.Contains("19,639,740", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDataTable_QueryResults_StripsMidnightTimeFromDatetimes()
    {
        var table = new TabularData(
            ["Day", "RowCount"],
            [
                ["2026-02-27T00:00:00Z", "100"],
                ["2026-03-01T00:00:00Z", "200"]
            ]);

        var rendered = Hex1bHumanRenderer.FormatDataTable(table, isQueryResultTable: true);

        Assert.Contains("2026-02-27", rendered, StringComparison.Ordinal);
        Assert.Contains("2026-03-01", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("T00:00:00Z", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDataTable_QueryResults_PreservesNonMidnightDatetimeWithReadableFormat()
    {
        var table = new TabularData(
            ["Timestamp", "Value"],
            [
                ["2026-02-27T14:30:00Z", "42"]
            ]);

        var rendered = Hex1bHumanRenderer.FormatDataTable(table, isQueryResultTable: true);

        Assert.Contains("2026-02-27 14:30:00Z", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("T14:30:00Z", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDataTable_NonQueryTable_DoesNotFormatValues()
    {
        var table = new TabularData(
            ["Name", "Count"],
            [
                ["DotnetEvents", "66380993"]
            ]);

        var rendered = Hex1bHumanRenderer.FormatDataTable(table, isQueryResultTable: false);

        Assert.Contains("66380993", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("66,380,993", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("hello", "hello")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    public void FormatCellValueForDisplay_NonNumericNonDatetime_ReturnsUnchanged(string? input, string? expected)
    {
        Assert.Equal(expected, Hex1bHumanRenderer.FormatCellValueForDisplay(input));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("42", "42")]
    [InlineData("1000", "1,000")]
    [InlineData("66380993", "66,380,993")]
    [InlineData("-66380993", "-66,380,993")]
    [InlineData("9999999999", "9,999,999,999")]
    public void FormatCellValueForDisplay_Integers_FormatsWithThousandSeparators(string input, string expected)
    {
        Assert.Equal(expected, Hex1bHumanRenderer.FormatCellValueForDisplay(input));
    }

    [Theory]
    [InlineData("1.5", "1.5")]
    [InlineData("12345.67", "12,345.67")]
    [InlineData("0.123", "0.123")]
    [InlineData("-99999.99", "-99,999.99")]
    public void FormatCellValueForDisplay_Decimals_FormatsWithThousandSeparators(string input, string expected)
    {
        Assert.Equal(expected, Hex1bHumanRenderer.FormatCellValueForDisplay(input));
    }

    [Theory]
    [InlineData("001234", "001234")]
    [InlineData("0123", "0123")]
    [InlineData("00", "00")]
    public void FormatCellValueForDisplay_LeadingZeroIntegers_ReturnsUnchanged(string input, string expected)
    {
        Assert.Equal(expected, Hex1bHumanRenderer.FormatCellValueForDisplay(input));
    }

    [Theory]
    [InlineData("2026-02-27T00:00:00Z", "2026-02-27")]
    [InlineData("2026-03-01T00:00:00Z", "2026-03-01")]
    [InlineData("2026-02-27T00:00:00.0000000Z", "2026-02-27")]
    public void FormatCellValueForDisplay_MidnightDatetime_ReturnsDateOnly(string input, string expected)
    {
        Assert.Equal(expected, Hex1bHumanRenderer.FormatCellValueForDisplay(input));
    }

    [Theory]
    [InlineData("2026-02-27T14:30:00Z", "2026-02-27 14:30:00Z")]
    [InlineData("2026-02-27T14:30:00.1234567Z", "2026-02-27 14:30:00.1234567Z")]
    [InlineData("2026-02-27T14:30:00+05:00", "2026-02-27 14:30:00+05:00")]
    public void FormatCellValueForDisplay_NonMidnightDatetime_ReplaceTWithSpace(string input, string expected)
    {
        Assert.Equal(expected, Hex1bHumanRenderer.FormatCellValueForDisplay(input));
    }

    [Theory]
    [InlineData("1.23E+10")]
    [InlineData("not-a-date")]
    [InlineData("00:05:30")]
    public void FormatCellValueForDisplay_UnrecognizedFormats_ReturnsUnchanged(string input)
    {
        Assert.Equal(input, Hex1bHumanRenderer.FormatCellValueForDisplay(input));
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
}
