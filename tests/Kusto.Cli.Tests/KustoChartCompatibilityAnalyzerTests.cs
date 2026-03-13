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

    [Fact]
    public void Analyze_GroupedChartWithMissingSeriesPoint_IsRejected()
    {
        var table = new TabularData(
            ["Month", "Series", "Value"],
            [
                ["Jan", "A", "1"],
                ["Feb", "A", "2"],
                ["Jan", "B", "3"]
            ]);
        var visualization = new QueryVisualization
        {
            Visualization = "linechart",
            XColumn = "Month",
            Series = ["Series"],
            YColumns = ["Value"]
        };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.Null(result.HumanChart);
        Assert.Null(result.MarkdownChart);
        Assert.Contains("missing X/series combinations", result.HumanReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_StackedLineChart_PreservesHumanLayout()
    {
        var table = new TabularData(
            ["Month", "Revenue", "Expenses"],
            [
                ["Jan", "10", "8"],
                ["Feb", "11", "7"]
            ]);
        var visualization = new QueryVisualization
        {
            Visualization = "linechart",
            XColumn = "Month",
            YColumns = ["Revenue", "Expenses"],
            Kind = "stacked"
        };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.Equal(QueryChartLayout.Stacked, result.HumanChart!.Layout);
        Assert.Null(result.MarkdownChart);
    }

    [Fact]
    public void Analyze_GroupedChart_WithCaseDistinctSeriesValues_KeepsSeriesSeparate()
    {
        var table = new TabularData(
            ["Category", "Series", "Value"],
            [
                ["A", "X", "1"],
                ["A", "x", "2"]
            ]);
        var visualization = new QueryVisualization
        {
            Visualization = "columnchart",
            XColumn = "Category",
            Series = ["Series"],
            YColumns = ["Value"]
        };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.Equal(["X", "x"], result.HumanChart!.Series.Select(series => series.Name).ToArray());
    }
}
