namespace Kusto.Cli.Tests;

[Collection("Console")]
public sealed class ConfirmationPromptTests
{
    [Fact]
    public async Task ConfirmAsync_WritesPromptToProvidedWriter()
    {
        using var input = new StringReader("y" + Environment.NewLine);
        using var output = new StringWriter();
        var prompt = new ConsoleConfirmationPrompt(input, output);

        var confirmed = await prompt.ConfirmAsync("Clear offline table data?", CancellationToken.None);

        Assert.True(confirmed);
        Assert.Contains("Clear offline table data? [y/N]", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateRuntime_UsesConfiguredStderrWriterForConfirmationPrompts()
    {
        var originalInput = Console.In;
        using var stderr = new StringWriter();

        try
        {
            Console.SetIn(new StringReader("n" + Environment.NewLine));
            using var runtime = CliRunner.CreateRuntime(null, stderrWriter: stderr);

            var confirmed = await runtime.ConfirmationPrompt.ConfirmAsync("Clear all notes?", CancellationToken.None);

            Assert.False(confirmed);
            Assert.Contains("Clear all notes? [y/N]", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetIn(originalInput);
        }
    }
}
