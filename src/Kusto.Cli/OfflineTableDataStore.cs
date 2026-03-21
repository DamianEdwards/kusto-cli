using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

public sealed class OfflineTableDataStore(
    SchemaCacheSettingsResolver settingsResolver,
    ILogger<OfflineTableDataStore> logger)
{
    private const int CacheFormatVersion = 1;

    private readonly SchemaCacheSettingsResolver _settingsResolver = settingsResolver;
    private readonly ILogger<OfflineTableDataStore> _logger = logger;

    public async Task<DatabaseSchemaCacheEntry?> TryReadEntryAsync(
        KustoConfig config,
        string clusterUrl,
        string database,
        CancellationToken cancellationToken)
    {
        var normalizedClusterUrl = ClusterUtilities.NormalizeClusterUrl(clusterUrl);
        var cacheDirectory = _settingsResolver.ResolveCacheDirectory(config);
        var cachePath = BuildCachePath(cacheDirectory, normalizedClusterUrl, database);
        return await TryReadEntryFromPathAsync(cachePath, cancellationToken);
    }

    public async Task<IReadOnlyList<DatabaseSchemaCacheEntry>> ReadAllEntriesAsync(
        KustoConfig config,
        CancellationToken cancellationToken)
    {
        var cacheDirectory = _settingsResolver.ResolveCacheDirectory(config);
        if (!Directory.Exists(cacheDirectory))
        {
            return [];
        }

        var entries = new List<DatabaseSchemaCacheEntry>();
        foreach (var path in Directory.EnumerateFiles(cacheDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await TryReadEntryFromPathAsync(path, cancellationToken);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    public async Task WriteEntryAsync(
        KustoConfig config,
        DatabaseSchemaCacheEntry cacheEntry,
        CancellationToken cancellationToken)
    {
        var normalizedClusterUrl = ClusterUtilities.NormalizeClusterUrl(cacheEntry.ClusterUrl);
        var cacheDirectory = _settingsResolver.ResolveCacheDirectory(config);
        var cachePath = BuildCachePath(cacheDirectory, normalizedClusterUrl, cacheEntry.DatabaseName);
        var directory = Path.GetDirectoryName(cachePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        cacheEntry.CacheFormatVersion = CacheFormatVersion;
        cacheEntry.ClusterUrl = normalizedClusterUrl;
        cacheEntry.DatabaseName = cacheEntry.DatabaseName.Trim();
        cacheEntry.TableNotes = NormalizeNotes(cacheEntry.TableNotes);

        var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(cachePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    cacheEntry,
                    KustoJsonSerializerContext.Default.DatabaseSchemaCacheEntry,
                    cancellationToken);
            }

            File.Move(temporaryPath, cachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to write offline table data entry to {CachePath}.", cachePath);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    public Task DeleteEntryAsync(
        KustoConfig config,
        string clusterUrl,
        string database,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedClusterUrl = ClusterUtilities.NormalizeClusterUrl(clusterUrl);
        var cacheDirectory = _settingsResolver.ResolveCacheDirectory(config);
        var cachePath = BuildCachePath(cacheDirectory, normalizedClusterUrl, database);

        try
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete offline table data entry at {CachePath}.", cachePath);
        }

        return Task.CompletedTask;
    }

    private async Task<DatabaseSchemaCacheEntry?> TryReadEntryFromPathAsync(string cachePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(cachePath);
            var cacheEntry = await JsonSerializer.DeserializeAsync(
                stream,
                KustoJsonSerializerContext.Default.DatabaseSchemaCacheEntry,
                cancellationToken);

            if (cacheEntry is null ||
                cacheEntry.CacheFormatVersion != CacheFormatVersion ||
                string.IsNullOrWhiteSpace(cacheEntry.ClusterUrl) ||
                string.IsNullOrWhiteSpace(cacheEntry.DatabaseName))
            {
                return null;
            }

            cacheEntry.ClusterUrl = ClusterUtilities.NormalizeClusterUrl(cacheEntry.ClusterUrl);
            cacheEntry.DatabaseName = cacheEntry.DatabaseName.Trim();
            cacheEntry.TableNotes = NormalizeNotes(cacheEntry.TableNotes);
            return cacheEntry;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or UserFacingException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable offline table data entry at {CachePath}.", cachePath);
            return null;
        }
    }

    private static Dictionary<string, List<string>> NormalizeNotes(Dictionary<string, List<string>>? tableNotes)
    {
        var normalized = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (tableNotes is null)
        {
            return normalized;
        }

        foreach (var pair in tableNotes)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            var notes = pair.Value?
                .Where(note => !string.IsNullOrWhiteSpace(note))
                .Select(note => note.Trim())
                .ToList();

            if (notes is { Count: > 0 })
            {
                normalized[pair.Key.Trim()] = notes;
            }
        }

        return normalized;
    }

    private static string BuildCachePath(string cacheDirectory, string clusterUrl, string database)
    {
        var keyBytes = Encoding.UTF8.GetBytes($"database-schema:v{CacheFormatVersion}:{clusterUrl.ToLowerInvariant()}|{database.ToLowerInvariant()}");
        var hash = Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();
        return Path.Combine(cacheDirectory, $"{hash}.json");
    }

    private void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to clean up temporary offline table data file {TemporaryPath}.", temporaryPath);
        }
    }
}
