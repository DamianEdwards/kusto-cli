using System.CommandLine;

namespace Kusto.Cli.Tests;

[Collection("Console")]
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

    [Fact]
    public async Task Query_WithInvalidSyntax_ReturnsValidationError()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var originalError = Console.Error;
        using var errorWriter = new StringWriter();
        Console.SetError(errorWriter);

        try
        {
            var exitCode = await rootCommand.Parse(
                    ["query", "StormEvents | where", "--cluster", "https://help.kusto.windows.net", "--database", "Samples"],
                    new ParserConfiguration())
                .InvokeAsync();

            Assert.Equal(1, exitCode);
            Assert.Contains("The query is invalid", errorWriter.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollectionDefinition;
