using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Kusto.Cli;

public sealed class KustoConsoleFormatter(IOptionsMonitor<KustoConsoleFormatterOptions> options) : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "kusto";

    private readonly KustoConsoleFormatterOptions _options = options.CurrentValue;

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrWhiteSpace(message) && logEntry.Exception is null)
        {
            return;
        }

        var line = BuildLine(logEntry.LogLevel, logEntry.Category, message, logEntry.Exception);
        var useAnsi = _options.UseAnsi && ConsoleRendering.ShouldUseAnsiForStandardError();
        textWriter.WriteLine(ConsoleStyle.ColorizeLog(line, useAnsi));
    }

    internal static string BuildLine(LogLevel logLevel, string categoryName, string? message, Exception? exception)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{logLevel}] {categoryName}: {message}";
        if (exception is not null)
        {
            line = $"{line}{Environment.NewLine}{exception}";
        }

        return line;
    }
}

public sealed class KustoConsoleFormatterOptions : ConsoleFormatterOptions
{
    public bool UseAnsi { get; set; } = true;
}
