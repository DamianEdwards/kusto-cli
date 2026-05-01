namespace Kusto.Cli.Tests;

public sealed class ChartImageWriterTests : IDisposable
{
    private readonly string _tempDir;

    public ChartImageWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "kusto-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static QueryChartDefinition SampleChart() => new()
    {
        Kind = QueryChartKind.Column,
        Title = "T",
        Categories = ["a", "b"],
        Series = [new QueryChartSeries("v", [1, 2])]
    };

    [Fact]
    public async Task WritePngAsync_WritesFile_AndReturnsAbsolutePath()
    {
        var path = Path.Combine(_tempDir, "out.png");

        var written = await ChartImageWriter.WritePngAsync(
            SampleChart(),
            path,
            ChartStyle.DefaultWidth,
            ChartStyle.DefaultHeight,
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(path), written);
        Assert.True(File.Exists(written));
        Assert.True(new FileInfo(written).Length > 100);
        Assert.False(File.Exists(written + ".tmp"));
    }

    [Fact]
    public async Task WritePngAsync_CreatesParentDirectory()
    {
        var nested = Path.Combine(_tempDir, "sub", "dir", "chart.png");

        var written = await ChartImageWriter.WritePngAsync(
            SampleChart(),
            nested,
            ChartStyle.DefaultWidth,
            ChartStyle.DefaultHeight,
            CancellationToken.None);

        Assert.True(File.Exists(written));
    }

    [Fact]
    public async Task WritePngAsync_NonPngExtension_Throws()
    {
        var path = Path.Combine(_tempDir, "out.jpg");

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => ChartImageWriter.WritePngAsync(
            SampleChart(),
            path,
            ChartStyle.DefaultWidth,
            ChartStyle.DefaultHeight,
            CancellationToken.None));

        Assert.Contains(".png", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritePngAsync_PathIsDirectory_Throws()
    {
        await Assert.ThrowsAsync<UserFacingException>(() => ChartImageWriter.WritePngAsync(
            SampleChart(),
            _tempDir,
            ChartStyle.DefaultWidth,
            ChartStyle.DefaultHeight,
            CancellationToken.None));
    }

    [Fact]
    public async Task WritePngAsync_EmptyPath_Throws()
    {
        await Assert.ThrowsAsync<UserFacingException>(() => ChartImageWriter.WritePngAsync(
            SampleChart(),
            "",
            ChartStyle.DefaultWidth,
            ChartStyle.DefaultHeight,
            CancellationToken.None));
    }

    [Theory]
    [InlineData(50, 480)]
    [InlineData(800, 50)]
    [InlineData(99999, 480)]
    [InlineData(800, 99999)]
    public async Task WritePngAsync_DimensionOutOfRange_Throws(int width, int height)
    {
        var path = Path.Combine(_tempDir, "x.png");

        await Assert.ThrowsAsync<UserFacingException>(() => ChartImageWriter.WritePngAsync(
            SampleChart(),
            path,
            width,
            height,
            CancellationToken.None));
    }

    [Fact]
    public async Task WritePngAsync_OverwritesExistingFile()
    {
        var path = Path.Combine(_tempDir, "out.png");
        await File.WriteAllBytesAsync(path, new byte[] { 0, 1, 2 });

        var written = await ChartImageWriter.WritePngAsync(
            SampleChart(),
            path,
            ChartStyle.DefaultWidth,
            ChartStyle.DefaultHeight,
            CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(written);
        Assert.True(bytes.Length > 100);
        Assert.Equal(0x89, bytes[0]);
    }
}
