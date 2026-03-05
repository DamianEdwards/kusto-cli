using Microsoft.Extensions.Logging;

namespace Kusto.Cli.Tests;

public sealed class KustoConsoleFormatterTests
{
    [Fact]
    public void BuildLine_IncludesLogMetadataAndMessage()
    {
        var line = KustoConsoleFormatter.BuildLine(LogLevel.Warning, "kusto.test", "message", null);
        Assert.Contains("[Warning]", line, StringComparison.Ordinal);
        Assert.Contains("kusto.test", line, StringComparison.Ordinal);
        Assert.Contains("message", line, StringComparison.Ordinal);
    }
}
