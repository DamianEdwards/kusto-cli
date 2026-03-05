namespace Kusto.Cli.Tests;

public sealed class ListQueryBuilderTests
{
    [Fact]
    public void Build_WithoutFilterOrTake_ReturnsBaseCommand()
    {
        var query = ListQueryBuilder.Build(".show databases | project DatabaseName", "DatabaseName", null, null);

        Assert.Equal(".show databases | project DatabaseName", query.Command);
        Assert.Empty(query.Parameters);
    }

    [Theory]
    [InlineData("abc", "contains")]
    [InlineData("^abc", "startswith")]
    [InlineData("abc$", "endswith")]
    public void Build_WithFilter_UsesExpectedOperator(string filter, string expectedOperator)
    {
        var query = ListQueryBuilder.Build(".show databases | project DatabaseName", "DatabaseName", filter, null);

        Assert.Contains($"DatabaseName {expectedOperator} 'abc'", query.Command, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(query.Parameters);
    }

    [Fact]
    public void Build_WithStartAndEndAnchors_UsesBothOperators()
    {
        var query = ListQueryBuilder.Build(".show tables | project TableName", "TableName", "^exact$", null);

        Assert.Contains("TableName startswith 'exact'", query.Command, StringComparison.Ordinal);
        Assert.Contains("TableName endswith 'exact'", query.Command, StringComparison.Ordinal);
        Assert.Empty(query.Parameters);
    }

    [Fact]
    public void Build_FilterEscapesSingleQuotesInParameterLiteral()
    {
        var query = ListQueryBuilder.Build(".show tables | project TableName", "TableName", "O'Brien", null);
        Assert.Contains("contains 'O''Brien'", query.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithTake_AddsParameterizedTakeClause()
    {
        var query = ListQueryBuilder.Build(".show tables | project TableName", "TableName", null, 5);

        Assert.Contains("| take 5", query.Command, StringComparison.Ordinal);
        Assert.Empty(query.Parameters);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("^")]
    [InlineData("$")]
    [InlineData("^$")]
    [InlineData("a^bc")]
    [InlineData("ab$c")]
    public void Build_InvalidFilter_ThrowsUserFacingException(string filter)
    {
        var exception = Assert.Throws<UserFacingException>(() =>
            ListQueryBuilder.Build(".show databases | project DatabaseName", "DatabaseName", filter, null));

        Assert.Contains("--filter", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_InvalidTake_ThrowsUserFacingException(int take)
    {
        var exception = Assert.Throws<UserFacingException>(() =>
            ListQueryBuilder.Build(".show tables | project TableName", "TableName", null, take));

        Assert.Contains("--take", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
