using System.Text.Json;

namespace Kusto.Cli.Tests;

public sealed class OutputFormatterTests
{
    [Fact]
    public void FormatHuman_TableOutput_IncludesHeadersAndRows()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(
                ["TableName", "RowCount"],
                [
                    ["DotnetEvents", "42"],
                    ["DotnetMetrics", "7"]
                ])
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("TableName", rendered, StringComparison.Ordinal);
        Assert.Contains("RowCount", rendered, StringComparison.Ordinal);
        Assert.Contains("DotnetEvents", rendered, StringComparison.Ordinal);
        Assert.Contains("DotnetMetrics", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHuman_QueryTableOutput_UsesReadableTabularLayout()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            IsQueryResultTable = true,
            Table = new TabularData(
                ["Name", "Count"],
                [
                    ["alpha", "42"],
                    ["beta", "7"]
                ])
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("Name", rendered, StringComparison.Ordinal);
        Assert.Contains("Count", rendered, StringComparison.Ordinal);
        Assert.Contains("alpha", rendered, StringComparison.Ordinal);
        Assert.Contains("beta", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHuman_PropertiesOutput_RendersPropertyTable()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Properties = new Dictionary<string, string?>
            {
                ["Name"] = "Samples",
                ["Default"] = "true"
            }
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("Property", rendered, StringComparison.Ordinal);
        Assert.Contains("Value", rendered, StringComparison.Ordinal);
        Assert.Contains("Name", rendered, StringComparison.Ordinal);
        Assert.Contains("Samples", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatJson_QueryOutput_IncludesWebExplorerUrlAndStatistics()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["Name"], [["alpha"]]),
            WebExplorerUrl = "https://dataexplorer.azure.com/clusters/help.kusto.windows.net/databases/Samples?query=abc",
            Statistics = new QueryStatistics
            {
                ExecutionTimeSec = 1.23,
                Network = new QueryNetworkStatistics
                {
                    CrossClusterMb = 5.2
                }
            }
        };

        var rendered = formatter.Format(output, OutputFormat.Json);

        using var document = JsonDocument.Parse(rendered);
        Assert.Equal(
            "https://dataexplorer.azure.com/clusters/help.kusto.windows.net/databases/Samples?query=abc",
            document.RootElement.GetProperty("webExplorerUrl").GetString());
        Assert.Equal(1.23, document.RootElement.GetProperty("statistics").GetProperty("executionTimeSec").GetDouble());
        Assert.Equal(5.2, document.RootElement.GetProperty("statistics").GetProperty("network").GetProperty("crossClusterMb").GetDouble());
        Assert.False(document.RootElement.GetProperty("statistics").TryGetProperty("rows", out _));
    }

    [Fact]
    public void FormatCsv_TableOutput_UsesHeadersAndRows()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(
                ["Name", "Count"],
                [
                    ["alpha", "42"],
                    ["beta", "7"]
                ])
        };

        var rendered = formatter.Format(output, OutputFormat.Csv);

        var expected = string.Join(Environment.NewLine, ["Name,Count", "alpha,42", "beta,7"]);
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void FormatCsv_TableOutput_EscapesSpecialCharacters()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(
                ["Name", "Notes", "Quote"],
                [
                    ["alpha,beta", $"line1{Environment.NewLine}line2", "he said \"hi\""]
                ])
        };

        var rendered = formatter.Format(output, OutputFormat.Csv);

        var expected = string.Join(
            Environment.NewLine,
            [
                "Name,Notes,Quote",
                $"\"alpha,beta\",\"line1{Environment.NewLine}line2\",\"he said \"\"hi\"\"\""
            ]);
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void FormatCsv_QueryOutput_IgnoresNonTabularMetadata()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Message = "not included",
            Properties = new Dictionary<string, string?> { ["Name"] = "Samples" },
            Table = new TabularData(["Name"], [["alpha"]]),
            WebExplorerUrl = "https://dataexplorer.azure.com/",
            Statistics = new QueryStatistics { ExecutionTimeSec = 1.23 },
            Visualization = new QueryVisualization { Visualization = "piechart" },
            ChartHint = "hint",
            ChartMessage = "message",
            HumanChart = "chart",
            MarkdownChart = "```mermaid```"
        };

        var rendered = formatter.Format(output, OutputFormat.Csv);

        Assert.Equal(string.Join(Environment.NewLine, ["Name", "alpha"]), rendered);
    }

    [Fact]
    public void FormatCsv_WithoutTable_ReturnsEmptyString()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Message = "hello"
        };

        var rendered = formatter.Format(output, OutputFormat.Csv);

        Assert.Equal(string.Empty, rendered);
    }

    [Fact]
    public void FormatHuman_QueryOutput_HidesWebExplorerUrlByDefault()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["Name"], [["alpha"]]),
            WebExplorerUrl = "https://dataexplorer.azure.com/clusters/help.kusto.windows.net/databases/Samples?query=abc",
            Statistics = new QueryStatistics
            {
                ExecutionTimeSec = 1.23,
                Result = new QueryResultStatistics
                {
                    RowCount = 1
                }
            }
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("Open in Web Explorer", rendered, StringComparison.Ordinal);
        Assert.Contains("Statistics", rendered, StringComparison.Ordinal);
        Assert.Contains("ExecutionTimeSec", rendered, StringComparison.Ordinal);
        Assert.Contains("Result.RowCount", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("WebExplorerUrl", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHuman_QueryOutput_WithVisualization_ShowsRenderSummary()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["State", "Count"], [["TEXAS", "4701"]]),
            WebExplorerUrl = "https://dataexplorer.azure.com/clusters/help.kusto.windows.net/databases/Samples?query=abc",
            Visualization = new QueryVisualization
            {
                Visualization = "piechart",
                Title = "Top states",
                XColumn = "State",
                YColumns = ["Count"],
                AdditionalProperties = new Dictionary<string, string?>
                {
                    ["CustomProperty"] = "custom-value"
                },
                Raw = "{\"Visualization\":\"piechart\"}"
            },
            ChartHint = "This query can be rendered as a terminal chart. Re-run with --chart to see it."
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("Render requested: piechart", rendered, StringComparison.Ordinal);
        Assert.Contains("Title", rendered, StringComparison.Ordinal);
        Assert.Contains("Top states", rendered, StringComparison.Ordinal);
        Assert.Contains("Additional.CustomProperty", rendered, StringComparison.Ordinal);
        Assert.Contains("Re-run with --chart to see it", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("see it.Open in Web Explorer", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"Visualization\":\"piechart\"}", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHuman_QueryOutput_WithHumanChart_AppendsRenderedChart()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["State", "Count"], [["TEXAS", "4701"]]),
            Visualization = new QueryVisualization
            {
                Visualization = "columnchart"
            },
            HumanChart = "Top states\nTEXAS 4701"
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("Top states", rendered, StringComparison.Ordinal);
        Assert.Contains("TEXAS 4701", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHuman_QueryOutput_WithPieHumanChart_AppendsRenderedChart()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["State", "Count"], [["TEXAS", "60"], ["KANSAS", "40"]]),
            Visualization = new QueryVisualization
            {
                Visualization = "piechart"
            },
            HumanChart = "Top states\nTEXAS 60 60%\nKANSAS 40 40%"
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("Top states", rendered, StringComparison.Ordinal);
        Assert.Contains("TEXAS 60 60%", rendered, StringComparison.Ordinal);
        Assert.Contains("KANSAS 40 40%", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHuman_QueryOutput_WithAnsiHumanChart_FallsBackToPlainChartWhenAnsiIsUnavailable()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["State", "Count"], [["TEXAS", "60"]]),
            Visualization = new QueryVisualization
            {
                Visualization = "piechart"
            },
            HumanChart = "Top states\nTEXAS 60 60%",
            HumanChartAnsi = "\u001b[31mansi chart\u001b[0m"
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("Top states", rendered, StringComparison.Ordinal);
        Assert.Contains("TEXAS 60 60%", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[31m", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHuman_MessageOutput_PreservesWideUnicodeCharacters()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Message = "abc漢字"
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Equal("abc漢字", rendered);
    }

    [Fact]
    public void FormatJson_QueryOutput_WithVisualization_IncludesStructuredMetadata()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["State"], [["TEXAS"]]),
            Visualization = new QueryVisualization
            {
                Visualization = "piechart",
                Title = "Top states",
                XColumn = "State",
                YColumns = ["Count"],
                Raw = "{\"Visualization\":\"piechart\"}"
            }
        };

        var rendered = formatter.Format(output, OutputFormat.Json);

        using var document = JsonDocument.Parse(rendered);
        Assert.Equal("piechart", document.RootElement.GetProperty("visualization").GetProperty("visualization").GetString());
        Assert.Equal("Top states", document.RootElement.GetProperty("visualization").GetProperty("title").GetString());
        Assert.Equal("State", document.RootElement.GetProperty("visualization").GetProperty("xColumn").GetString());
        Assert.Equal("Count", document.RootElement.GetProperty("visualization").GetProperty("yColumns")[0].GetString());
        Assert.Equal("{\"Visualization\":\"piechart\"}", document.RootElement.GetProperty("visualization").GetProperty("raw").GetString());
    }

    [Fact]
    public void FormatMarkdown_QueryOutput_HidesWebExplorerUrlByDefault()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["Name"], [["alpha"]]),
            WebExplorerUrl = "https://dataexplorer.azure.com/clusters/help.kusto.windows.net/databases/Samples?query=abc",
            Statistics = new QueryStatistics
            {
                ExecutionTimeSec = 1.23
            }
        };

        var rendered = formatter.Format(output, OutputFormat.Markdown);

        Assert.Contains("### Statistics", rendered, StringComparison.Ordinal);
        Assert.Contains("ExecutionTimeSec", rendered, StringComparison.Ordinal);
        Assert.Contains("[Open in Web Explorer](", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("WebExplorerUrl", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMarkdown_QueryOutput_WithVisualization_ShowsRenderSection()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["State", "Count"], [["TEXAS", "4701"]]),
            Visualization = new QueryVisualization
            {
                Visualization = "piechart",
                Title = "Top states",
                YColumns = ["Count"]
            },
            ChartMessage = "The 'piechart' render kind is not supported for terminal chart rendering."
        };

        var rendered = formatter.Format(output, OutputFormat.Markdown);

        Assert.Contains("### Render", rendered, StringComparison.Ordinal);
        Assert.Contains("Render requested: piechart", rendered, StringComparison.Ordinal);
        Assert.Contains("| Title | Top states |", rendered, StringComparison.Ordinal);
        Assert.Contains("not supported for terminal chart rendering", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMarkdown_QueryOutput_WithMermaidChart_AppendsMermaidBlock()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["State", "Count"], [["TEXAS", "4701"]]),
            Visualization = new QueryVisualization
            {
                Visualization = "piechart",
                Title = "Top states"
            },
            MarkdownChart = "```mermaid\npie showData\n```"
        };

        var rendered = formatter.Format(output, OutputFormat.Markdown);

        Assert.Contains("```mermaid", rendered, StringComparison.Ordinal);
        Assert.Contains("pie showData", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Markdown)]
    [InlineData(OutputFormat.Csv)]
    public void Format_NonHumanOutputFormats_PreserveRawValues(OutputFormat format)
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            IsQueryResultTable = true,
            Table = new TabularData(
                ["Day", "RowCount"],
                [
                    ["2026-02-27T00:00:00Z", "66380993"]
                ])
        };

        var rendered = formatter.Format(output, format);

        Assert.Contains("2026-02-27T00:00:00Z", rendered, StringComparison.Ordinal);
        Assert.Contains("66380993", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("66,380,993", rendered, StringComparison.Ordinal);
    }
}
