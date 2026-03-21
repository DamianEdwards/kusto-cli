namespace Kusto.Cli;

public sealed class ConsoleConfirmationPrompt(TextReader input, TextWriter output) : IConfirmationPrompt
{
    private readonly TextReader _input = input;
    private readonly TextWriter _output = output;

    public async Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        await _output.WriteAsync($"{prompt} [y/N] ");
        await _output.FlushAsync(cancellationToken);
        var response = await _input.ReadLineAsync(cancellationToken);
        return response is not null &&
               (string.Equals(response.Trim(), "y", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(response.Trim(), "yes", StringComparison.OrdinalIgnoreCase));
    }
}
