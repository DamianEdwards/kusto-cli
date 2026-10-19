using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kusto.Cli.Tests;

public sealed class KustoHttpServiceTests
{
    [Fact]
    public async Task ExecuteManagementCommandAsync_SelectsPrimaryResultAndDeserializesPascalCasePayload()
    {
        const string responseJson =
            """
            {
              "Tables": [
                {
                  "TableName": "QueryStatus",
                  "TableKind": "QueryCompletionInformation",
                  "Columns": [
                    { "ColumnName": "Status", "DataType": "string" }
                  ],
                  "Rows": [
                    [ "Completed" ]
                  ]
                },
                {
                  "TableName": "Table_0",
                  "TableKind": "PrimaryResult",
                  "Columns": [
                    { "ColumnName": "DatabaseName", "DataType": "string" }
                  ],
                  "Rows": [
                    [ "Samples" ]
                  ]
                }
              ]
            }
            """;

        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var result = await service.ExecuteManagementCommandAsync(
            new ResolvedCluster(null, "https://help.kusto.windows.net"),
            null,
            ".show databases | project DatabaseName",
            null,
            CancellationToken.None);

        Assert.Equal(["DatabaseName"], result.Columns);
        Assert.Single(result.Rows);
        Assert.Equal("Samples", result.Rows[0][0]);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("fake-token", handler.LastAuthorizationParameter);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ParsesV2FrameArrayPayload()
    {
        const string responseJson =
            """
            [
              { "FrameType": "DataSetHeader", "IsProgressive": false },
              {
                "FrameType": "DataTable",
                "TableName": "PrimaryResult",
                "TableKind": "PrimaryResult",
                "Columns": [
                  { "ColumnName": "ValidationInline", "ColumnType": "long" }
                ],
                "Rows": [
                  [ 1 ]
                ]
              },
              { "FrameType": "DataSetCompletion", "HasErrors": false }
            ]
            """;

        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var result = await service.ExecuteQueryAsync(
            new ResolvedCluster(null, "https://help.kusto.windows.net"),
            "Samples",
            "print ValidationInline=1",
            includeStatistics: false,
            CancellationToken.None);

        Assert.Equal(["ValidationInline"], result.Table.Columns);
        Assert.Single(result.Table.Rows);
        Assert.Equal("1", result.Table.Rows[0][0]);
        Assert.StartsWith(
            "https://dataexplorer.azure.com/clusters/help.kusto.windows.net/databases/Samples?query=",
            result.WebExplorerUrl);
        Assert.Null(result.Statistics);
        Assert.Null(result.Visualization);
    }

    [Fact]
    public async Task ExecuteQueryAsync_OnCompletionError_RejectsPartialResults()
    {
        const string responseJson =
            """
            [
              {
                "FrameType": "DataTable",
                "TableKind": "PrimaryResult",
                "Columns": [{ "ColumnName": "Value", "ColumnType": "long" }],
                "Rows": [[1]]
              },
              { "FrameType": "DataSetCompletion", "HasErrors": true }
            ]
            """;

        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var exception = await Assert.ThrowsAsync<UserFacingException>(() =>
            service.ExecuteQueryAsync(
                new ResolvedCluster(null, "https://help.kusto.windows.net"),
                "Samples",
                "print Value=1",
                includeStatistics: false,
                CancellationToken.None));

        Assert.Contains("results", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteQueryAsync_OnInlineRowError_RejectsPartialResults()
    {
        const string responseJson =
            """
            [
              {
                "FrameType": "DataTable",
                "TableKind": "PrimaryResult",
                "Columns": [{ "ColumnName": "Value", "ColumnType": "long" }],
                "Rows": [[1], { "error": { "code": "E_RUNAWAY_QUERY", "message": "private-diagnostic" } }]
              }
            ]
            """;

        var exception = await Assert.ThrowsAsync<UserFacingException>(() => ExecuteResponseAsync(responseJson));

        Assert.Contains("results", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-diagnostic", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "FrameType": "DataSetCompletion", "HasErrors": false, "Cancelled": true }""")]
    [InlineData("""{ "frametype": "datasetcompletion", "haserrors": true }""")]
    [InlineData("""{ "FrameType": "DataSetCompletion", "HasErrors": false, "OneApiErrors": [{ "code": "E_QUERY_RESULT_SET_TOO_LARGE", "message": "private-diagnostic" }] }""")]
    [InlineData("""{ "error": { "code": "E_RUNAWAY_QUERY", "message": "private-diagnostic" } }""")]
    public async Task ExecuteQueryAsync_OnEmbeddedExecutionFailure_RejectsPartialResults(string errorFrame)
    {
        var responseJson =
            $$"""
            [
              {
                "FrameType": "DataTable",
                "TableKind": "PrimaryResult",
                "Columns": [{ "ColumnName": "Value", "ColumnType": "long" }],
                "Rows": [[1]]
              },
              {{errorFrame}}
            ]
            """;

        var exception = await Assert.ThrowsAsync<UserFacingException>(() => ExecuteResponseAsync(responseJson));

        Assert.Contains("results", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-diagnostic", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 2)]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 2)]
    public async Task ExecuteQueryAsync_OnFailedQueryStatus_RejectsPartialResults(bool v1, bool includeStatistics, int severity)
    {
        var tables =
            $$"""
            [
              {
                "FrameType": "DataTable",
                "TableName": "PrimaryResult",
                "TableKind": "PrimaryResult",
                "Columns": [{ "ColumnName": "Value", "ColumnType": "long" }],
                "Rows": [[1]]
              },
              {
                "FrameType": "DataTable",
                "TableName": "QueryStatus",
                "TableKind": "QueryCompletionInformation",
                "Columns": [
                  { "ColumnName": "Severity", "ColumnType": "int" },
                  { "ColumnName": "StatusDescription", "ColumnType": "string" }
                ],
                "Rows": [[{{severity}}, "private-diagnostic"]]
              }
            ]
            """;
        var responseJson = v1 ? $$"""{ "Tables": {{tables}} }""" : tables;

        var exception = await Assert.ThrowsAsync<UserFacingException>(() => ExecuteResponseAsync(responseJson, includeStatistics));

        Assert.Contains("results", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-diagnostic", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteQueryAsync_OnNamedQueryStatusError_RejectsPartialResults()
    {
        const string responseJson =
            """
            [
              {
                "FrameType": "DataTable",
                "TableKind": "PrimaryResult",
                "Columns": [{ "ColumnName": "Value", "ColumnType": "long" }],
                "Rows": [[1]]
              },
              {
                "FrameType": "DataTable",
                "TableKind": "QueryCompletionInformation",
                "Columns": [{ "ColumnName": "SeverityName", "ColumnType": "string" }],
                "Rows": [["Error"]]
              }
            ]
            """;

        await Assert.ThrowsAsync<UserFacingException>(() => ExecuteResponseAsync(responseJson));
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(false, 6)]
    [InlineData(true, 3)]
    [InlineData(true, 6)]
    public async Task ExecuteQueryAsync_OnNonFailureQueryStatus_PreservesResults(bool v1, int severity)
    {
        var tables =
            $$"""
            [
              {
                "FrameType": "DataTable",
                "TableKind": "PrimaryResult",
                "Columns": [{ "ColumnName": "Value", "ColumnType": "long" }],
                "Rows": [[1]]
              },
              {
                "FrameType": "DataTable",
                "TableName": "QueryStatus",
                "TableKind": "QueryCompletionInformation",
                "Columns": [{ "ColumnName": "Severity", "ColumnType": "int" }],
                "Rows": [[{{severity}}]]
              }
            ]
            """;

        var result = await ExecuteResponseAsync(v1 ? $$"""{ "Tables": {{tables}} }""" : tables);

        Assert.Equal("1", Assert.Single(result.Table.Rows)[0]);
    }

    [Fact]
    public async Task ExecuteManagementCommandAsync_OnFailedQueryStatus_RejectsPartialResults()
    {
        const string responseJson =
            """
            {
              "Tables": [
                {
                  "TableKind": "PrimaryResult",
                  "Columns": [{ "ColumnName": "DatabaseName", "DataType": "string" }],
                  "Rows": [["Samples"]]
                },
                {
                  "TableName": "QueryStatus",
                  "Columns": [{ "ColumnName": "Severity", "DataType": "int" }],
                  "Rows": [[2]]
                }
              ]
            }
            """;

        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        await Assert.ThrowsAsync<UserFacingException>(() =>
            service.ExecuteManagementCommandAsync(
                new ResolvedCluster(null, "https://help.kusto.windows.net"),
                "Samples",
                ".show databases",
                null,
                CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteQueryAsync_WithPrimaryResultNamedQueryStatus_PreservesBusinessData()
    {
        const string responseJson =
            """
            [
              {
                "FrameType": "DataTable",
                "TableKind": "PrimaryResult",
                "TableName": "QueryStatus",
                "Columns": [
                  { "ColumnName": "Severity", "ColumnType": "int" },
                  { "ColumnName": "SeverityName", "ColumnType": "string" }
                ],
                "Rows": [[1, "Error"]]
              },
              { "FrameType": "DataSetCompletion", "HasErrors": false, "Cancelled": false }
            ]
            """;

        var result = await ExecuteResponseAsync(responseJson);

        Assert.Equal(new string?[] { "1", "Error" }, Assert.Single(result.Table.Rows));
    }

    [Fact]
    public async Task ExecuteQueryAsync_WithLegacyQueryStatus_RejectsPartialResults()
    {
        const string responseJson =
            """
            {
              "Tables": [
                {
                  "TableName": "PrimaryResult",
                  "Columns": [{ "ColumnName": "Value", "DataType": "long" }],
                  "Rows": [[1]]
                },
                {
                  "TableName": "QueryStatus",
                  "Columns": [{ "ColumnName": "Severity", "DataType": "int" }],
                  "Rows": [[2]]
                }
              ]
            }
            """;

        await Assert.ThrowsAsync<UserFacingException>(() => ExecuteResponseAsync(responseJson));
    }

    [Fact]
    public async Task ExecuteQueryAsync_WithErrorNamedBusinessData_PreservesResults()
    {
        const string responseJson =
            """
            [
              {
                "FrameType": "DataTable",
                "TableKind": "PrimaryResult",
                "Columns": [{ "ColumnName": "Error", "ColumnType": "dynamic" }],
                "Rows": [[{ "error": "business-data", "HasErrors": true }]]
              },
              { "FrameType": "DataSetCompletion", "HasErrors": false, "Cancelled": false, "OneApiErrors": [] }
            ]
            """;

        var result = await ExecuteResponseAsync(responseJson);

        Assert.Contains("business-data", Assert.Single(result.Table.Rows)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ExtractsVisualizationMetadataFromExtendedProperties()
    {
        var responseJson = JsonSerializer.Serialize(
            new object[]
            {
                new
                {
                    FrameType = "DataTable",
                    TableName = "PrimaryResult",
                    TableKind = "PrimaryResult",
                    Columns = new object[]
                    {
                        new { ColumnName = "State", ColumnType = "string" },
                        new { ColumnName = "Count", ColumnType = "long" }
                    },
                    Rows = new object?[][]
                    {
                        ["TEXAS", 4701]
                    }
                },
                new
                {
                    FrameType = "DataTable",
                    TableName = "@ExtendedProperties",
                    TableKind = "QueryProperties",
                    Columns = new object[]
                    {
                        new { ColumnName = "TableId", ColumnType = "int" },
                        new { ColumnName = "Key", ColumnType = "string" },
                        new { ColumnName = "Value", ColumnType = "dynamic" }
                    },
                    Rows = new object?[][]
                    {
                        [
                            0,
                            "Visualization",
                            new Dictionary<string, object?>
                            {
                                ["Visualization"] = "piechart",
                                ["Title"] = "Top states",
                                ["XColumn"] = "State",
                                ["YColumns"] = "Count",
                                ["Legend"] = "hidden",
                                ["YMin"] = "NaN",
                                ["YMax"] = "NaN",
                                ["CustomProperty"] = "custom-value"
                            }
                        ]
                    }
                }
            });

        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var result = await service.ExecuteQueryAsync(
            new ResolvedCluster(null, "https://help.kusto.windows.net"),
            "Samples",
            "StormEvents | summarize Count=count() by State | render piechart",
            includeStatistics: false,
            CancellationToken.None);

        Assert.NotNull(result.Visualization);
        Assert.Equal("piechart", result.Visualization!.Visualization);
        Assert.Equal("Top states", result.Visualization.Title);
        Assert.Equal("State", result.Visualization.XColumn);
        Assert.Equal(["Count"], result.Visualization.YColumns);
        Assert.Equal("hidden", result.Visualization.Legend);
        Assert.Null(result.Visualization.YMin);
        Assert.Null(result.Visualization.YMax);
        Assert.Equal("custom-value", result.Visualization.AdditionalProperties?["CustomProperty"]);
        Assert.Contains("\"Visualization\":\"piechart\"", result.Visualization.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteQueryAsync_WithShowStats_ExtractsStatisticsFromStatusDescription()
    {
        var statsPayload = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["ExecutionTime"] = 1.234,
                ["resource_usage"] = new Dictionary<string, object?>
                {
                    ["cpu"] = new Dictionary<string, object?>
                    {
                        ["total cpu"] = "00:00:01.5",
                        ["breakdown"] = new Dictionary<string, object?>
                        {
                            ["query execution"] = "00:00:01.2",
                            ["query planning"] = "00:00:00.3"
                        }
                    },
                    ["memory"] = new Dictionary<string, object?>
                    {
                        ["peak_per_node"] = 47395635
                    },
                    ["cache"] = new Dictionary<string, object?>
                    {
                        ["shards"] = new Dictionary<string, object?>
                        {
                            ["hot"] = new Dictionary<string, object?>
                            {
                                ["hitbytes"] = 126353408,
                                ["missbytes"] = 3355443
                            }
                        }
                    },
                    ["network"] = new Dictionary<string, object?>
                    {
                        ["cross_cluster_total_bytes"] = 5452595,
                        ["inter_cluster_total_bytes"] = 0
                    }
                },
                ["input_dataset_statistics"] = new Dictionary<string, object?>
                {
                    ["extents"] = new Dictionary<string, object?>
                    {
                        ["scanned"] = 42,
                        ["total"] = 1000
                    },
                    ["rows"] = new Dictionary<string, object?>
                    {
                        ["scanned"] = 50000,
                        ["total"] = 1000000
                    }
                },
                ["dataset_statistics"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["table_row_count"] = 150,
                        ["table_size"] = 12800
                    }
                },
                ["cross_cluster_resource_usage"] = new Dictionary<string, object?>
                {
                    ["https://clustername.region.kusto.windows.net/"] = new Dictionary<string, object?>
                    {
                        ["cpu"] = new Dictionary<string, object?>
                        {
                            ["total cpu"] = "00:00:00.8"
                        },
                        ["memory"] = new Dictionary<string, object?>
                        {
                            ["peak_per_node"] = 23173530
                        },
                        ["cache"] = new Dictionary<string, object?>
                        {
                            ["shards"] = new Dictionary<string, object?>
                            {
                                ["hot"] = new Dictionary<string, object?>
                                {
                                    ["hitbytes"] = 52428800,
                                    ["missbytes"] = 1048576
                                }
                            }
                        }
                    }
                }
            });

        var responseJson = JsonSerializer.Serialize(
            new object[]
            {
                new
                {
                    FrameType = "DataTable",
                    TableName = "PrimaryResult",
                    TableKind = "PrimaryResult",
                    Columns = new object[]
                    {
                        new { ColumnName = "ValidationInline", ColumnType = "long" }
                    },
                    Rows = new object?[][]
                    {
                        [1]
                    }
                },
                new
                {
                    FrameType = "DataTable",
                    TableName = "QueryCompletionInformation",
                    TableKind = "QueryCompletionInformation",
                    Columns = new object[]
                    {
                        new { ColumnName = "SeverityName", ColumnType = "string" },
                        new { ColumnName = "StatusDescription", ColumnType = "string" }
                    },
                    Rows = new object?[][]
                    {
                        ["Stats", statsPayload]
                    }
                }
            });

        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var result = await service.ExecuteQueryAsync(
            new ResolvedCluster(null, "https://help.kusto.windows.net"),
            "Samples",
            "print ValidationInline=1",
            includeStatistics: true,
            CancellationToken.None);

        Assert.NotNull(result.Statistics);
        Assert.Equal(1.234, result.Statistics.ExecutionTimeSec);
        Assert.Equal(45.2, result.Statistics.MemoryPeakPerNodeMb);
        Assert.Equal(5.2, result.Statistics.Network?.CrossClusterMb);
        Assert.Equal(42, result.Statistics.Extents?.Scanned);
        Assert.Equal(150, result.Statistics.Result?.RowCount);
        Assert.Equal("00:00:00.8", result.Statistics.CrossClusterBreakdown?["clustername.region.kusto.windows.net"].CpuTotal);
        Assert.Equal(22.1, result.Statistics.CrossClusterBreakdown?["clustername.region.kusto.windows.net"].MemoryPeakMb);
    }

    [Fact]
    public async Task ExecuteQueryAsync_WithShowStats_ExtractsStatisticsFromPayload()
    {
        var responseJson = JsonSerializer.Serialize(
            new object[]
            {
                new
                {
                    FrameType = "DataTable",
                    TableName = "PrimaryResult",
                    TableKind = "PrimaryResult",
                    Columns = new object[]
                    {
                        new { ColumnName = "ValidationInline", ColumnType = "long" }
                    },
                    Rows = new object?[][]
                    {
                        [1]
                    }
                },
                new
                {
                    FrameType = "DataTable",
                    TableName = "QueryCompletionInformation",
                    TableKind = "QueryCompletionInformation",
                    Columns = new object[]
                    {
                        new { ColumnName = "EventTypeName", ColumnType = "string" },
                        new { ColumnName = "Payload", ColumnType = "string" }
                    },
                    Rows = new object?[][]
                    {
                        ["QueryResourceConsumption", "{\"ExecutionTime\":2.5}"]
                    }
                }
            });

        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var result = await service.ExecuteQueryAsync(
            new ResolvedCluster(null, "https://help.kusto.windows.net"),
            "Samples",
            "print ValidationInline=1",
            includeStatistics: true,
            CancellationToken.None);

        Assert.NotNull(result.Statistics);
        Assert.Equal(2.5, result.Statistics.ExecutionTimeSec);
    }

    [Fact]
    public async Task ExecuteQueryAsync_OnBadRequest_ReturnsActionableMessageWithoutStatusCode()
    {
        const string responseBody = "Bad request: Syntax error: Unexpected token ';' at position 13";
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(responseBody)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var exception = await Assert.ThrowsAsync<UserFacingException>(() =>
            service.ExecuteQueryAsync(
                new ResolvedCluster(null, "https://help.kusto.windows.net"),
                "Samples",
                "invalid query;",
                includeStatistics: false,
                CancellationToken.None));

        Assert.Contains("Kusto rejected the query or command", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unexpected token ';'", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("400", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bad request:", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteQueryAsync_OnForbidden_IncludesCloudSelectionGuidance()
    {
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("Forbidden")
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var exception = await Assert.ThrowsAsync<UserFacingException>(() =>
            service.ExecuteQueryAsync(
                new ResolvedCluster(null, "https://help.kusto.windows.net"),
                "Samples",
                "StormEvents | take 1",
                includeStatistics: false,
                CancellationToken.None));

        Assert.Contains("az login", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("az cloud set", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteManagementCommandAsync_OnBadRequest_HidesServiceMetadata()
    {
        const string responseBody =
            """
            General_BadRequest: Request is invalid and cannot be executed.
            Error details:
            ClientRequestId='unspecified;123', ActivityId='456', Timestamp='2026-01-01T00:00:00.0000000Z'.
            """;
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(responseBody)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var exception = await Assert.ThrowsAsync<UserFacingException>(() =>
            service.ExecuteManagementCommandAsync(
                new ResolvedCluster(null, "https://help.kusto.windows.net"),
                "Samples",
                ".show tables",
                null,
                CancellationToken.None));

        Assert.Equal(
            "Kusto rejected the query or command for cluster 'https://help.kusto.windows.net' and database 'Samples'. Check your syntax and verify that the selected cluster and database match the query or command.",
            exception.Message);
        Assert.DoesNotContain("ClientRequestId", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ActivityId", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Timestamp", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteQueryAsync_OnGenericBadRequest_IncludesClusterAndDatabaseContext()
    {
        const string responseBody = "General_BadRequest: Request is invalid and cannot be executed.";
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(responseBody)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var exception = await Assert.ThrowsAsync<UserFacingException>(() =>
            service.ExecuteQueryAsync(
                new ResolvedCluster(null, "https://help.kusto.windows.net"),
                "Samples",
                "StormEvents | take 1",
                includeStatistics: false,
                CancellationToken.None));

        Assert.Contains("https://help.kusto.windows.net", exception.Message, StringComparison.Ordinal);
        Assert.Contains("database 'Samples'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("General_BadRequest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteManagementCommandAsync_WithQueryParameters_SerializesPropertiesPayload()
    {
        const string responseJson =
            """
            {
              "Tables": [
                {
                  "TableName": "PrimaryResult",
                  "TableKind": "PrimaryResult",
                  "Columns": [{ "ColumnName": "DatabaseName", "DataType": "string" }],
                  "Rows": [["Samples"]]
                }
              ]
            }
            """;
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        _ = await service.ExecuteManagementCommandAsync(
            new ResolvedCluster(null, "https://help.kusto.windows.net"),
            null,
            "declare query_parameters(filterValue:string); .show databases | where DatabaseName contains filterValue",
            new Dictionary<string, string> { ["filterValue"] = "'Sam'" },
            CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        using var requestDocument = JsonDocument.Parse(handler.LastRequestBody!);
        var propertiesElement = requestDocument.RootElement.GetProperty("properties");
        Assert.Equal("'Sam'", propertiesElement.GetProperty("Parameters").GetProperty("filterValue").GetString());
    }

    [Fact]
    public async Task ExecuteQueryAsync_WithAdeProxyUrl_PreservesFullResourcePathInRequestUri()
    {
        const string responseJson =
            """
            [
              { "FrameType": "DataSetHeader", "IsProgressive": false },
              {
                "FrameType": "DataTable",
                "TableName": "PrimaryResult",
                "TableKind": "PrimaryResult",
                "Columns": [{ "ColumnName": "TimeGenerated", "ColumnType": "datetime" }],
                "Rows": [["2026-01-01T00:00:00Z"]]
              },
              { "FrameType": "DataSetCompletion", "HasErrors": false }
            ]
            """;
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        const string adeUrl = "https://ade.applicationinsights.io/subscriptions/00000000-0000-0000-0000-000000000000/resourcegroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws";

        _ = await service.ExecuteQueryAsync(
            new ResolvedCluster(null, adeUrl),
            "ws",
            "AppTraces | take 1",
            includeStatistics: false,
            CancellationToken.None);

        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal(
            adeUrl + "/v2/rest/query",
            handler.LastRequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ExecuteManagementCommandAsync_WithAdeProxyUrl_PreservesFullResourcePathInRequestUri()
    {
        const string responseJson =
            """
            {
              "Tables": [
                {
                  "TableName": "PrimaryResult",
                  "TableKind": "PrimaryResult",
                  "Columns": [{ "ColumnName": "TableName", "DataType": "string" }],
                  "Rows": [["AppTraces"]]
                }
              ]
            }
            """;
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        const string adeUrl = "https://adx.monitor.azure.com/subscriptions/sub/resourcegroups/rg/providers/microsoft.insights/components/app";

        _ = await service.ExecuteManagementCommandAsync(
            new ResolvedCluster(null, adeUrl),
            "app",
            ".show tables",
            null,
            CancellationToken.None);

        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal(
            adeUrl + "/v1/rest/mgmt",
            handler.LastRequestUri!.AbsoluteUri);
    }

    internal static async Task<QueryExecutionResult> ExecuteResponseAsync(string responseJson, bool includeStatistics = false)
    {
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        return await service.ExecuteQueryAsync(
            new ResolvedCluster(null, "https://help.kusto.windows.net"),
            "Samples",
            "print Value=1",
            includeStatistics,
            CancellationToken.None);
    }

    private sealed class StaticTokenProvider(string token) : ITokenProvider
    {
        public Task<string> GetTokenAsync(ResolvedCluster cluster, CancellationToken cancellationToken)
        {
            return Task.FromResult(token);
        }
    }

    private sealed class RecordingHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responseFactory = responseFactory;

        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }
        public string? LastRequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequestUri = request.RequestUri;
            return _responseFactory();
        }
    }
}
