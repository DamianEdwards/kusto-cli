using System.CommandLine;

namespace Kusto.Cli.Tests;

public sealed class ParserTests
{
    [Fact]
    public void Parse_AllowsMarkdownAlias()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["cluster", "list", "--format", "md"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_RejectsUnknownFormat()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["cluster", "list", "--format", "csv"], new ParserConfiguration());
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("trace")]
    [InlineData("Warning")]
    [InlineData("Critical")]
    public void ParseLogLevelToken_AcceptsValidValues(string value)
    {
        var parsed = CliRunner.ParseLogLevelToken(value);
        Assert.NotNull(parsed);
    }

    [Fact]
    public void Parse_DatabaseList_AcceptsFilterAndTake()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["database", "list", "--filter", "^DD", "--take", "10"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_TableList_AcceptsFilterAndTake()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["table", "list", "--filter", "Events$", "--take", "25"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_Query_AcceptsShowStats()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["query", "print 1", "--show-stats"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }
}
