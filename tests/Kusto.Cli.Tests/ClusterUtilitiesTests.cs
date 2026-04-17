namespace Kusto.Cli.Tests;

public sealed class ClusterUtilitiesTests
{
    [Theory]
    [InlineData("https://help.kusto.windows.net", "https://help.kusto.windows.net")]
    [InlineData("https://help.kusto.windows.net/", "https://help.kusto.windows.net")]
    [InlineData("https://help.kusto.windows.net/Samples", "https://help.kusto.windows.net")]
    [InlineData("HTTPS://Help.Kusto.Windows.Net", "https://help.kusto.windows.net")]
    public void NormalizeClusterUrl_ClassicAdxHost_ReturnsAuthorityOnly(string input, string expected)
    {
        Assert.Equal(expected, ClusterUtilities.NormalizeClusterUrl(input));
    }

    [Theory]
    [InlineData(
        "https://ade.applicationinsights.io/subscriptions/00000000-0000-0000-0000-000000000000/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws",
        "https://ade.applicationinsights.io/subscriptions/00000000-0000-0000-0000-000000000000/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws")]
    [InlineData(
        "https://ade.applicationinsights.io/subscriptions/sub/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws/",
        "https://ade.applicationinsights.io/subscriptions/sub/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws")]
    [InlineData(
        "https://ade.loganalytics.io/subscriptions/sub/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws",
        "https://ade.loganalytics.io/subscriptions/sub/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws")]
    [InlineData(
        "https://adx.applicationinsights.azure.com/subscriptions/sub/resourcegroups/rg/providers/microsoft.insights/components/app",
        "https://adx.applicationinsights.azure.com/subscriptions/sub/resourcegroups/rg/providers/microsoft.insights/components/app")]
    [InlineData(
        "https://adx.monitor.azure.com/subscriptions/sub/resourcegroups/rg/providers/microsoft.insights/components/app",
        "https://adx.monitor.azure.com/subscriptions/sub/resourcegroups/rg/providers/microsoft.insights/components/app")]
    public void NormalizeClusterUrl_AdeProxyHost_PreservesResourcePath(string input, string expected)
    {
        Assert.Equal(expected, ClusterUtilities.NormalizeClusterUrl(input));
    }

    [Fact]
    public void NormalizeClusterUrl_AdeProxyHost_StripsQueryAndFragment()
    {
        const string input = "https://ade.applicationinsights.io/subscriptions/sub/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws?foo=bar#frag";
        const string expected = "https://ade.applicationinsights.io/subscriptions/sub/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws";

        Assert.Equal(expected, ClusterUtilities.NormalizeClusterUrl(input));
    }

    [Fact]
    public void NormalizeClusterUrl_AdeProxyHostWithoutPath_ReturnsAuthorityOnly()
    {
        Assert.Equal(
            "https://ade.applicationinsights.io",
            ClusterUtilities.NormalizeClusterUrl("https://ade.applicationinsights.io"));
    }

    [Fact]
    public void NormalizeClusterUrl_InvalidUrl_Throws()
    {
        Assert.Throws<UserFacingException>(() => ClusterUtilities.NormalizeClusterUrl("not-a-url"));
    }

    [Theory]
    [InlineData("ade.applicationinsights.io")]
    [InlineData("ade.loganalytics.io")]
    [InlineData("adx.aimon.applicationinsights.azure.com")]
    [InlineData("adx.applicationinsights.azure.com")]
    [InlineData("adx.int.applicationinsights.azure.com")]
    [InlineData("adx.int.loganalytics.azure.com")]
    [InlineData("adx.int.monitor.azure.com")]
    [InlineData("adx.loganalytics.azure.com")]
    [InlineData("adx.monitor.azure.com")]
    [InlineData("kusto.aria.microsoft.com")]
    [InlineData("eu.kusto.aria.microsoft.com")]
    [InlineData("api.securityplatform.microsoft.com")]
    [InlineData("adx.applicationinsights.azure.us")]
    [InlineData("adx.loganalytics.azure.us")]
    [InlineData("adx.monitor.azure.us")]
    [InlineData("adx.applicationinsights.azure.cn")]
    [InlineData("adx.loganalytics.azure.cn")]
    [InlineData("adx.monitor.azure.cn")]
    [InlineData("adx.applicationinsights.azure.eaglex.ic.gov")]
    [InlineData("adx.loganalytics.azure.eaglex.ic.gov")]
    [InlineData("adx.monitor.azure.eaglex.ic.gov")]
    [InlineData("adx.applicationinsights.azure.microsoft.scloud")]
    [InlineData("adx.loganalytics.azure.microsoft.scloud")]
    [InlineData("adx.monitor.azure.microsoft.scloud")]
    [InlineData("adx.applicationinsights.azure.fr")]
    [InlineData("adx.loganalytics.azure.fr")]
    [InlineData("adx.monitor.azure.fr")]
    [InlineData("adx.applicationinsights.azure.de")]
    [InlineData("adx.loganalytics.azure.de")]
    [InlineData("adx.monitor.azure.de")]
    [InlineData("adx.applicationinsights.azure.sg")]
    [InlineData("adx.loganalytics.azure.sg")]
    [InlineData("adx.monitor.azure.sg")]
    public void NormalizeClusterUrl_AllKnownProxyHosts_PreservePath(string host)
    {
        var input = $"https://{host}/subscriptions/sub/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws";

        Assert.Equal(input, ClusterUtilities.NormalizeClusterUrl(input));
        Assert.True(ClusterUtilities.IsProxyHost(host));
    }

    [Fact]
    public void IsProxyHost_IsCaseInsensitive()
    {
        Assert.True(ClusterUtilities.IsProxyHost("ADE.ApplicationInsights.IO"));
    }

    [Fact]
    public void IsProxyHost_ReturnsFalseForClassicAdxCluster()
    {
        Assert.False(ClusterUtilities.IsProxyHost("help.kusto.windows.net"));
    }

    [Fact]
    public void FindKnownCluster_MatchesAdeProxyUrlByNormalizedFullPath()
    {
        var config = new KustoConfig
        {
            Clusters =
            [
                new KnownCluster
                {
                    Name = "workspace-cluster",
                    Url = "https://ade.applicationinsights.io/subscriptions/sub/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws",
                }
            ],
        };

        var match = ClusterUtilities.FindKnownCluster(
            config,
            "https://ade.applicationinsights.io/subscriptions/sub/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws/");

        Assert.NotNull(match);
        Assert.Equal("workspace-cluster", match!.Name);
    }

    [Fact]
    public void NormalizeConfig_PreservesAdeProxyUrlsForSavedClustersAndDefaults()
    {
        const string adeUrl = "https://ade.applicationinsights.io/subscriptions/sub/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws";
        var config = new KustoConfig
        {
            DefaultClusterUrl = adeUrl + "/",
            Clusters =
            [
                new KnownCluster { Name = "ws", Url = adeUrl + "/" }
            ],
            DefaultDatabases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [adeUrl + "/"] = "ws",
            },
        };

        var normalized = ClusterUtilities.NormalizeConfig(config);

        Assert.Equal(adeUrl, normalized.DefaultClusterUrl);
        Assert.Equal(adeUrl, normalized.Clusters[0].Url);
        Assert.True(normalized.DefaultDatabases.ContainsKey(adeUrl));
    }
}
