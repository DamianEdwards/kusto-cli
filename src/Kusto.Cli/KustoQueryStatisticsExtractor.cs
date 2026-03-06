using System.Globalization;
using System.Text.Json;

namespace Kusto.Cli;

internal static class KustoQueryStatisticsExtractor
{
    public static QueryStatistics? Extract(IReadOnlyList<ParsedKustoTable> tables)
    {
        foreach (var table in tables)
        {
            if (!string.Equals(table.TableKind, "QueryCompletionInformation", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryExtractPayload(table, out var payload))
            {
                return Normalize(payload);
            }
        }

        return null;
    }

    private static bool TryExtractPayload(ParsedKustoTable table, out JsonElement payload)
    {
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var severityName = GetRowValue(table, rowIndex, "SeverityName");
            if (string.Equals(severityName, "Stats", StringComparison.OrdinalIgnoreCase) &&
                TryParseJsonObject(GetRowValue(table, rowIndex, "StatusDescription"), out payload))
            {
                return true;
            }

            if (!TryParseJsonObject(GetRowValue(table, rowIndex, "Payload"), out payload))
            {
                continue;
            }

            var eventTypeName = GetRowValue(table, rowIndex, "EventTypeName");
            if (string.IsNullOrWhiteSpace(eventTypeName) ||
                string.Equals(eventTypeName, "Stats", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(eventTypeName, "QueryResourceConsumption", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(eventTypeName, "QueryCompletionInformation", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        payload = default;
        return false;
    }

    private static QueryStatistics? Normalize(JsonElement source)
    {
        var hasValue = false;

        QueryCpuStatistics? cpu = null;
        double? memoryPeakPerNodeMb = null;
        QueryCacheStatistics? cache = null;
        QueryNetworkStatistics? network = null;
        QueryCountStatistics? extents = null;
        QueryCountStatistics? rows = null;
        QueryResultStatistics? result = null;
        Dictionary<string, QueryCrossClusterStatistics>? crossClusterBreakdown = null;

        if (TryGetObject(source, "resource_usage", out var resourceUsage))
        {
            cpu = ExtractCpu(resourceUsage);
            if (cpu is not null)
            {
                hasValue = true;
            }

            if (TryGetObject(resourceUsage, "memory", out var memory) &&
                TryGetDouble(memory, "peak_per_node", out var peakPerNodeBytes))
            {
                memoryPeakPerNodeMb = BytesToMegabytes(peakPerNodeBytes);
                hasValue = true;
            }

            cache = ExtractCache(resourceUsage);
            if (cache is not null)
            {
                hasValue = true;
            }

            network = ExtractNetwork(resourceUsage);
            if (network is not null)
            {
                hasValue = true;
            }
        }

        if (TryGetObject(source, "input_dataset_statistics", out var inputDatasetStatistics))
        {
            extents = ExtractCountStatistics(inputDatasetStatistics, "extents");
            if (extents is not null)
            {
                hasValue = true;
            }

            rows = ExtractCountStatistics(inputDatasetStatistics, "rows");
            if (rows is not null)
            {
                hasValue = true;
            }
        }

        if (TryGetArray(source, "dataset_statistics", out var datasetStatistics) &&
            datasetStatistics.GetArrayLength() > 0)
        {
            var firstDataset = datasetStatistics[0];
            if (firstDataset.ValueKind == JsonValueKind.Object)
            {
                int? rowCount = null;
                double? sizeKb = null;

                if (TryGetInt(firstDataset, "table_row_count", out var tableRowCount))
                {
                    rowCount = tableRowCount;
                }

                if (TryGetDouble(firstDataset, "table_size", out var tableSizeBytes))
                {
                    sizeKb = BytesToKilobytes(tableSizeBytes);
                }

                if (rowCount is not null || sizeKb is not null)
                {
                    result = new QueryResultStatistics
                    {
                        RowCount = rowCount,
                        SizeKb = sizeKb
                    };
                    hasValue = true;
                }
            }
        }

        if (TryGetObject(source, "cross_cluster_resource_usage", out var crossClusterResourceUsage))
        {
            foreach (var cluster in crossClusterResourceUsage.EnumerateObject())
            {
                if (cluster.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var clusterStatistics = ExtractCrossClusterStatistics(cluster.Value);
                if (clusterStatistics is null)
                {
                    continue;
                }

                var clusterName = NormalizeClusterName(cluster.Name);
                if (string.IsNullOrWhiteSpace(clusterName))
                {
                    continue;
                }

                crossClusterBreakdown ??= new Dictionary<string, QueryCrossClusterStatistics>(StringComparer.OrdinalIgnoreCase);
                crossClusterBreakdown[clusterName] = clusterStatistics;
                hasValue = true;
            }
        }

        double? executionTimeSec = null;
        if (TryGetDouble(source, "ExecutionTime", out var executionTime))
        {
            executionTimeSec = executionTime;
            hasValue = true;
        }

        return hasValue
            ? new QueryStatistics
            {
                ExecutionTimeSec = executionTimeSec,
                Cpu = cpu,
                MemoryPeakPerNodeMb = memoryPeakPerNodeMb,
                Cache = cache,
                Network = network,
                Extents = extents,
                Rows = rows,
                Result = result,
                CrossClusterBreakdown = crossClusterBreakdown
            }
            : null;
    }

    private static QueryCpuStatistics? ExtractCpu(JsonElement resourceUsage)
    {
        if (!TryGetObject(resourceUsage, "cpu", out var cpu))
        {
            return null;
        }

        var total = GetString(cpu, "total cpu");
        var queryExecution = default(string);
        var queryPlanning = default(string);

        if (TryGetObject(cpu, "breakdown", out var breakdown))
        {
            queryExecution = GetString(breakdown, "query execution");
            queryPlanning = GetString(breakdown, "query planning");
        }

        return string.IsNullOrWhiteSpace(total) &&
               string.IsNullOrWhiteSpace(queryExecution) &&
               string.IsNullOrWhiteSpace(queryPlanning)
            ? null
            : new QueryCpuStatistics
            {
                Total = total,
                QueryExecution = queryExecution,
                QueryPlanning = queryPlanning
            };
    }

    private static QueryCacheStatistics? ExtractCache(JsonElement resourceUsage)
    {
        if (!TryGetObject(resourceUsage, "cache", out var cache) ||
            !TryGetObject(cache, "shards", out var shards) ||
            !TryGetObject(shards, "hot", out var hot))
        {
            return null;
        }

        double? hotHitMb = null;
        double? hotMissMb = null;

        if (TryGetDouble(hot, "hitbytes", out var hitBytes))
        {
            hotHitMb = BytesToMegabytes(hitBytes);
        }

        if (TryGetDouble(hot, "missbytes", out var missBytes))
        {
            hotMissMb = BytesToMegabytes(missBytes);
        }

        return hotHitMb is null && hotMissMb is null
            ? null
            : new QueryCacheStatistics
            {
                HotHitMb = hotHitMb,
                HotMissMb = hotMissMb
            };
    }

    private static QueryNetworkStatistics? ExtractNetwork(JsonElement resourceUsage)
    {
        if (!TryGetObject(resourceUsage, "network", out var network))
        {
            return null;
        }

        double? crossClusterMb = null;
        double? interClusterMb = null;

        if (TryGetDouble(network, "cross_cluster_total_bytes", out var crossClusterBytes))
        {
            crossClusterMb = BytesToMegabytes(crossClusterBytes);
        }

        if (TryGetDouble(network, "inter_cluster_total_bytes", out var interClusterBytes))
        {
            interClusterMb = BytesToMegabytes(interClusterBytes);
        }

        return crossClusterMb is null && interClusterMb is null
            ? null
            : new QueryNetworkStatistics
            {
                CrossClusterMb = crossClusterMb,
                InterClusterMb = interClusterMb
            };
    }

    private static QueryCountStatistics? ExtractCountStatistics(JsonElement parent, string propertyName)
    {
        if (!TryGetObject(parent, propertyName, out var value))
        {
            return null;
        }

        int? scanned = null;
        int? total = null;

        if (TryGetInt(value, "scanned", out var scannedValue))
        {
            scanned = scannedValue;
        }

        if (TryGetInt(value, "total", out var totalValue))
        {
            total = totalValue;
        }

        return scanned is null && total is null
            ? null
            : new QueryCountStatistics
            {
                Scanned = scanned,
                Total = total
            };
    }

    private static QueryCrossClusterStatistics? ExtractCrossClusterStatistics(JsonElement clusterUsage)
    {
        string? cpuTotal = null;
        double? memoryPeakMb = null;
        double? cacheHitMb = null;
        double? cacheMissMb = null;

        if (TryGetObject(clusterUsage, "cpu", out var cpu))
        {
            cpuTotal = GetString(cpu, "total cpu");
        }

        if (TryGetObject(clusterUsage, "memory", out var memory) &&
            TryGetDouble(memory, "peak_per_node", out var peakPerNodeBytes))
        {
            memoryPeakMb = BytesToMegabytes(peakPerNodeBytes);
        }

        if (TryGetObject(clusterUsage, "cache", out var cache) &&
            TryGetObject(cache, "shards", out var shards) &&
            TryGetObject(shards, "hot", out var hot))
        {
            if (TryGetDouble(hot, "hitbytes", out var hitBytes))
            {
                cacheHitMb = BytesToMegabytes(hitBytes);
            }

            if (TryGetDouble(hot, "missbytes", out var missBytes))
            {
                cacheMissMb = BytesToMegabytes(missBytes);
            }
        }

        return string.IsNullOrWhiteSpace(cpuTotal) &&
               memoryPeakMb is null &&
               cacheHitMb is null &&
               cacheMissMb is null
            ? null
            : new QueryCrossClusterStatistics
            {
                CpuTotal = cpuTotal,
                MemoryPeakMb = memoryPeakMb,
                CacheHitMb = cacheHitMb,
                CacheMissMb = cacheMissMb
            };
    }

    private static string? GetRowValue(ParsedKustoTable table, int rowIndex, string columnName)
    {
        var columnIndex = GetColumnIndex(table.Columns, columnName);
        if (columnIndex < 0)
        {
            return null;
        }

        var row = table.Rows[rowIndex];
        return columnIndex < row.Count ? row[columnIndex] : null;
    }

    private static int GetColumnIndex(IReadOnlyList<string> columns, string columnName)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryParseJsonObject(string? rawValue, out JsonElement payload)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            payload = default;
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawValue);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                payload = default;
                return false;
            }

            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            payload = default;
            return false;
        }
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out value) &&
            value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var propertyValue))
        {
            if (propertyValue.ValueKind == JsonValueKind.Number)
            {
                value = propertyValue.GetDouble();
                return true;
            }

            if (propertyValue.ValueKind == JsonValueKind.String &&
                double.TryParse(propertyValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var propertyValue))
        {
            if (propertyValue.ValueKind == JsonValueKind.Number)
            {
                if (propertyValue.TryGetInt32(out value))
                {
                    return true;
                }

                value = (int)propertyValue.GetDouble();
                return true;
            }

            if (propertyValue.ValueKind == JsonValueKind.String &&
                double.TryParse(propertyValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                value = (int)parsed;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static double BytesToMegabytes(double value)
    {
        return Math.Round(value / 1048576d, 2);
    }

    private static double BytesToKilobytes(double value)
    {
        return Math.Round(value / 1024d, 2);
    }

    private static string NormalizeClusterName(string clusterIdentifier)
    {
        if (Uri.TryCreate(clusterIdentifier, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        var normalized = clusterIdentifier.Trim().TrimEnd('/');
        if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return normalized["https://".Length..];
        }

        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return normalized["http://".Length..];
        }

        return normalized;
    }
}
