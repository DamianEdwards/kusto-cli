using System.Globalization;
using System.Text;

namespace Kusto.Cli;

internal static class MermaidChartRenderer
{
    public static string Render(QueryChartDefinition chart)
    {
        var buffer = new StringBuilder();
        buffer.AppendLine("```mermaid");

        if (chart.Kind == QueryChartKind.Pie)
        {
            buffer.AppendLine("pie showData");
            if (!string.IsNullOrWhiteSpace(chart.Title))
            {
                buffer.AppendLine($"    title {Quote(chart.Title)}");
            }

            var series = chart.Series[0];
            for (var i = 0; i < chart.Categories.Count; i++)
            {
                buffer.AppendLine($"    {Quote(chart.Categories[i])} : {series.Values[i].ToString("0.##", CultureInfo.InvariantCulture)}");
            }
        }
        else
        {
            buffer.AppendLine(chart.Horizontal ? "xychart horizontal" : "xychart");
            if (!string.IsNullOrWhiteSpace(chart.Title))
            {
                buffer.AppendLine($"    title {Quote(chart.Title)}");
            }

            buffer.AppendLine(string.IsNullOrWhiteSpace(chart.XTitle)
                ? $"    x-axis [{string.Join(", ", chart.Categories.Select(Quote))}]"
                : $"    x-axis {Quote(chart.XTitle)} [{string.Join(", ", chart.Categories.Select(Quote))}]");
            buffer.AppendLine(string.IsNullOrWhiteSpace(chart.YTitle)
                ? "    y-axis"
                : $"    y-axis {Quote(chart.YTitle)}");

            var series = chart.Series[0];
            var directive = chart.Kind == QueryChartKind.Line ? "line" : "bar";
            buffer.AppendLine($"    {directive} [{string.Join(", ", series.Values.Select(value => value.ToString("0.##", CultureInfo.InvariantCulture)))}]");
        }

        buffer.Append("```");
        return buffer.ToString();
    }

    private static string Quote(string value)
    {
        var sanitized = string.Create(value.Length, value, static (buffer, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                buffer[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });

        return $"\"{sanitized.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
