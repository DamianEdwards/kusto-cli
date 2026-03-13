using System.CommandLine;

namespace Kusto.Cli.Tests;

public sealed class QueryCommandTests
{
    [Fact]
    public async Task Query_WithJsonFormatAndChart_ReturnsError()
    {
        var rootCommand = CommandFactory.CreateRootCommand();

        var exitCode = await rootCommand.Parse(["--format", "json", "query", "print 1", "--chart"], new ParserConfiguration())
            .InvokeAsync();

        Assert.Equal(1, exitCode);
    }
}
