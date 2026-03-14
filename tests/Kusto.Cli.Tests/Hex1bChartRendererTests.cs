namespace Kusto.Cli.Tests;

public sealed class Hex1bChartRendererTests
{
    [Fact]
    public async Task RenderAsync_ColumnChart_RendersTitleAndLabels()
    {
        var chart = new QueryChartDefinition
        {
            Kind = QueryChartKind.Column,
            Title = "Top states",
            Categories = ["TEXAS", "KANSAS"],
            Series =
            [
                new QueryChartSeries("Count", [4701, 3166])
            ]
        };

        var rendered = await Hex1bChartRenderer.RenderAsync(chart, CancellationToken.None);

        Assert.Contains("Top states", rendered, StringComparison.Ordinal);
        Assert.Contains("TEXAS", rendered, StringComparison.Ordinal);
        Assert.Contains("KANSAS", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_PieChart_RendersTitleLegendAndPercentages()
    {
        var chart = new QueryChartDefinition
        {
            Kind = QueryChartKind.Pie,
            Title = "Top states",
            Categories = ["TEXAS", "KANSAS"],
            Series =
            [
                new QueryChartSeries("Count", [60, 40])
            ]
        };

        var rendered = await Hex1bChartRenderer.RenderAsync(chart, CancellationToken.None);

        Assert.Contains("Top states", rendered, StringComparison.Ordinal);
        Assert.Contains("TEXAS", rendered, StringComparison.Ordinal);
        Assert.Contains("KANSAS", rendered, StringComparison.Ordinal);
        Assert.Contains("%", rendered, StringComparison.Ordinal);
    }
}
