using System.CommandLine;

namespace Kusto.Cli.Tests;

[Collection("Console")]
public sealed class ExamplesCommandTests
{
    [Fact]
    public async Task Examples_WithSamplesAlias_ShowsSamplesInOptionalAliases()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var originalOutput = Console.Out;
        using var outputWriter = new StringWriter();
        Console.SetOut(outputWriter);

        try
        {
            var exitCode = await rootCommand.Parse(["samples"], new ParserConfiguration())
                .InvokeAsync();

            Assert.Equal(0, exitCode);
            var output = outputWriter.ToString();
            Assert.Contains("Optional aliases", output, StringComparison.Ordinal);
            Assert.Contains("samples", output, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }
}
