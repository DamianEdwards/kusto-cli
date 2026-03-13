namespace Kusto.Cli.Tests;

public sealed class QueryValidatorTests
{
    [Theory]
    [InlineData("print 1")]
    [InlineData("StormEvents | take 5")]
    public void Validate_ValidQuery_DoesNotThrow(string query)
    {
        QueryValidator.Validate(query);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_EmptyQuery_ThrowsUserFacingException(string query)
    {
        var exception = Assert.Throws<UserFacingException>(() => QueryValidator.Validate(query));
        Assert.Contains("cannot be empty", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("StormEvents | where")]
    [InlineData("print(")]
    public void Validate_InvalidQuery_ThrowsUserFacingException(string query)
    {
        var exception = Assert.Throws<UserFacingException>(() => QueryValidator.Validate(query));
        Assert.Contains("The query is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_MultilineInvalidQuery_IncludesLineAndColumn()
    {
        var exception = Assert.Throws<UserFacingException>(() => QueryValidator.Validate("print 1\r\n| where"));
        Assert.Contains("at line 2, column 8", exception.Message, StringComparison.Ordinal);
    }
}
