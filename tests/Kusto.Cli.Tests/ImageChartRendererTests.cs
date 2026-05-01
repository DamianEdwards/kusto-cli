using SkiaSharp;

namespace Kusto.Cli.Tests;

public sealed class ImageChartRendererTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Theory]
    [InlineData((int)QueryChartKind.Column, (int)QueryChartLayout.Simple, false)]
    [InlineData((int)QueryChartKind.Column, (int)QueryChartLayout.Grouped, false)]
    [InlineData((int)QueryChartKind.Column, (int)QueryChartLayout.Stacked, false)]
    [InlineData((int)QueryChartKind.Column, (int)QueryChartLayout.Stacked100, false)]
    [InlineData((int)QueryChartKind.Bar, (int)QueryChartLayout.Simple, true)]
    [InlineData((int)QueryChartKind.Bar, (int)QueryChartLayout.Grouped, true)]
    [InlineData((int)QueryChartKind.Bar, (int)QueryChartLayout.Stacked, true)]
    [InlineData((int)QueryChartKind.Bar, (int)QueryChartLayout.Stacked100, true)]
    [InlineData((int)QueryChartKind.Line, (int)QueryChartLayout.Simple, false)]
    [InlineData((int)QueryChartKind.Line, (int)QueryChartLayout.Stacked, false)]
    [InlineData((int)QueryChartKind.Line, (int)QueryChartLayout.Stacked100, false)]
    public void RenderPng_CartesianKindAndLayout_ProducesValidPngWithRequestedDimensions(
        int kindValue,
        int layoutValue,
        bool horizontal)
    {
        var kind = (QueryChartKind)kindValue;
        var layout = (QueryChartLayout)layoutValue;

        var chart = new QueryChartDefinition
        {
            Kind = kind,
            Layout = layout,
            Horizontal = horizontal,
            Title = "Sample chart",
            XTitle = "Categories",
            YTitle = "Values",
            Categories = ["alpha", "beta", "gamma", "delta"],
            Series =
            [
                new QueryChartSeries("series-a", [10, 25, 15, 40]),
                new QueryChartSeries("series-b", [5, 18, 12, 22])
            ]
        };

        using var ms = new MemoryStream();
        ImageChartRenderer.RenderPng(chart, 800, 480, ms);

        AssertPng(ms, expectedWidth: 800, expectedHeight: 480);
    }

    [Fact]
    public void RenderPng_PieChart_ProducesValidPng()
    {
        var chart = new QueryChartDefinition
        {
            Kind = QueryChartKind.Pie,
            Title = "Distribution",
            Categories = ["one", "two", "three", "four"],
            Series = [new QueryChartSeries("share", [10, 20, 30, 40])]
        };

        using var ms = new MemoryStream();
        ImageChartRenderer.RenderPng(chart, 1000, 600, ms);

        AssertPng(ms, expectedWidth: 1000, expectedHeight: 600);
    }

    [Fact]
    public void RenderPng_DefaultDimensions_ProducesValidPng()
    {
        var chart = new QueryChartDefinition
        {
            Kind = QueryChartKind.Column,
            Title = "Default size",
            Categories = ["a", "b"],
            Series = [new QueryChartSeries("count", [3, 7])]
        };

        using var ms = new MemoryStream();
        ImageChartRenderer.RenderPng(chart, ChartStyle.DefaultWidth, ChartStyle.DefaultHeight, ms);

        AssertPng(ms, expectedWidth: ChartStyle.DefaultWidth, expectedHeight: ChartStyle.DefaultHeight);
    }

    [Fact]
    public void RenderPng_DimensionsOutOfRange_Throws()
    {
        var chart = new QueryChartDefinition
        {
            Kind = QueryChartKind.Column,
            Categories = ["a"],
            Series = [new QueryChartSeries("v", [1])]
        };

        using var ms = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() => ImageChartRenderer.RenderPng(chart, 50, 480, ms));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageChartRenderer.RenderPng(chart, 800, 50, ms));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageChartRenderer.RenderPng(chart, 99999, 480, ms));
    }

    private static void AssertPng(MemoryStream stream, int expectedWidth, int expectedHeight)
    {
        Assert.True(stream.Length > 100, "PNG output suspiciously small");

        var bytes = stream.ToArray();
        for (var i = 0; i < PngSignature.Length; i++)
        {
            Assert.Equal(PngSignature[i], bytes[i]);
        }

        stream.Position = 0;
        using var codec = SKCodec.Create(stream);
        Assert.NotNull(codec);
        Assert.Equal(expectedWidth, codec!.Info.Width);
        Assert.Equal(expectedHeight, codec.Info.Height);
    }
}
