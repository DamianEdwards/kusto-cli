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
    public void Parse_Examples_AcceptsAlias()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["example"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_ClusterAliases_AcceptPluralListAndUse()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["clusters", "use", "help"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_DatabaseCommandAlias_IsAccepted()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["db", "use", "Samples", "--cluster", "help"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_TableAliases_AcceptPluralLsDbAndLimit()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["tables", "ls", "--cluster", "help", "--db", "Samples", "--limit", "10"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_TableSchemaAlias_IsAccepted()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["table", "schema", "StormEvents", "--cluster", "help", "--db", "Samples"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_QueryAliases_AcceptRunAndFileAlias()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["run", "-f", "query.kql", "--cluster", "help", "--db", "Samples"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_Query_AcceptsShowStats()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["query", "print 1", "--show-stats"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_Query_AcceptsChart()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["query", "print 1", "--chart"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_ClusterAdd_AcceptsUseFlag()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["cluster", "add", "help", "https://help.kusto.windows.net/", "--use"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_ExamplesAliases_AcceptsAliasesCommand()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["aliases"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }
}
