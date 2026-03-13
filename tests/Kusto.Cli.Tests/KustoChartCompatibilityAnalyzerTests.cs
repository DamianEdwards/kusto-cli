namespace Kusto.Cli.Tests;

public sealed class KustoChartCompatibilityAnalyzerTests
{
    [Fact]
    public void Analyze_ColumnChart_WithSingleSeries_IsCompatibleForHumanAndMarkdown()
    {
        var table = new TabularData(
            ["State", "Count"],
            [
                ["TEXAS", "4701"],
                ["KANSAS", "3166"]
            ]);
        var visualization = new QueryVisualization
        {
            Visualization = "columnchart",
            XColumn = "State",
            YColumns = ["Count"],
            Title = "Top states"
        };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.NotNull(result.MarkdownChart);
        Assert.Equal(QueryChartKind.Column, result.HumanChart!.Kind);
        Assert.Equal(["TEXAS", "KANSAS"], result.HumanChart.Categories);
        Assert.Equal(["Count"], result.HumanChart.Series.Select(series => series.Name).ToArray());
    }

    [Fact]
    public void Analyze_PieChart_IsOnlyCompatibleForMarkdown()
    {
        var table = new TabularData(
            ["State", "Count"],
            [
                ["TEXAS", "4701"],
                ["KANSAS", "3166"]
            ]);
        var visualization = new QueryVisualization
        {
            Visualization = "piechart",
            XColumn = "State",
            YColumns = ["Count"],
            Title = "Top states"
        };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.Null(result.HumanChart);
        Assert.Contains("piechart", result.HumanReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.MarkdownChart);
        Assert.Equal(QueryChartKind.Pie, result.MarkdownChart!.Kind);
    }

    [Fact]
    public void Analyze_StackedColumnChart_IsNotCompatibleForMarkdown()
    {
        var table = new TabularData(
            ["Month", "Revenue", "Expenses"],
            [
                ["Jan", "10", "8"],
                ["Feb", "11", "7"]
            ]);
        var visualization = new QueryVisualization
        {
            Visualization = "columnchart",
            XColumn = "Month",
            YColumns = ["Revenue", "Expenses"],
            Kind = "stacked"
        };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.Equal(QueryChartLayout.Stacked, result.HumanChart!.Layout);
        Assert.Null(result.MarkdownChart);
        Assert.Contains("can't be represented faithfully", result.MarkdownReason, StringComparison.OrdinalIgnoreCase);
    }
}
