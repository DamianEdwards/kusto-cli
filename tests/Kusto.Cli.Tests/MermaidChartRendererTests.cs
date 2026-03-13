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

    [Fact]
    public void Render_SanitizesControlCharactersInTitlesAndLabels()
    {
        var chart = new QueryChartDefinition
        {
            Kind = QueryChartKind.Column,
            Title = "Top\r\nstates",
            Categories = ["NEW\r\nYORK", "KANSAS\tCITY"],
            Series = [new QueryChartSeries("Count", [4701, 3166])]
        };

        var rendered = MermaidChartRenderer.Render(chart);

        Assert.Contains("title \"Top  states\"", rendered, StringComparison.Ordinal);
        Assert.Contains("x-axis [\"NEW  YORK\", \"KANSAS CITY\"]", rendered, StringComparison.Ordinal);
    }
}
