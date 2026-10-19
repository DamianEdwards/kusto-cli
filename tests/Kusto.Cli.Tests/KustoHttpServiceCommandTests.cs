namespace Kusto.Cli.Tests;

[Collection("Console")]
public sealed class KustoHttpServiceCommandTests
{
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task RunAsync_CompletionStatusControlsExitCodeAndResultOutput(bool hasErrors, int expectedExitCode)
    {
        var responseJson =
            $$"""
            [
              {
                "FrameType": "DataTable",
                "TableKind": "PrimaryResult",
                "Columns": [{ "ColumnName": "Value", "ColumnType": "string" }],
                "Rows": [["fixture-row"]]
              },
              { "FrameType": "DataSetCompletion", "HasErrors": {{(hasErrors ? "true" : "false")}} }
            ]
            """;
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);

        try
        {
            var exitCode = await CliRunner.RunAsync(
                "json",
                null,
                async (_, _) =>
                {
                    var result = await KustoHttpServiceTests.ExecuteResponseAsync(responseJson);
                    return new CliOutput { Table = result.Table, IsQueryResultTable = true };
                },
                CancellationToken.None);

            Assert.Equal(expectedExitCode, exitCode);
            if (hasErrors)
            {
                Assert.Equal(string.Empty, output.ToString());
                Assert.Contains("results must not be used", error.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.Contains("fixture-row", output.ToString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }
}
