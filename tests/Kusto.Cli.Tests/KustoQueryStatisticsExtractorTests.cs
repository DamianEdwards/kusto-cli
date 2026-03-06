namespace Kusto.Cli.Tests;

public sealed class KustoQueryStatisticsExtractorTests
{
    [Fact]
    public void Extract_FromStatusDescriptionStats_NormalizesFields()
    {
        const string statsPayload =
            """
            {
              "ExecutionTime": 1.234,
              "resource_usage": {
                "cpu": {
                  "total cpu": "00:00:01.5",
                  "breakdown": {
                    "query execution": "00:00:01.2",
                    "query planning": "00:00:00.3"
                  }
                },
                "memory": { "peak_per_node": 47395635 },
                "cache": { "shards": { "hot": { "hitbytes": 126353408, "missbytes": 3355443 } } },
                "network": { "cross_cluster_total_bytes": 5452595, "inter_cluster_total_bytes": 0 }
              },
              "input_dataset_statistics": {
                "extents": { "scanned": 42, "total": 1000 },
                "rows": { "scanned": 50000, "total": 1000000 }
              },
              "dataset_statistics": [
                { "table_row_count": 150, "table_size": 12800 }
              ],
              "cross_cluster_resource_usage": {
                "https://clustername.region.kusto.windows.net/": {
                  "cpu": { "total cpu": "00:00:00.8" },
                  "memory": { "peak_per_node": 23173530 },
                  "cache": { "shards": { "hot": { "hitbytes": 52428800, "missbytes": 1048576 } } }
                }
              }
            }
            """;

        var tables = new[]
        {
            new ParsedKustoTable(
                "QueryCompletionInformation",
                "QueryCompletionInformation",
                ["SeverityName", "StatusDescription"],
                [
                    new string?[] { "Info", "Query completed" },
                    new string?[] { "Stats", statsPayload }
                ])
        };

        var result = KustoQueryStatisticsExtractor.Extract(tables);

        Assert.NotNull(result);
        Assert.Equal(1.234, result.ExecutionTimeSec);
        Assert.Equal("00:00:01.5", result.Cpu?.Total);
        Assert.Equal("00:00:01.2", result.Cpu?.QueryExecution);
        Assert.Equal("00:00:00.3", result.Cpu?.QueryPlanning);
        Assert.Equal(45.2, result.MemoryPeakPerNodeMb);
        Assert.Equal(120.5, result.Cache?.HotHitMb);
        Assert.Equal(3.2, result.Cache?.HotMissMb);
        Assert.Equal(5.2, result.Network?.CrossClusterMb);
        Assert.Equal(0, result.Network?.InterClusterMb);
        Assert.Equal(42, result.Extents?.Scanned);
        Assert.Equal(1000, result.Extents?.Total);
        Assert.Equal(50000, result.Rows?.Scanned);
        Assert.Equal(1000000, result.Rows?.Total);
        Assert.Equal(150, result.Result?.RowCount);
        Assert.Equal(12.5, result.Result?.SizeKb);
        Assert.Equal("00:00:00.8", result.CrossClusterBreakdown?["clustername.region.kusto.windows.net"].CpuTotal);
        Assert.Equal(22.1, result.CrossClusterBreakdown?["clustername.region.kusto.windows.net"].MemoryPeakMb);
        Assert.Equal(50, result.CrossClusterBreakdown?["clustername.region.kusto.windows.net"].CacheHitMb);
        Assert.Equal(1, result.CrossClusterBreakdown?["clustername.region.kusto.windows.net"].CacheMissMb);
    }

    [Fact]
    public void Extract_FromPayloadRow_NormalizesFields()
    {
        var tables = new[]
        {
            new ParsedKustoTable(
                "QueryCompletionInformation",
                "QueryCompletionInformation",
                ["EventTypeName", "Payload"],
                [
                    new string?[] { "QueryResourceConsumption", "{\"ExecutionTime\":2.5}" }
                ])
        };

        var result = KustoQueryStatisticsExtractor.Extract(tables);

        Assert.NotNull(result);
        Assert.Equal(2.5, result.ExecutionTimeSec);
    }

    [Fact]
    public void Extract_WhenStatisticsUnavailable_ReturnsNull()
    {
        var tables = new[]
        {
            new ParsedKustoTable(
                "QueryCompletionInformation",
                "QueryCompletionInformation",
                ["SeverityName", "StatusDescription"],
                [
                    new string?[] { "Info", "Query completed" }
                ])
        };

        Assert.Null(KustoQueryStatisticsExtractor.Extract(tables));
    }
}
