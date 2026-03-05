namespace Kusto.Cli;

internal static class ConsoleRendering
{
    public static bool ShouldUseAnsiForStandardOutput()
    {
        return Environment.UserInteractive && !Console.IsOutputRedirected;
    }

    public static bool ShouldUseAnsiForStandardError()
    {
        return Environment.UserInteractive && !Console.IsErrorRedirected;
    }
}

internal static class ConsoleStyle
{
    private const string Red = "\u001b[31m";
    private const string LightGray = "\u001b[37m";
    private const string Reset = "\u001b[0m";

    public static string ColorizeError(string message, bool useAnsi)
    {
        return useAnsi ? $"{Red}{message}{Reset}" : message;
    }

    public static string ColorizeLog(string message, bool useAnsi)
    {
        return useAnsi ? $"{LightGray}{message}{Reset}" : message;
    }
}

internal static class ConsoleOutput
{
    public static void WriteError(string message)
    {
        Console.Error.WriteLine(ConsoleStyle.ColorizeError(message, ConsoleRendering.ShouldUseAnsiForStandardError()));
    }
}
