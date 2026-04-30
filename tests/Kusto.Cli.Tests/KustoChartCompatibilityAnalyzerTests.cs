namespace Kusto.Cli.Tests;

public sealed class KustoChartCompatibilityAnalyzerTests
{
    [Fact]
    public void Analyze_ColumnChart_WithSingleSeries_IsCompatibleForHumanAndMarkdown()
    {
        var table = new TabularData(
            ["State", "Count"],
            [
                ["TEXAS", "4701"],
                ["KANSAS", "3166"]
            ]);
        var visualization = new QueryVisualization
        {
            Visualization = "columnchart",
            XColumn = "State",
            YColumns = ["Count"],
            Title = "Top states"
        };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.NotNull(result.MarkdownChart);
        Assert.Equal(QueryChartKind.Column, result.HumanChart!.Kind);
        Assert.Equal(["TEXAS", "KANSAS"], result.HumanChart.Categories);
        Assert.Equal(["Count"], result.HumanChart.Series.Select(series => series.Name).ToArray());
    }

    [Fact]
    public void Analyze_PieChart_IsCompatibleForHumanAndMarkdown()
    {
        var table = new TabularData(
            ["State", "Count"],
            [
                ["TEXAS", "4701"],
                ["KANSAS", "3166"]
            ]);
        var visualization = new QueryVisualization
        {
            Visualization = "piechart",
            XColumn = "State",
            YColumns = ["Count"],
            Title = "Top states"
        };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.Equal(QueryChartKind.Pie, result.HumanChart!.Kind);
        Assert.NotNull(result.MarkdownChart);
        Assert.Equal(QueryChartKind.Pie, result.MarkdownChart!.Kind);
    }

    [Fact]
    public void Analyze_PieChart_WithNoRows_IsRejectedForHumanAndMarkdown()
    {
        var table = new TabularData(
            ["State", "Count"],
            []);
        var visualization = new QueryVisualization
        {
            Visualization = "piechart",
            XColumn = "State",
            YColumns = ["Count"],
            Title = "Top states"
        };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.Null(result.HumanChart);
        Assert.Contains("no rows", result.HumanReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.MarkdownChart);
        Assert.Contains("no rows", result.MarkdownReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_StackedColumnChart_IsNotCompatibleForMarkdown()
    {
        var table = new TabularData(
            ["Month", "Revenue", "Expenses"],
            [
                ["Jan", "10", "8"],
                ["Feb", "11", "7"]
            ]);
        var visualization = new QueryVisualization
        {
            Visualization = "columnchart",
            XColumn = "Month",
            YColumns = ["Revenue", "Expenses"],
            Kind = "stacked"
        };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.Equal(QueryChartLayout.Stacked, result.HumanChart!.Layout);
        Assert.Null(result.MarkdownChart);
        Assert.Contains("can't be represented faithfully", result.MarkdownReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_GroupedChartWithMissingSeriesPoint_FillsWithNaN()
    {
        var table = new TabularData(
            ["Month", "Series", "Value"],
            [
                ["Jan", "A", "1"],
                ["Feb", "A", "2"],
                ["Jan", "B", "3"]
                // Feb/B is missing — should be filled with NaN
            ]);
        var visualization = new QueryVisualization
        {
            Visualization = "linechart",
            XColumn = "Month",
            Series = ["Series"],
            YColumns = ["Value"]
        };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.Equal(2, result.HumanChart!.Categories.Count);
        Assert.Equal(2, result.HumanChart.Series.Count);

        var seriesA = result.HumanChart.Series.First(s => s.Name == "A");
        var seriesB = result.HumanChart.Series.First(s => s.Name == "B");

        Assert.Equal(1.0, seriesA.Values[0]); // Jan/A
        Assert.Equal(2.0, seriesA.Values[1]); // Feb/A
        Assert.Equal(3.0, seriesB.Values[0]); // Jan/B
        Assert.True(double.IsNaN(seriesB.Values[1])); // Feb/B — missing, filled NaN
    }

    [Fact]
    public void Analyze_StackedLineChart_PreservesHumanLayout()
    {
        var table = new TabularData(
            ["Month", "Revenue", "Expenses"],
            [
                ["Jan", "10", "8"],
                ["Feb", "11", "7"]
            ]);
        var visualization = new QueryVisualization
        {
            Visualization = "linechart",
            XColumn = "Month",
            YColumns = ["Revenue", "Expenses"],
            Kind = "stacked"
        };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.Equal(QueryChartLayout.Stacked, result.HumanChart!.Layout);
        Assert.Null(result.MarkdownChart);
    }

    [Fact]
    public void Analyze_GroupedChart_WithNumericStatusAsSeriesColumn_DetectsStatusAsSeries()
    {
        // Simulate: summarize avg(ExecutionTime) by Status, bin(PreciseTimeStamp, 5m) | render timechart
        // Status is a low-cardinality integer (0, 1) — should be the series splitter, not a Y axis.
        var t0 = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc).ToString("o");
        var t1 = new DateTime(2024, 1, 15, 10, 5, 0, DateTimeKind.Utc).ToString("o");
        var table = new TabularData(
            ["Status", "PreciseTimeStamp", "avg_ExecutionTime"],
            [
                ["0", t0, "100"],
                ["1", t0, "200"],
                ["0", t1, "110"],
                ["1", t1, "210"]
            ]);
        var visualization = new QueryVisualization { Visualization = "timechart" };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        // Two series — one per Status value
        Assert.Equal(2, result.HumanChart!.Series.Count);
        // Each series has 2 time points
        Assert.All(result.HumanChart.Series, s => Assert.Equal(2, s.Values.Count));
        // DateTime categories populated
        Assert.NotNull(result.HumanChart.DateTimeCategories);
    }

    [Fact]
    public void Analyze_WideFormatTimechart_NumericColumnsBecomeSeries()
    {
        // Wide format: summarize val1=sum(A), val2=sum(B) by bin(time, 5m) | render timechart
        // X values are unique → both numeric columns are Y series, no series splitter.
        var t0 = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc).ToString("o");
        var t1 = new DateTime(2024, 1, 15, 10, 5, 0, DateTimeKind.Utc).ToString("o");
        var table = new TabularData(
            ["PreciseTimeStamp", "val1", "val2"],
            [
                [t0, "10", "20"],
                [t1, "11", "21"]
            ]);
        var visualization = new QueryVisualization { Visualization = "timechart" };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.Equal(2, result.HumanChart!.Series.Count);
        Assert.Contains(result.HumanChart.Series, s => s.Name == "val1");
        Assert.Contains(result.HumanChart.Series, s => s.Name == "val2");
    }

    [Fact]
    public void Analyze_DateTimeXValues_AreSortedChronologically_WhenRowsAreOutOfOrder()
    {
        // Rows arrive out-of-order (e.g. grouped by Status first, then time).
        var t0 = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2024, 1, 15, 10, 5, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2024, 1, 15, 10, 10, 0, DateTimeKind.Utc);
        var table = new TabularData(
            ["Status", "PreciseTimeStamp", "avg_ExecutionTime"],
            [
                ["0", t2.ToString("o"), "12"],
                ["0", t0.ToString("o"), "10"],
                ["1", t1.ToString("o"), "21"],
                ["0", t1.ToString("o"), "11"],
                ["1", t0.ToString("o"), "20"],
                ["1", t2.ToString("o"), "22"]
            ]);
        var visualization = new QueryVisualization { Visualization = "timechart" };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.NotNull(result.HumanChart!.DateTimeCategories);

        var dates = result.HumanChart.DateTimeCategories!;
        Assert.Equal(t0, dates[0]);
        Assert.Equal(t1, dates[1]);
        Assert.Equal(t2, dates[2]);

        var series0 = result.HumanChart.Series.First(s => s.Name == "0");
        Assert.Equal(10.0, series0.Values[0]);
        Assert.Equal(11.0, series0.Values[1]);
        Assert.Equal(12.0, series0.Values[2]);

        var series1 = result.HumanChart.Series.First(s => s.Name == "1");
        Assert.Equal(20.0, series1.Values[0]);
        Assert.Equal(21.0, series1.Values[1]);
        Assert.Equal(22.0, series1.Values[2]);
    }

    [Fact]
    public void Analyze_LongFormat_WithAggregateNamedYColumn_KeepsAggregateAsY_AndPicksDimensionAsSeries()
    {
        // summarize AvgCpu = avg(CounterValue) by Status, bin(Time, 1m)
        // Status is a low-cardinality int dimension; AvgCpu should remain Y, not be split.
        var t0 = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc).ToString("o");
        var t1 = new DateTime(2024, 1, 15, 10, 1, 0, DateTimeKind.Utc).ToString("o");
        var table = new TabularData(
            ["Status", "Time", "AvgCpu"],
            [
                ["0", t0, "10.5"],
                ["1", t0, "20.5"],
                ["0", t1, "11.5"],
                ["1", t1, "21.5"]
            ]);
        var visualization = new QueryVisualization { Visualization = "timechart" };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        // Two series, one per Status value; AvgCpu is the Y measurement.
        Assert.Equal(2, result.HumanChart!.Series.Count);
        Assert.Contains(result.HumanChart.Series, s => s.Name == "0");
        Assert.Contains(result.HumanChart.Series, s => s.Name == "1");
    }

    [Fact]
    public void Analyze_WideFormat_WithMultipleAggregateColumns_KeepsAllAsYSeries()
    {
        // summarize AvgCpu=avg(...), P50Cpu=percentile(...,50), P99Cpu=percentile(...,99) by bin(Time, 1m)
        // Each numeric column is an aggregate measurement; none should be a series-split.
        var t0 = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc).ToString("o");
        var t1 = new DateTime(2024, 1, 15, 10, 1, 0, DateTimeKind.Utc).ToString("o");
        var t2 = new DateTime(2024, 1, 15, 10, 2, 0, DateTimeKind.Utc).ToString("o");
        var table = new TabularData(
            ["Time", "AvgCpu", "P50Cpu", "P99Cpu"],
            [
                [t0, "30.0", "29.0", "60.0"],
                [t1, "31.5", "30.0", "61.0"],
                [t2, "32.0", "31.0", "62.0"]
            ]);
        var visualization = new QueryVisualization { Visualization = "timechart" };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.Equal(3, result.HumanChart!.Series.Count);
        Assert.Contains(result.HumanChart.Series, s => s.Name == "AvgCpu");
        Assert.Contains(result.HumanChart.Series, s => s.Name == "P50Cpu");
        Assert.Contains(result.HumanChart.Series, s => s.Name == "P99Cpu");
    }

    [Fact]
    public void Analyze_DateTimeXValues_PartialParseFailures_StillSorts()
    {
        // One row out of five has a corrupt timestamp; remaining rows should still
        // be detected as datetime and sorted chronologically.
        var t0 = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc).ToString("o");
        var t1 = new DateTime(2024, 1, 15, 10, 5, 0, DateTimeKind.Utc).ToString("o");
        var t2 = new DateTime(2024, 1, 15, 10, 10, 0, DateTimeKind.Utc).ToString("o");
        var t3 = new DateTime(2024, 1, 15, 10, 15, 0, DateTimeKind.Utc).ToString("o");
        var table = new TabularData(
            ["Time", "AvgCpu"],
            [
                [t2, "12"],
                [t0, "10"],
                ["garbage", "99"],
                [t3, "13"],
                [t1, "11"]
            ]);
        var visualization = new QueryVisualization { Visualization = "timechart" };

        var result = KustoChartCompatibilityAnalyzer.Analyze(table, visualization);

        Assert.NotNull(result.HumanChart);
        Assert.NotNull(result.HumanChart!.DateTimeCategories);
        // 4 valid timestamps + 1 garbage = 5 entries
        Assert.Equal(5, result.HumanChart.DateTimeCategories!.Length);
        // Garbage parsed as MinValue should sort to position 0; valid timestamps
        // sorted chronologically follow.
        var dates = result.HumanChart.DateTimeCategories;
        Assert.Equal(DateTime.MinValue, dates[0]);
        Assert.True(dates[1] < dates[2]);
        Assert.True(dates[2] < dates[3]);
        Assert.True(dates[3] < dates[4]);
    }
}
