namespace Kusto.Cli.Tests;

public sealed class ConsoleStylingTests
{
    [Fact]
    public void ColorizeError_WithAnsiEnabled_UsesRed()
    {
        var rendered = ConsoleStyle.ColorizeError("boom", useAnsi: true);
        Assert.StartsWith("\u001b[31m", rendered, StringComparison.Ordinal);
        Assert.EndsWith("\u001b[0m", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorizeError_WithAnsiDisabled_ReturnsPlainText()
    {
        var rendered = ConsoleStyle.ColorizeError("boom", useAnsi: false);
        Assert.Equal("boom", rendered);
    }

    [Fact]
    public void ColorizeLog_WithAnsiEnabled_UsesLightGray()
    {
        var rendered = ConsoleStyle.ColorizeLog("line", useAnsi: true);
        Assert.StartsWith("\u001b[37m", rendered, StringComparison.Ordinal);
        Assert.EndsWith("\u001b[0m", rendered, StringComparison.Ordinal);
    }
}
