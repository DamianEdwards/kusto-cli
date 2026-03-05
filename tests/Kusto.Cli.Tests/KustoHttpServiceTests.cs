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
                    [ "ddtelinsights" ]
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
            "https://ddtelinsights.kusto.windows.net",
            null,
            ".show databases | project DatabaseName",
            null,
            CancellationToken.None);

        Assert.Equal(["DatabaseName"], result.Columns);
        Assert.Single(result.Rows);
        Assert.Equal("ddtelinsights", result.Rows[0][0]);
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
            "https://ddtelinsights.kusto.windows.net",
            "DDTelInsights",
            "print ValidationInline=1",
            CancellationToken.None);

        Assert.Equal(["ValidationInline"], result.Columns);
        Assert.Single(result.Rows);
        Assert.Equal("1", result.Rows[0][0]);
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
                "https://ddtelinsights.kusto.windows.net",
                "DDTelInsights",
                "invalid query;",
                CancellationToken.None));

        Assert.Contains("Kusto rejected the query or command", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unexpected token ';'", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("400", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bad request:", exception.Message, StringComparison.OrdinalIgnoreCase);
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
                "https://ddtelinsights.kusto.windows.net",
                "DDTelInsights",
                ".show tables",
                null,
                CancellationToken.None));

        Assert.Equal("Kusto rejected the query or command. Check your syntax and verify the selected cluster and database.", exception.Message);
        Assert.DoesNotContain("ClientRequestId", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ActivityId", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Timestamp", exception.Message, StringComparison.OrdinalIgnoreCase);
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
                  "Rows": [["DDTelInsights"]]
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
            "https://ddtelinsights.kusto.windows.net",
            null,
            "declare query_parameters(filterValue:string); .show databases | where DatabaseName contains filterValue",
            new Dictionary<string, string> { ["filterValue"] = "'ddtel'" },
            CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        using var requestDocument = JsonDocument.Parse(handler.LastRequestBody!);
        var propertiesElement = requestDocument.RootElement.GetProperty("properties");
        Assert.Equal("'ddtel'", propertiesElement.GetProperty("Parameters").GetProperty("filterValue").GetString());
    }

    private sealed class StaticTokenProvider(string token) : ITokenProvider
    {
        public Task<string> GetTokenAsync(string clusterUrl, CancellationToken cancellationToken)
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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responseFactory();
        }
    }
}
