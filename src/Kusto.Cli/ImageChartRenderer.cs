using SP = ScottPlot;

namespace Kusto.Cli;

internal static class ImageChartRenderer
{
    public static void RenderPng(QueryChartDefinition chart, int width, int height, Stream output)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(output);

        if (width < ChartStyle.MinDimension || width > ChartStyle.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height < ChartStyle.MinDimension || height > ChartStyle.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        ValidateChart(chart);

        var plt = new SP.Plot();
        plt.FigureBackground.Color = SP.Colors.White;

        switch (chart.Kind)
        {
            case QueryChartKind.Pie:
                AddPie(plt, chart);
                break;
            case QueryChartKind.Line:
                AddLine(plt, chart);
                break;
            case QueryChartKind.Column:
                AddBars(plt, chart, horizontal: false);
                break;
            case QueryChartKind.Bar:
                AddBars(plt, chart, horizontal: true);
                break;
        }

        if (!string.IsNullOrWhiteSpace(chart.Title))
        {
            plt.Title(chart.Title);
        }

        if (!string.IsNullOrWhiteSpace(chart.XTitle))
        {
            plt.XLabel(chart.XTitle);
        }

        if (!string.IsNullOrWhiteSpace(chart.YTitle))
        {
            plt.YLabel(chart.YTitle);
        }

        output.Write(plt.GetImage(width, height).GetImageBytes());
    }

    private static void ValidateChart(QueryChartDefinition chart)
    {
        var expected = chart.Categories.Count;
        for (var i = 0; i < chart.Series.Count; i++)
        {
            var s = chart.Series[i];
            if (s.Values.Count != expected)
            {
                throw new InvalidOperationException(
                    $"Chart series '{s.Name}' has {s.Values.Count} values but {expected} categories. " +
                    "All series must align with categories.");
            }
        }

        if (chart.DateTimeCategories is not null && chart.DateTimeCategories.Length != expected)
        {
            throw new InvalidOperationException(
                $"Chart has {chart.DateTimeCategories.Length} datetime categories but {expected} string categories.");
        }
    }

    private static SP.Color SeriesColor(int index)
    {
        var (r, g, b) = ChartStyle.SeriesColorRgb(index);
        return new SP.Color(r, g, b);
    }

    private static void AddPie(SP.Plot plt, QueryChartDefinition chart)
    {
        if (chart.Series.Count == 0 || chart.Categories.Count == 0)
        {
            return;
        }

        var slices = new List<SP.PieSlice>(chart.Categories.Count);
        for (var i = 0; i < chart.Categories.Count; i++)
        {
            var v = chart.Series[0].Values[i];
            // Skip NaN/non-positive slices rather than aborting the whole render.
            if (double.IsNaN(v) || v <= 0)
            {
                continue;
            }

            slices.Add(new SP.PieSlice
            {
                Value = v,
                FillColor = SeriesColor(i),
                LegendText = chart.Categories[i],
                Label = chart.Categories[i],
            });
        }

        if (slices.Count == 0)
        {
            return;
        }

        plt.Add.Pie(slices);
        plt.HideAxesAndGrid();
        plt.Legend.IsVisible = true;
    }

    private static void AddLine(SP.Plot plt, QueryChartDefinition chart)
    {
        if (chart.Series.Count == 0 || chart.Categories.Count == 0)
        {
            return;
        }

        var isDatetime = chart.DateTimeCategories is not null;
        var catCount = chart.Categories.Count;

        var xValues = isDatetime
            ? chart.DateTimeCategories!.Select(d => d.ToOADate()).ToArray()
            : Enumerable.Range(0, catCount).Select(i => (double)i).ToArray();

        // For stacked layouts, accumulate y values across series.
        // NaN values are treated as 0 for the stack accumulator (the "missing"
        // contribution is zero) but the original NaN is preserved in the Y array
        // so ScottPlot creates a gap at that point.
        var cumulative = chart.Layout is QueryChartLayout.Stacked or QueryChartLayout.Stacked100
            ? new double[catCount]
            : null;

        double[]? totals = null;
        if (chart.Layout == QueryChartLayout.Stacked100)
        {
            totals = new double[catCount];
            foreach (var s in chart.Series)
            {
                for (var i = 0; i < s.Values.Count; i++)
                {
                    if (!double.IsNaN(s.Values[i]))
                    {
                        totals[i] += s.Values[i];
                    }
                }
            }
        }

        for (var si = 0; si < chart.Series.Count; si++)
        {
            var series = chart.Series[si];
            double[] ys;

            if (cumulative is null)
            {
                ys = series.Values.ToArray();
            }
            else
            {
                ys = new double[catCount];
                for (var i = 0; i < catCount; i++)
                {
                    if (double.IsNaN(series.Values[i]))
                    {
                        // Preserve gap; cumulative does not advance.
                        ys[i] = double.NaN;
                        continue;
                    }

                    var v = series.Values[i];
                    if (totals is not null)
                    {
                        v = totals[i] > 0 ? v / totals[i] * 100 : 0;
                    }

                    cumulative[i] += v;
                    ys[i] = cumulative[i];
                }
            }

            var scatter = plt.Add.Scatter(xValues, ys);
            scatter.Color = SeriesColor(si);
            scatter.LineWidth = 2;
            scatter.MarkerSize = 0;
            scatter.LegendText = series.Name;
        }

        if (isDatetime)
        {
            plt.Axes.DateTimeTicksBottom();
        }
        else
        {
            plt.Axes.Bottom.SetTicks(
                Enumerable.Range(0, catCount).Select(i => (double)i).ToArray(),
                chart.Categories.ToArray());
        }

        if (chart.Series.Count > 1)
        {
            plt.Legend.IsVisible = true;
        }

        ClampYFloorToZeroIfNonNegative(plt, chart);
    }

    private static void AddBars(SP.Plot plt, QueryChartDefinition chart, bool horizontal)
    {
        if (chart.Series.Count == 0 || chart.Categories.Count == 0)
        {
            return;
        }

        var catCount = chart.Categories.Count;
        var seriesCount = chart.Series.Count;
        var isStacked = chart.Layout is QueryChartLayout.Stacked or QueryChartLayout.Stacked100;
        var isStacked100 = chart.Layout == QueryChartLayout.Stacked100;

        var totals = new double[catCount];
        if (isStacked100)
        {
            foreach (var s in chart.Series)
            {
                for (var i = 0; i < catCount; i++)
                {
                    if (!double.IsNaN(s.Values[i]))
                    {
                        totals[i] += s.Values[i];
                    }
                }
            }
        }

        if (isStacked)
        {
            var bottoms = new double[catCount];
            for (var si = 0; si < seriesCount; si++)
            {
                var series = chart.Series[si];
                var color = SeriesColor(si);
                var bars = new List<SP.Bar>(catCount);
                for (var i = 0; i < catCount; i++)
                {
                    if (double.IsNaN(series.Values[i]))
                    {
                        // Skip the bar entirely — leaves a visual gap and doesn't shift bottoms.
                        continue;
                    }

                    var v = series.Values[i];
                    if (isStacked100)
                    {
                        v = totals[i] > 0 ? v / totals[i] * 100 : 0;
                    }

                    bars.Add(new SP.Bar
                    {
                        Position = i,
                        Value = bottoms[i] + v,
                        ValueBase = bottoms[i],
                        FillColor = color,
                        Orientation = horizontal ? SP.Orientation.Horizontal : SP.Orientation.Vertical
                    });
                    bottoms[i] += v;
                }

                if (bars.Count > 0)
                {
                    var bp = plt.Add.Bars(bars);
                    bp.LegendText = series.Name;
                }
            }
        }
        else
        {
            var groupWidth = 0.8;
            var barWidth = seriesCount > 1 ? groupWidth / seriesCount : groupWidth;
            var startOffset = seriesCount > 1 ? -groupWidth / 2.0 + barWidth / 2.0 : 0;

            for (var si = 0; si < seriesCount; si++)
            {
                var series = chart.Series[si];
                var color = SeriesColor(si);
                var seriesOffset = startOffset + si * barWidth;
                var bars = new List<SP.Bar>(catCount);
                for (var i = 0; i < catCount; i++)
                {
                    if (double.IsNaN(series.Values[i]))
                    {
                        continue;
                    }

                    bars.Add(new SP.Bar
                    {
                        Position = i + seriesOffset,
                        Value = series.Values[i],
                        FillColor = color,
                        Size = barWidth * 0.9,
                        Orientation = horizontal ? SP.Orientation.Horizontal : SP.Orientation.Vertical
                    });
                }

                if (bars.Count > 0)
                {
                    var bp = plt.Add.Bars(bars);
                    bp.LegendText = series.Name;
                }
            }
        }

        var tickPositions = Enumerable.Range(0, catCount).Select(i => (double)i).ToArray();
        var tickLabels = chart.Categories.ToArray();

        if (horizontal)
        {
            plt.Axes.Left.SetTicks(tickPositions, tickLabels);
        }
        else
        {
            plt.Axes.Bottom.SetTicks(tickPositions, tickLabels);
        }

        if (seriesCount > 1)
        {
            plt.Legend.IsVisible = true;
        }

        if (isStacked100)
        {
            if (horizontal)
            {
                plt.Axes.SetLimitsX(0, 105);
            }
            else
            {
                plt.Axes.SetLimitsY(0, 105);
            }
        }
        else
        {
            ClampYFloorToZeroIfNonNegative(plt, chart);
        }
    }

    private static void ClampYFloorToZeroIfNonNegative(SP.Plot plt, QueryChartDefinition chart)
    {
        var allNonNegative = chart.Series
            .SelectMany(s => s.Values)
            .Where(v => !double.IsNaN(v))
            .All(v => v >= 0);

        if (!allNonNegative)
        {
            return;
        }

        plt.Axes.AutoScale();
        var limits = plt.Axes.GetLimits();
        if (limits.Bottom < 0)
        {
            plt.Axes.SetLimitsY(0, limits.Top);
        }
    }
}
