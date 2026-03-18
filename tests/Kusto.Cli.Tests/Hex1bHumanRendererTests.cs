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
}
