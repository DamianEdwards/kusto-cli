using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kusto.Cli;

public sealed class OutputFormatter : IOutputFormatter
{
    public string Format(CliOutput output, OutputFormat format)
    {
        return format switch
        {
            OutputFormat.Json => JsonSerializer.Serialize(output, KustoJsonSerializerContext.Default.CliOutput),
            OutputFormat.Markdown => FormatMarkdown(output),
            OutputFormat.Csv => FormatCsv(output),
            OutputFormat.Tsv => FormatTsv(output),
            _ => FormatHuman(output)
        };
    }

    private static string FormatHuman(CliOutput output)
    {
        var useAnsi = ConsoleRendering.ShouldUseAnsiForStandardOutput();
        var availableWidth = ConsoleRendering.TryGetStandardOutputWidth();
        var leadingText = BuildHumanTextOutput(output, availableWidth);
        var sections = new List<string>();

        if (!string.IsNullOrWhiteSpace(leadingText))
        {
            sections.Add(Hex1bHumanRenderer.RenderText(leadingText));
        }

        if (!string.IsNullOrWhiteSpace(output.HumanChart))
        {
            var selectedChart = useAnsi && !string.IsNullOrWhiteSpace(output.HumanChartAnsi)
                ? output.HumanChartAnsi
                : output.HumanChart;
            sections.Add(selectedChart);
        }

        if (!string.IsNullOrWhiteSpace(output.WebExplorerUrl))
        {
            sections.Add(Hex1bHumanRenderer.RenderHyperlink("Open in Web Explorer", output.WebExplorerUrl, useAnsi));
        }

        return string.Join(Environment.NewLine + Environment.NewLine, sections.Where(section => !string.IsNullOrWhiteSpace(section))).TrimEnd();
    }

    private static string FormatMarkdown(CliOutput output)
    {
        var buffer = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(output.Message))
        {
            buffer.AppendLine(output.Message);
            buffer.AppendLine();
        }

        var displayProperties = BuildDisplayProperties(output);
        if (displayProperties.Count > 0)
        {
            AppendPropertiesTable(buffer, displayProperties);
            buffer.AppendLine();
        }

        if (output.Statistics is not null)
        {
            var statisticsProperties = FlattenStatistics(output.Statistics);
            if (statisticsProperties.Count > 0)
            {
                buffer.AppendLine("### Statistics");
                buffer.AppendLine();
                AppendPropertiesTable(buffer, statisticsProperties);
                buffer.AppendLine();
            }
        }

        if (output.Table is not null)
        {
            if (output.Table.Columns.Count > 0)
            {
                buffer.AppendLine($"| {string.Join(" | ", output.Table.Columns.Select(EscapeMarkdown))} |");
                buffer.AppendLine($"| {string.Join(" | ", output.Table.Columns.Select(_ => "---"))} |");
            }

            foreach (var row in output.Table.Rows)
            {
                buffer.AppendLine($"| {string.Join(" | ", row.Select(value => EscapeMarkdown(value ?? string.Empty)))} |");
            }
        }

        if (output.Visualization is not null)
        {
            if (buffer.Length > 0)
            {
                buffer.AppendLine();
            }

            AppendVisualizationSection(buffer, output.Visualization, output.ChartHint, output.ChartMessage);
        }

        if (!string.IsNullOrWhiteSpace(output.MarkdownChart))
        {
            if (buffer.Length > 0)
            {
                buffer.AppendLine();
            }

            buffer.AppendLine(output.MarkdownChart);
        }

        if (!string.IsNullOrWhiteSpace(output.WebExplorerUrl))
        {
            if (buffer.Length > 0)
            {
                buffer.AppendLine();
            }

            buffer.AppendLine($"[Open in Web Explorer]({output.WebExplorerUrl})");
        }

        return buffer.ToString().TrimEnd();
    }

    private static string FormatCsv(CliOutput output)
    {
        if (output.Table is not { Columns.Count: > 0 } table)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder();
        AppendCsvRow(buffer, table.Columns);

        foreach (var row in table.Rows)
        {
            AppendCsvRow(buffer, row);
        }

        return buffer.ToString().TrimEnd('\r', '\n');
    }

    private static string FormatTsv(CliOutput output)
    {
        if (output.Table is not { Columns.Count: > 0 } table)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder();
        buffer.AppendLine(string.Join('\t', table.Columns.Select(EscapeTsvValue)));

        foreach (var row in table.Rows)
        {
            buffer.AppendLine(string.Join('\t', row.Select(EscapeTsvValue)));
        }

        return buffer.ToString().TrimEnd('\r', '\n');
    }

    private static string EscapeTsvValue(string? value)
    {
        var text = value ?? string.Empty;
        if (!text.Contains('\\') &&
            !text.Contains('\t') &&
            !text.Contains('\r') &&
            !text.Contains('\n'))
        {
            return text;
        }

        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> BuildDisplayProperties(CliOutput output)
    {
        return output.Properties is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(output.Properties, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string?> BuildVisualizationProperties(QueryVisualization visualization)
    {
        var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        AddStatistic(properties, "Title", visualization.Title);
        AddStatistic(properties, "XColumn", visualization.XColumn);
        AddStatistic(properties, "YColumns", Join(visualization.YColumns));
        AddStatistic(properties, "Series", Join(visualization.Series));
        AddStatistic(properties, "XTitle", visualization.XTitle);
        AddStatistic(properties, "YTitle", visualization.YTitle);
        AddStatistic(properties, "Kind", visualization.Kind);
        AddStatistic(properties, "Legend", visualization.Legend);
        AddStatistic(properties, "YMin", FormatNumber(visualization.YMin));
        AddStatistic(properties, "YMax", FormatNumber(visualization.YMax));

        if (visualization.AdditionalProperties is not null)
        {
            foreach (var pair in visualization.AdditionalProperties.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                AddStatistic(properties, $"Additional.{pair.Key}", pair.Value);
            }
        }

        return properties;
    }

    private static Dictionary<string, string?> FlattenStatistics(QueryStatistics statistics)
    {
        var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        AddStatistic(properties, "ExecutionTimeSec", FormatNumber(statistics.ExecutionTimeSec));

        if (statistics.Cpu is not null)
        {
            AddStatistic(properties, "Cpu.Total", statistics.Cpu.Total);
            AddStatistic(properties, "Cpu.QueryExecution", statistics.Cpu.QueryExecution);
            AddStatistic(properties, "Cpu.QueryPlanning", statistics.Cpu.QueryPlanning);
        }

        AddStatistic(properties, "MemoryPeakPerNodeMb", FormatNumber(statistics.MemoryPeakPerNodeMb));

        if (statistics.Cache is not null)
        {
            AddStatistic(properties, "Cache.HotHitMb", FormatNumber(statistics.Cache.HotHitMb));
            AddStatistic(properties, "Cache.HotMissMb", FormatNumber(statistics.Cache.HotMissMb));
        }

        if (statistics.Network is not null)
        {
            AddStatistic(properties, "Network.CrossClusterMb", FormatNumber(statistics.Network.CrossClusterMb));
            AddStatistic(properties, "Network.InterClusterMb", FormatNumber(statistics.Network.InterClusterMb));
        }

        if (statistics.Extents is not null)
        {
            AddStatistic(properties, "Extents.Scanned", FormatNumber(statistics.Extents.Scanned));
            AddStatistic(properties, "Extents.Total", FormatNumber(statistics.Extents.Total));
        }

        if (statistics.Rows is not null)
        {
            AddStatistic(properties, "Rows.Scanned", FormatNumber(statistics.Rows.Scanned));
            AddStatistic(properties, "Rows.Total", FormatNumber(statistics.Rows.Total));
        }

        if (statistics.Result is not null)
        {
            AddStatistic(properties, "Result.RowCount", FormatNumber(statistics.Result.RowCount));
            AddStatistic(properties, "Result.SizeKb", FormatNumber(statistics.Result.SizeKb));
        }

        if (statistics.CrossClusterBreakdown is not null)
        {
            foreach (var cluster in statistics.CrossClusterBreakdown.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                AddStatistic(properties, $"CrossClusterBreakdown.{cluster.Key}.CpuTotal", cluster.Value.CpuTotal);
                AddStatistic(properties, $"CrossClusterBreakdown.{cluster.Key}.MemoryPeakMb", FormatNumber(cluster.Value.MemoryPeakMb));
                AddStatistic(properties, $"CrossClusterBreakdown.{cluster.Key}.CacheHitMb", FormatNumber(cluster.Value.CacheHitMb));
                AddStatistic(properties, $"CrossClusterBreakdown.{cluster.Key}.CacheMissMb", FormatNumber(cluster.Value.CacheMissMb));
            }
        }

        return properties;
    }

    private static void AppendPropertiesTable(StringBuilder buffer, IReadOnlyDictionary<string, string?> properties)
    {
        buffer.AppendLine("| Property | Value |");
        buffer.AppendLine("|---|---|");
        foreach (var pair in properties.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            buffer.AppendLine($"| {EscapeMarkdown(pair.Key)} | {EscapeMarkdown(pair.Value ?? string.Empty)} |");
        }
    }

    private static void AddStatistic(IDictionary<string, string?> properties, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            properties[key] = value;
        }
    }

    private static void AppendCsvRow(StringBuilder buffer, IEnumerable<string?> values)
    {
        var firstValue = true;
        foreach (var value in values)
        {
            if (!firstValue)
            {
                buffer.Append(',');
            }

            buffer.Append(EscapeCsvValue(value));
            firstValue = false;
        }

        buffer.AppendLine();
    }

    private static string EscapeCsvValue(string? value)
    {
        var text = value ?? string.Empty;
        if (!text.Contains(',', StringComparison.Ordinal) &&
            !text.Contains('"', StringComparison.Ordinal) &&
            !text.Contains('\r') &&
            !text.Contains('\n'))
        {
            return text;
        }

        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string BuildHumanTextOutput(CliOutput output, int? availableWidth)
    {
        var buffer = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(output.Message))
        {
            AppendHumanSection(buffer, output.Message);
        }

        var displayProperties = BuildDisplayProperties(output);
        if (displayProperties.Count > 0)
        {
            AppendHumanSection(buffer, Hex1bHumanRenderer.FormatKeyValueTable(displayProperties, availableWidth));
        }

        if (output.Statistics is not null)
        {
            var statisticsProperties = FlattenStatistics(output.Statistics);
            if (statisticsProperties.Count > 0)
            {
                AppendHumanSection(buffer, FormatStatisticsSection(statisticsProperties, availableWidth));
            }
        }

        if (output.Table is not null)
        {
            AppendHumanSection(buffer, Hex1bHumanRenderer.FormatDataTable(output.Table, output.IsQueryResultTable, availableWidth));
        }

        if (output.Visualization is not null)
        {
            AppendHumanSection(buffer, FormatHumanVisualizationSection(output.Visualization, output.ChartHint, output.ChartMessage, availableWidth));
        }

        return buffer.ToString().TrimEnd();
    }

    private static string FormatStatisticsSection(IReadOnlyDictionary<string, string?> statisticsProperties, int? availableWidth)
    {
        var buffer = new StringBuilder();
        buffer.AppendLine("Statistics");
        buffer.AppendLine();
        buffer.Append(Hex1bHumanRenderer.FormatKeyValueTable(statisticsProperties, availableWidth));
        return buffer.ToString().TrimEnd();
    }

    private static string FormatHumanVisualizationSection(QueryVisualization visualization, string? chartHint, string? chartMessage, int? availableWidth)
    {
        var buffer = new StringBuilder();
        buffer.AppendLine($"Render requested: {visualization.Visualization ?? "visualization"}");

        var properties = BuildVisualizationProperties(visualization);
        if (properties.Count > 0)
        {
            buffer.AppendLine();
            buffer.Append(Hex1bHumanRenderer.FormatKeyValueTable(properties, availableWidth));
        }

        if (!string.IsNullOrWhiteSpace(chartHint))
        {
            buffer.AppendLine();
            buffer.Append(chartHint);
        }

        if (!string.IsNullOrWhiteSpace(chartMessage))
        {
            buffer.AppendLine();
            buffer.Append(chartMessage);
        }

        return buffer.ToString().TrimEnd();
    }

    private static string? FormatNumber(double? value)
    {
        return value?.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string? FormatNumber(int? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture);
    }

    private static string EscapeMarkdown(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static string? Join(IReadOnlyList<string>? values)
    {
        return values is { Count: > 0 }
            ? string.Join(", ", values)
            : null;
    }

    private static void AppendHumanSection(StringBuilder buffer, string section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return;
        }

        if (buffer.Length > 0)
        {
            buffer.AppendLine();
            buffer.AppendLine();
        }

        buffer.Append(section.TrimEnd());
    }

    private static void AppendVisualizationSection(StringBuilder buffer, QueryVisualization visualization, string? chartHint, string? chartMessage)
    {
        buffer.AppendLine("### Render");
        buffer.AppendLine();
        buffer.AppendLine($"Render requested: {visualization.Visualization ?? "visualization"}");
        buffer.AppendLine();

        var properties = BuildVisualizationProperties(visualization);
        if (properties.Count > 0)
        {
            AppendPropertiesTable(buffer, properties);
            buffer.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(chartHint))
        {
            buffer.AppendLine(chartHint);
            buffer.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(chartMessage))
        {
            buffer.AppendLine(chartMessage);
        }
    }
}
