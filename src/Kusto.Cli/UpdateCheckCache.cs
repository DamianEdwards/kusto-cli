using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

internal sealed class UpdateCheckCache(string path, ILogger logger)
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    public bool IsDue(DateTimeOffset now)
    {
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(path);
            return now.UtcDateTime - lastWrite >= CheckInterval
                || lastWrite > now.UtcDateTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not read update check timestamp");
            return false;
        }
    }

    public string? ReadVersion()
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Trim();
            if (text.Length == 0)
            {
                return null;
            }

            if (VersionHelper.TryParse(text, out var version))
            {
                return version.ToNormalizedString();
            }

            logger.LogDebug("Ignoring malformed update check cache at {Path}", path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not read update check cache");
        }

        return null;
    }

    public FileStream? TryAcquireLock()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Keep the lock file in place: unlinking it can let Unix writers lock different inodes.
            return new FileStream(
                path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Update check cache is busy or inaccessible");
            return null;
        }
    }

    public bool Save(string? version, DateTimeOffset now)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, version ?? "");
            File.SetLastWriteTimeUtc(temporaryPath, now.UtcDateTime);
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not write update check cache");
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not remove temporary update check cache");
            }
        }
    }

    public void Clear()
    {
        using var lease = TryAcquireLock();
        if (lease is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not reset update check cache");
        }
    }

    public async Task<UpdateCheckResult?> CheckAsync(
        Func<CancellationToken, Task<UpdateCheckResult?>> check,
        bool background,
        CancellationToken cancellationToken)
    {
        using var lease = TryAcquireLock();
        if (background && (lease is null || !IsDue(DateTimeOffset.UtcNow)))
        {
            return null;
        }

        // Record the attempt before contacting the server, retaining a known update on failure.
        if (lease is not null && !Save(ReadVersion(), DateTimeOffset.UtcNow) && background)
        {
            return null;
        }

        var result = await check(cancellationToken);
        if (lease is not null)
        {
            Save(result?.AvailableVersion, DateTimeOffset.UtcNow);
        }

        return result;
    }
}
