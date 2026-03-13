namespace Kusto.Cli.Tests;

public sealed class MermaidChartRendererTests
{
    [Fact]
    public void Render_CartesianChartWithoutAxisTitles_OmitsEmptyXAxisTitle()
    {
        var chart = new QueryChartDefinition
        {
            Kind = QueryChartKind.Column,
            Categories = ["TEXAS", "KANSAS"],
            Series = [new QueryChartSeries("Count", [4701, 3166])]
        };

        var rendered = MermaidChartRenderer.Render(chart);

        Assert.Contains("x-axis [\"TEXAS\", \"KANSAS\"]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("x-axis \"\"", rendered, StringComparison.Ordinal);
    }
}
