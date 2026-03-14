using System.Globalization;
using System.Text;
using System.Diagnostics;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Charts;
using Hex1b.Widgets;

namespace Kusto.Cli;

internal static class Hex1bChartRenderer
{
    private const int ChartWidth = 100;
    private const int ChartHeight = 24;
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RenderPollInterval = TimeSpan.FromMilliseconds(50);

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
        try
        {
            return await WaitForRenderedScreenAsync(terminal, chart, runTask, cancellationToken);
        }
        finally
        {
            cts.Cancel();
            try
            {
                await runTask;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<string> WaitForRenderedScreenAsync(
        Hex1bTerminal terminal,
        QueryChartDefinition chart,
        Task runTask,
        CancellationToken cancellationToken)
    {
        var expectedTitle = chart.Title ?? "Chart";
        string? lastRenderedScreen = null;
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < RenderTimeout)
        {
            if (runTask.IsFaulted)
            {
                await runTask;
            }

            using var snapshot = terminal.CreateSnapshot();
            var screenText = Normalize(snapshot.GetScreenText());
            if (!string.IsNullOrWhiteSpace(screenText) &&
                screenText.Contains(expectedTitle, StringComparison.Ordinal) &&
                string.Equals(screenText, lastRenderedScreen, StringComparison.Ordinal))
            {
                return screenText;
            }

            lastRenderedScreen = screenText;
            await Task.Delay(RenderPollInterval, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(lastRenderedScreen))
        {
            return lastRenderedScreen;
        }

        throw new InvalidOperationException("Timed out waiting for Hex1b to render the chart.");
    }

    private static Hex1bWidget BuildWidget(RootContext ctx, QueryChartDefinition chart)
    {
        return chart.Kind switch
        {
            QueryChartKind.Column => ConfigureColumnChart(ctx.ColumnChart(BuildRows(chart)), chart),
            QueryChartKind.Bar => ConfigureBarChart(ctx.BarChart(BuildRows(chart)), chart),
            QueryChartKind.Line => ConfigureTimeSeriesChart(ctx.TimeSeriesChart(BuildRows(chart)), chart),
            QueryChartKind.Pie => ConfigurePieChart(ctx, chart),
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

    private static Hex1bWidget ConfigurePieChart(RootContext ctx, QueryChartDefinition chart)
    {
        var items = BuildPieItems(chart);
        return ctx.HStack(h =>
        [
            h.DonutChart(items)
                .HoleSize(0)
                .Title(chart.Title ?? "Chart")
                .FillHeight(),
            h.Legend(items)
                .ShowValues(true)
                .ShowPercentages(true)
                .FormatValue(value => value.ToString("0.###", CultureInfo.InvariantCulture))
        ]);
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

    private static IReadOnlyList<ChartItem> BuildPieItems(QueryChartDefinition chart)
    {
        if (chart.Series.Count != 1)
        {
            throw new InvalidOperationException("Pie charts require exactly one series.");
        }

        var series = chart.Series[0];
        if (series.Values.Count != chart.Categories.Count)
        {
            throw new InvalidOperationException("Pie chart series values must match the category count.");
        }

        var items = new List<ChartItem>(chart.Categories.Count);
        for (var categoryIndex = 0; categoryIndex < chart.Categories.Count; categoryIndex++)
        {
            items.Add(new ChartItem(chart.Categories[categoryIndex], series.Values[categoryIndex]));
        }

        return items;
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
