using System.Text;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Charts;
using Hex1b.Widgets;

namespace Kusto.Cli;

internal static class Hex1bChartRenderer
{
    private const int ChartWidth = 100;
    private const int ChartHeight = 24;

    public static async Task<string> RenderAsync(QueryChartDefinition chart, CancellationToken cancellationToken)
    {
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(ChartWidth, ChartHeight)
            .WithHex1bApp((app, options) =>
            {
                options.EnableDefaultCtrlCExit = false;
                return ctx => BuildWidget(ctx, chart);
            })
            .Build();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runTask = terminal.RunAsync(cts.Token);

        await Task.Delay(150, cancellationToken);
        using var snapshot = terminal.CreateSnapshot();
        var screenText = Normalize(snapshot.GetScreenText());

        cts.Cancel();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        return screenText;
    }

    private static Hex1bWidget BuildWidget(RootContext ctx, QueryChartDefinition chart)
    {
        var rows = BuildRows(chart);
        return chart.Kind switch
        {
            QueryChartKind.Column => ConfigureColumnChart(ctx.ColumnChart(rows), chart),
            QueryChartKind.Bar => ConfigureBarChart(ctx.BarChart(rows), chart),
            QueryChartKind.Line => ConfigureTimeSeriesChart(ctx.TimeSeriesChart(rows), chart),
            _ => ctx.Text("Unsupported chart")
        };
    }

    private static ColumnChartWidget<Hex1bChartRow> ConfigureColumnChart(ColumnChartWidget<Hex1bChartRow> widget, QueryChartDefinition chart)
    {
        widget = widget.Label(row => row.Label).Title(chart.Title ?? "Chart");
        foreach (var series in chart.Series)
        {
            var seriesName = series.Name;
            widget = widget.Series(seriesName, row => row.Values[seriesName]);
        }

        return ApplyCommonColumnOptions(widget, chart);
    }

    private static BarChartWidget<Hex1bChartRow> ConfigureBarChart(BarChartWidget<Hex1bChartRow> widget, QueryChartDefinition chart)
    {
        widget = widget.Label(row => row.Label).Title(chart.Title ?? "Chart");
        foreach (var series in chart.Series)
        {
            var seriesName = series.Name;
            widget = widget.Series(seriesName, row => row.Values[seriesName]);
        }

        return ApplyCommonBarOptions(widget, chart);
    }

    private static TimeSeriesChartWidget<Hex1bChartRow> ConfigureTimeSeriesChart(TimeSeriesChartWidget<Hex1bChartRow> widget, QueryChartDefinition chart)
    {
        widget = widget.Label(row => row.Label)
            .Title(chart.Title ?? "Chart")
            .ShowGridLines();

        foreach (var series in chart.Series)
        {
            var seriesName = series.Name;
            widget = widget.Series(seriesName, row => row.Values[seriesName]);
        }

        return chart.Layout switch
        {
            QueryChartLayout.Stacked => widget.Layout(ChartLayout.Stacked),
            QueryChartLayout.Stacked100 => widget.Layout(ChartLayout.Stacked100),
            _ => widget
        };
    }

    private static ColumnChartWidget<Hex1bChartRow> ApplyCommonColumnOptions(ColumnChartWidget<Hex1bChartRow> widget, QueryChartDefinition chart)
    {
        widget = widget.ShowValues();
        return chart.Layout switch
        {
            QueryChartLayout.Grouped => widget.Layout(ChartLayout.Grouped),
            QueryChartLayout.Stacked => widget.Layout(ChartLayout.Stacked),
            QueryChartLayout.Stacked100 => widget.Layout(ChartLayout.Stacked100),
            _ => widget
        };
    }

    private static BarChartWidget<Hex1bChartRow> ApplyCommonBarOptions(BarChartWidget<Hex1bChartRow> widget, QueryChartDefinition chart)
    {
        widget = widget.ShowValues();
        return chart.Layout switch
        {
            QueryChartLayout.Grouped => widget.Layout(ChartLayout.Grouped),
            QueryChartLayout.Stacked => widget.Layout(ChartLayout.Stacked),
            QueryChartLayout.Stacked100 => widget.Layout(ChartLayout.Stacked100),
            _ => widget
        };
    }

    private static IReadOnlyList<Hex1bChartRow> BuildRows(QueryChartDefinition chart)
    {
        var rows = new List<Hex1bChartRow>(chart.Categories.Count);
        for (var categoryIndex = 0; categoryIndex < chart.Categories.Count; categoryIndex++)
        {
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var series in chart.Series)
            {
                values[series.Name] = series.Values[categoryIndex];
            }

            rows.Add(new Hex1bChartRow(chart.Categories[categoryIndex], values));
        }

        return rows;
    }

    private static string Normalize(string screenText)
    {
        var lines = screenText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var builder = new StringBuilder();

        var start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        var end = lines.Length - 1;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        for (var i = start; i <= end; i++)
        {
            builder.AppendLine(lines[i].TrimEnd());
        }

        return builder.ToString().TrimEnd();
    }

    private sealed record Hex1bChartRow(string Label, Dictionary<string, double> Values);
}
