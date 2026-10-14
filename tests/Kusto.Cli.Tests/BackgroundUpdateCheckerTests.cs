namespace Kusto.Cli.Tests;

public sealed class BackgroundUpdateCheckerTests
{
    [Theory]
    [InlineData(null, "1.0.0")]
    [InlineData("", "1.0.0")]
    [InlineData("invalid", "1.0.0")]
    [InlineData("2.0.0", "invalid")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("2.0.0+different-build", "2.0.0+installed-build")]
    public void CreateNotice_IgnoresMissingInvalidAndObsoleteVersions(string? available, string current)
    {
        Assert.Null(BackgroundUpdateChecker.CreateNotice(available, current));
    }

    [Theory]
    [InlineData("2.0.0", "kusto update")]
    [InlineData("2.0.0-preview.1", "kusto update --pre-release")]
    [InlineData("2.0.0-dev.1", "kusto update")]
    public void CreateNotice_IncludesVersionAndMatchingUpdateCommand(string available, string command)
    {
        var notice = BackgroundUpdateChecker.CreateNotice(available, "1.0.0-dev.1");
        Assert.Equal(
            $"A new version of kusto is available: {available}. Run '{command}' to install it.",
            notice);
    }

    [Fact]
    public void CreateStartInfo_IsSilentAndUsesBackgroundCheck()
    {
        var info = BackgroundUpdateChecker.CreateStartInfo();
        Assert.Equal(Environment.ProcessPath, info.FileName);
        Assert.Equal(["update", "--check", "--background"], info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
    }
}
