using System.Text;

namespace Kusto.Cli.Tests;

public sealed class QueryTextResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsInlineQuery()
    {
        var query = await QueryTextResolver.ResolveAsync("StormEvents | take 1", null, false, TextReader.Null, CancellationToken.None);
        Assert.Equal("StormEvents | take 1", query);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsFileQuery()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "StormEvents | count");

            var query = await QueryTextResolver.ResolveAsync(null, path, false, TextReader.Null, CancellationToken.None);
            Assert.Equal("StormEvents | count", query);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResolveAsync_ReturnsStdinWhenDashSpecified()
    {
        using var stdin = new StringReader("StormEvents | limit 10");
        var query = await QueryTextResolver.ResolveAsync("-", null, false, stdin, CancellationToken.None);
        Assert.Equal("StormEvents | limit 10", query);
    }

    [Fact]
    public void ParseFileReference_KeepsWindowsAbsolutePathWithoutRange()
    {
        var fileReference = QueryTextResolver.ParseFileReference(@"C:\queries\top-states.kql");

        Assert.Equal(@"C:\queries\top-states.kql", fileReference.Path);
        Assert.Null(fileReference.LineRange);
    }

    [Fact]
    public void ParseFileReference_KeepsWindowsDriveRelativePathWithoutRange()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fileReference = QueryTextResolver.ParseFileReference("D:queries\\top-states.kql");

        Assert.Equal("D:queries\\top-states.kql", fileReference.Path);
        Assert.Null(fileReference.LineRange);
    }

    [Fact]
    public void ParseFileReference_ParsesLineRange()
    {
        var fileReference = QueryTextResolver.ParseFileReference(@"C:\queries\top-states.kql:12-15");

        Assert.Equal(@"C:\queries\top-states.kql", fileReference.Path);
        Assert.Equal(new QueryLineRange(12, 15), fileReference.LineRange);
    }

    [Theory]
    [InlineData("query.kql:1")]
    [InlineData("query.kql:1-")]
    [InlineData("query.kql:1-a")]
    public void ParseFileReference_InvalidSyntax_ThrowsUserFacingException(string fileReference)
    {
        var exception = Assert.Throws<UserFacingException>(() => QueryTextResolver.ParseFileReference(fileReference));
        Assert.Equal("Query file range '" + fileReference[(fileReference.LastIndexOf(':') + 1)..] + "' is invalid. Use '<path>:<start>-<end>'.", exception.Message);
    }

    [Theory]
    [InlineData("query.kql:0-1")]
    [InlineData("query.kql:-1-2")]
    [InlineData("query.kql:1-0")]
    public void ParseFileReference_NonPositiveLineNumbers_ThrowsUserFacingException(string fileReference)
    {
        var exception = Assert.Throws<UserFacingException>(() => QueryTextResolver.ParseFileReference(fileReference));
        Assert.Equal("Query file line numbers must be positive integers.", exception.Message);
    }

    [Fact]
    public void ParseFileReference_EndBeforeStart_ThrowsUserFacingException()
    {
        var exception = Assert.Throws<UserFacingException>(() => QueryTextResolver.ParseFileReference("query.kql:5-2"));
        Assert.Equal("Query file range '5-2' is invalid. The end line must be greater than or equal to the start line.", exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsSpecifiedLineRangeFromFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                "let cutoff = ago(7d);",
                "StormEvents",
                "| where StartTime > cutoff",
                "| take 5"
            ]);

            var query = await QueryTextResolver.ResolveAsync(null, $"{path}:2-4", false, TextReader.Null, CancellationToken.None);
            Assert.Equal("StormEvents" + Environment.NewLine + "| where StartTime > cutoff" + Environment.NewLine + "| take 5", query);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResolveAsync_OutOfRangeLineSelection_ThrowsUserFacingException()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                "StormEvents",
                "| count"
            ]);

            var exception = await Assert.ThrowsAsync<UserFacingException>(() =>
                QueryTextResolver.ResolveAsync(null, $"{path}:2-4", false, TextReader.Null, CancellationToken.None));

            Assert.Equal($"Query file range '2-4' is out of range for '{path}', which has 2 lines.", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResolveAsync_ReturnsSpecifiedLineRangeFromUtf8BomFileWithMultibyteCharacters()
    {
        var path = Path.GetTempFileName();
        try
        {
            var fileContents = string.Join(
                Environment.NewLine,
                [
                    "let city = \"Qu\u00E9bec\";",
                    "let emoji = \"\uD83D\uDE00\";",
                    "StormEvents",
                    "| take 3"
                ]);

            await File.WriteAllTextAsync(
                path,
                fileContents,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                CancellationToken.None);

            var query = await QueryTextResolver.ResolveAsync(null, $"{path}:3-4", false, TextReader.Null, CancellationToken.None);
            Assert.Equal("StormEvents" + Environment.NewLine + "| take 3", query);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResolveAsync_PathContainingColon_IsReadAsPlainPathWhenFileExists()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "query:v2.kql");
        try
        {
            await File.WriteAllTextAsync(path, "StormEvents | count");

            var query = await QueryTextResolver.ResolveAsync(null, path, false, TextReader.Null, CancellationToken.None);
            Assert.Equal("StormEvents | count", query);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
