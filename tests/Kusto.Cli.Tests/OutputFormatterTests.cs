namespace Kusto.Cli.Tests;

public sealed class OutputFormatterTests
{
    [Fact]
    public void FormatHuman_TableOutput_IncludesHeadersAndRows()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(
                ["TableName", "RowCount"],
                [
                    ["DotnetEvents", "42"],
                    ["DotnetMetrics", "7"]
                ])
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("TableName", rendered, StringComparison.Ordinal);
        Assert.Contains("RowCount", rendered, StringComparison.Ordinal);
        Assert.Contains("DotnetEvents", rendered, StringComparison.Ordinal);
        Assert.Contains("DotnetMetrics", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHuman_QueryTableOutput_UsesReadableTabularLayout()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            IsQueryResultTable = true,
            Table = new TabularData(
                ["Name", "Count"],
                [
                    ["alpha", "42"],
                    ["beta", "7"]
                ])
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("Name", rendered, StringComparison.Ordinal);
        Assert.Contains("Count", rendered, StringComparison.Ordinal);
        Assert.Contains("alpha", rendered, StringComparison.Ordinal);
        Assert.Contains("beta", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHuman_PropertiesOutput_RendersPropertyTable()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Properties = new Dictionary<string, string?>
            {
                ["Name"] = "DDTelInsights",
                ["Default"] = "true"
            }
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("Property", rendered, StringComparison.Ordinal);
        Assert.Contains("Value", rendered, StringComparison.Ordinal);
        Assert.Contains("Name", rendered, StringComparison.Ordinal);
        Assert.Contains("DDTelInsights", rendered, StringComparison.Ordinal);
    }
}
