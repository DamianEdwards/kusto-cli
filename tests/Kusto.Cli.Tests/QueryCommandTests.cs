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

    [Theory]
    [InlineData("csv")]
    [InlineData("tsv")]
    public async Task Query_WithDelimitedFormatAndChart_ReturnsError(string format)
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var originalError = Console.Error;
        using var errorWriter = new StringWriter();
        Console.SetError(errorWriter);

        try
        {
            var exitCode = await rootCommand.Parse(["--format", format, "query", "print 1", "--chart"], new ParserConfiguration())
                .InvokeAsync();

            Assert.Equal(1, exitCode);
            Assert.Contains($"--chart can't be used with --format {format}.", errorWriter.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("tsv")]
    public async Task Query_WithDelimitedFormatAndShowStats_ReturnsError(string format)
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var originalError = Console.Error;
        using var errorWriter = new StringWriter();
        Console.SetError(errorWriter);

        try
        {
            var exitCode = await rootCommand.Parse(["--format", format, "query", "print 1", "--show-stats"], new ParserConfiguration())
                .InvokeAsync();

            Assert.Equal(1, exitCode);
            Assert.Contains($"--show-stats can't be used with --format {format}.", errorWriter.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("tsv")]
    public async Task ClusterList_WithDelimitedFormat_ReturnsError(string format)
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var originalError = Console.Error;
        using var errorWriter = new StringWriter();
        Console.SetError(errorWriter);

        try
        {
            var exitCode = await rootCommand.Parse(["cluster", "list", "--format", format], new ParserConfiguration())
                .InvokeAsync();

            Assert.Equal(1, exitCode);
            Assert.Contains($"'{format}' is not supported for this command.", errorWriter.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }
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
