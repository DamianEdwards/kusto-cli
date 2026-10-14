using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

internal sealed class BackgroundUpdateChecker(UpdateCheckCache cache, ILogger logger)
{
    public static BackgroundUpdateChecker? Create(ILogger logger)
    {
        if (UpdateService.IsDisabled
            || Assembly.GetEntryAssembly() != typeof(BackgroundUpdateChecker).Assembly
            || string.IsNullOrWhiteSpace(Environment.ProcessPath)
            || string.Equals(
                Path.GetFileNameWithoutExtension(Environment.ProcessPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new BackgroundUpdateChecker(
            new UpdateCheckCache(AppPaths.GetUpdateCheckPath(), logger), logger);
    }

    public void TryStart()
    {
        if (!cache.IsDue(DateTimeOffset.UtcNow))
        {
            return;
        }

        try
        {
            _ = DrainAsync(Process.Start(CreateStartInfo()), logger);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Could not start background update check");
        }
    }

    internal static ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("update");
        startInfo.ArgumentList.Add("--check");
        startInfo.ArgumentList.Add("--background");
        return startInfo;
    }

    private static async Task DrainAsync(Process? process, ILogger logger)
    {
        if (process is null)
        {
            logger.LogDebug("Could not start background update check");
            return;
        }

        using (process)
        {
            try
            {
                process.StandardInput.Close();
                await Task.WhenAll(
                    process.StandardOutput.BaseStream.CopyToAsync(Stream.Null),
                    process.StandardError.BaseStream.CopyToAsync(Stream.Null));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                logger.LogDebug(ex, "Could not drain background update check output");
            }
        }
    }

    public string? GetNotice()
        => CreateNotice(cache.ReadVersion(), VersionHelper.GetCurrentVersion());

    internal static string? CreateNotice(string? availableText, string? currentText)
    {
        if (!VersionHelper.TryParse(availableText, out var available)
            || !VersionHelper.TryParse(currentText, out var current)
            || available <= current)
        {
            return null;
        }

        var command = available.IsPrerelease && !VersionHelper.IsDevBuild(available)
            ? "kusto update --pre-release"
            : "kusto update";
        return $"A new version of kusto is available: {available.ToNormalizedString()}. Run '{command}' to install it.";
    }
}
