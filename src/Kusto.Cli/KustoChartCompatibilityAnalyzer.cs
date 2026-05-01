using System.Globalization;

namespace Kusto.Cli;

internal static class KustoChartCompatibilityAnalyzer
{
    public static QueryChartCompatibility Analyze(TabularData table, QueryVisualization visualization)
    {
        var renderKind = visualization.Visualization?.Trim().ToLowerInvariant();
        return renderKind switch
        {
            "piechart" => AnalyzePieChart(table, visualization),
            "columnchart" => AnalyzeCartesianChart(table, visualization, QueryChartKind.Column, horizontal: false),
            "barchart" => AnalyzeCartesianChart(table, visualization, QueryChartKind.Bar, horizontal: true),
            "linechart" or "timechart" => AnalyzeCartesianChart(table, visualization, QueryChartKind.Line, horizontal: false),
            _ => new QueryChartCompatibility
            {
                HumanReason = $"The '{visualization.Visualization}' render kind is not supported for terminal chart rendering.",
                MarkdownReason = $"The '{visualization.Visualization}' render kind is not supported for markdown chart rendering."
            }
        };
    }

    private static QueryChartCompatibility AnalyzePieChart(TabularData table, QueryVisualization visualization)
    {
        var xColumn = ResolveColumnName(table, visualization.XColumn) ?? table.Columns.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(xColumn))
        {
            return new QueryChartCompatibility
            {
                HumanReason = "This piechart can't be rendered in the terminal because the label column couldn't be determined.",
                MarkdownReason = "This piechart can't be rendered as Mermaid because the label column couldn't be determined."
            };
        }

        var valueColumns = ResolveYColumns(table, visualization, xColumn, []);
        if (valueColumns.Count != 1)
        {
            return new QueryChartCompatibility
            {
                HumanReason = "This piechart can't be rendered in the terminal because it requires exactly one numeric value column.",
                MarkdownReason = "This piechart requires exactly one numeric value column for Mermaid pie output."
            };
        }

        var labels = new List<string>();
        var values = new List<double>();
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var label = GetCellValue(table, rowIndex, xColumn);
            if (string.IsNullOrWhiteSpace(label))
            {
                return new QueryChartCompatibility
                {
                    HumanReason = "This piechart can't be rendered in the terminal because it contains an empty label value.",
                    MarkdownReason = "This piechart has an empty label value that Mermaid pie output can't represent safely."
                };
            }

            if (!TryGetRequiredDouble(table, rowIndex, valueColumns[0], out var value))
            {
                return new QueryChartCompatibility
                {
                    HumanReason = $"This piechart can't be rendered in the terminal because column '{valueColumns[0]}' contains a non-numeric value.",
                    MarkdownReason = $"This piechart can't be rendered as Mermaid because column '{valueColumns[0]}' contains a non-numeric value."
                };
            }

            if (value <= 0)
            {
                return new QueryChartCompatibility
                {
                    HumanReason = "This piechart can't be rendered in the terminal because pie segments require positive values greater than zero.",
                    MarkdownReason = "Mermaid pie charts require positive values greater than zero."
                };
            }

            labels.Add(label);
            values.Add(value);
        }

        if (labels.Count == 0)
        {
            return new QueryChartCompatibility
            {
                HumanReason = "This piechart can't be rendered in the terminal because the result contains no rows.",
                MarkdownReason = "This piechart can't be rendered as Mermaid because the result contains no rows."
            };
        }

        var definition = new QueryChartDefinition
        {
            Kind = QueryChartKind.Pie,
            Title = visualization.Title,
            Categories = labels,
            Series =
            [
                new QueryChartSeries(valueColumns[0], values)
            ]
        };

        return new QueryChartCompatibility
        {
            HumanChart = definition,
            MarkdownChart = definition
        };
    }

    private static QueryChartCompatibility AnalyzeCartesianChart(
        TabularData table,
        QueryVisualization visualization,
        QueryChartKind kind,
        bool horizontal)
    {
        var xColumn = ResolveColumnName(table, visualization.XColumn);
        if (xColumn == null)
        {
            // For timechart/linechart prefer a datetime column as X (the time bin is rarely first).
            xColumn = kind == QueryChartKind.Line
                ? FindDateTimeColumn(table) ?? table.Columns.FirstOrDefault()
                : table.Columns.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(xColumn))
        {
            return Unsupported(
                "This render can't be charted because the X column couldn't be determined.",
                "This render can't be charted because the X column couldn't be determined.");
        }

        var seriesColumns = ResolveSeriesColumns(table, visualization, xColumn);

        // When Kusto returns long-format grouped data (e.g. summarize ... by DimCol, bin(time,5m))
        // the Series field in the visualization may be unset. Auto-detect: any column that isn't
        // the X column, isn't numeric and isn't a datetime is a series-split dimension.
        if (seriesColumns.Count == 0)
        {
            seriesColumns = AutoDetectSeriesColumns(table, xColumn);
        }

        var yColumns = ResolveYColumns(table, visualization, xColumn, seriesColumns);
        if (yColumns.Count == 0)
        {
            return Unsupported(
                "This render can't be charted because no numeric Y columns could be determined.",
                "This render can't be charted because no numeric Y columns could be determined.");
        }

        if (seriesColumns.Count > 0 && yColumns.Count > 1)
        {
            return Unsupported(
                "This render can't be charted because grouped-series metadata combined with multiple Y columns isn't supported yet.",
                "This render can't be charted as Mermaid because grouped-series metadata combined with multiple Y columns isn't supported.");
        }

        var humanLayout = ResolveHumanLayout(visualization, kind);
        if (humanLayout is null)
        {
            return Unsupported(
                $"The '{visualization.Kind}' chart layout isn't supported for terminal chart rendering.",
                $"The '{visualization.Kind}' chart layout isn't supported for markdown chart rendering.");
        }

        var cartesianResult = seriesColumns.Count > 0
            ? BuildGroupedSeries(table, xColumn, seriesColumns, yColumns[0])
            : BuildWideSeries(table, xColumn, yColumns);

        if (cartesianResult.Reason is not null)
        {
            return Unsupported(cartesianResult.Reason, cartesianResult.Reason);
        }

        DateTime[]? dateTimeCategories = null;
        if (IsDateTimeColumn(table, xColumn) && cartesianResult.Categories is not null)
        {
            dateTimeCategories = ParseDateTimeCategories(cartesianResult.Categories);
            if (dateTimeCategories is not null)
            {
                // Kusto may return rows in arbitrary order when grouping. Sort categories
                // chronologically so lines connect adjacent time points instead of
                // zig-zagging across the chart.
                SortCategoriesChronologically(
                    ref dateTimeCategories,
                    ref cartesianResult);
            }
        }

        var definition = new QueryChartDefinition
        {
            Kind = kind,
            Horizontal = horizontal,
            Layout = humanLayout.Value,
            Title = visualization.Title,
            XTitle = visualization.XTitle,
            YTitle = visualization.YTitle,
            Categories = cartesianResult.Categories!,
            Series = cartesianResult.Series!,
            DateTimeCategories = dateTimeCategories
        };

        return new QueryChartCompatibility
        {
            HumanChart = definition,
            MarkdownChart = TryCreateMarkdownChart(definition, visualization, out var markdownReason),
            MarkdownReason = markdownReason
        };
    }

    private static QueryChartDefinition? TryCreateMarkdownChart(
        QueryChartDefinition definition,
        QueryVisualization visualization,
        out string? markdownReason)
    {
        if (definition.Kind == QueryChartKind.Line || definition.Kind == QueryChartKind.Column || definition.Kind == QueryChartKind.Bar)
        {
            if (definition.Layout != QueryChartLayout.Simple)
            {
                markdownReason = $"The '{visualization.Kind}' chart layout can't be represented faithfully as Mermaid xychart output.";
                return null;
            }

            if (definition.Series.Count != 1)
            {
                markdownReason = "Markdown chart output currently supports exactly one series for Mermaid xychart output.";
                return null;
            }

            markdownReason = null;
            return definition;
        }

        markdownReason = $"The '{visualization.Visualization}' render kind is not supported for Mermaid markdown output.";
        return null;
    }

    private static QueryChartLayout? ResolveHumanLayout(QueryVisualization visualization, QueryChartKind kind)
    {
        if (string.IsNullOrWhiteSpace(visualization.Kind))
        {
            return QueryChartLayout.Simple;
        }

        var layout = visualization.Kind.Trim().ToLowerInvariant();
        return kind switch
        {
            QueryChartKind.Line => layout switch
            {
                "default" or "unstacked" => QueryChartLayout.Simple,
                "stacked" => QueryChartLayout.Stacked,
                "stacked100" => QueryChartLayout.Stacked100,
                _ => null
            },
            _ => layout switch
            {
                "default" or "unstacked" => QueryChartLayout.Simple,
                "grouped" => QueryChartLayout.Grouped,
                "stacked" => QueryChartLayout.Stacked,
                "stacked100" => QueryChartLayout.Stacked100,
                _ => null
            }
        };
    }

    private static (IReadOnlyList<string>? Categories, IReadOnlyList<QueryChartSeries>? Series, string? Reason) BuildWideSeries(
        TabularData table,
        string xColumn,
        IReadOnlyList<string> yColumns)
    {
        var categories = new List<string>(table.Rows.Count);
        var seriesValues = yColumns.ToDictionary(column => column, _ => new List<double>(table.Rows.Count), StringComparer.OrdinalIgnoreCase);

        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            categories.Add(GetCellValue(table, rowIndex, xColumn) ?? string.Empty);

            foreach (var yColumn in yColumns)
            {
                if (!TryGetRequiredDouble(table, rowIndex, yColumn, out var value))
                {
                    return (null, null, $"Column '{yColumn}' contains a non-numeric or empty value that can't be charted.");
                }

                seriesValues[yColumn].Add(value);
            }
        }

        return (
            categories,
            yColumns.Select(column => new QueryChartSeries(column, seriesValues[column])).ToArray(),
            null);
    }

    private static (IReadOnlyList<string>? Categories, IReadOnlyList<QueryChartSeries>? Series, string? Reason) BuildGroupedSeries(
        TabularData table,
        string xColumn,
        IReadOnlyList<string> seriesColumns,
        string yColumn)
    {
        var categories = new List<string>();
        var categoryIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenPairs = new HashSet<(string SeriesName, int CategoryIndex)>();
        var seriesMap = new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);

        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var category = GetCellValue(table, rowIndex, xColumn) ?? string.Empty;
            if (!categoryIndex.TryGetValue(category, out var index))
            {
                index = categories.Count;
                categories.Add(category);
                categoryIndex[category] = index;
            }

            var seriesName = string.Join(" / ", seriesColumns.Select(column => GetCellValue(table, rowIndex, column) ?? string.Empty));
            if (!TryGetRequiredDouble(table, rowIndex, yColumn, out var value))
            {
                return (null, null, $"Column '{yColumn}' contains a non-numeric or empty value that can't be charted.");
            }

            if (!seenPairs.Add((seriesName, index)))
            {
                return (null, null, "The result contains duplicate X/series combinations that can't be charted deterministically.");
            }

            if (!seriesMap.TryGetValue(seriesName, out var points))
            {
                points = new Dictionary<int, double>();
                seriesMap[seriesName] = points;
            }

            points[index] = value;
        }

        var series = new List<QueryChartSeries>(seriesMap.Count);
        foreach (var pair in seriesMap.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var values = new double[categories.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = pair.Value.TryGetValue(i, out var v) ? v : double.NaN;
            }

            series.Add(new QueryChartSeries(pair.Key, values));
        }

        return (categories, series, null);
    }

    private static IReadOnlyList<string> ResolveSeriesColumns(TabularData table, QueryVisualization visualization, string xColumn)
    {
        if (visualization.Series is not { Count: > 0 })
        {
            return [];
        }

        var results = new List<string>(visualization.Series.Count);
        foreach (var column in visualization.Series)
        {
            var resolved = ResolveColumnName(table, column);
            if (!string.IsNullOrWhiteSpace(resolved) &&
                !string.Equals(resolved, xColumn, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(resolved);
            }
        }

        return results;
    }

    private static IReadOnlyList<string> ResolveYColumns(
        TabularData table,
        QueryVisualization visualization,
        string xColumn,
        IReadOnlyList<string> seriesColumns)
    {
        if (visualization.YColumns is { Count: > 0 })
        {
            var resolvedColumns = new List<string>(visualization.YColumns.Count);
            foreach (var column in visualization.YColumns)
            {
                var resolved = ResolveColumnName(table, column);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    resolvedColumns.Add(resolved);
                }
            }

            if (resolvedColumns.Count > 0)
            {
                return resolvedColumns;
            }
        }

        return table.Columns
            .Where(column => !string.Equals(column, xColumn, StringComparison.OrdinalIgnoreCase))
            .Where(column => !seriesColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
            .Where(column => IsNumericColumn(table, column))
            .ToArray();
    }

    private static string? ResolveColumnName(TabularData table, string? requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return null;
        }

        return table.TryGetColumnIndex(requestedName, out var index)
            ? table.Columns[index]
            : null;
    }

    private static string? FindDateTimeColumn(TabularData table)
    {
        foreach (var column in table.Columns)
        {
            if (IsDateTimeColumn(table, column))
            {
                return column;
            }
        }

        return null;
    }

    private static void SortCategoriesChronologically(
        ref DateTime[] dateTimeCategories,
        ref (IReadOnlyList<string>? Categories, IReadOnlyList<QueryChartSeries>? Series, string? Reason) cartesianResult)
    {
        var categories = cartesianResult.Categories!;
        var series = cartesianResult.Series!;
        var count = dateTimeCategories.Length;
        var dates = dateTimeCategories;

        // Build sort permutation: order[newIndex] = oldIndex
        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => dates[a].CompareTo(dates[b]));

        // If already sorted, nothing to do.
        var alreadySorted = true;
        for (var i = 0; i < count; i++)
        {
            if (order[i] != i)
            {
                alreadySorted = false;
                break;
            }
        }

        if (alreadySorted)
        {
            return;
        }

        var sortedDates = new DateTime[count];
        var sortedCategories = new string[count];
        for (var i = 0; i < count; i++)
        {
            sortedDates[i] = dateTimeCategories[order[i]];
            sortedCategories[i] = categories[order[i]];
        }

        var sortedSeries = new List<QueryChartSeries>(series.Count);
        foreach (var s in series)
        {
            var sortedValues = new double[count];
            for (var i = 0; i < count; i++)
            {
                sortedValues[i] = s.Values[order[i]];
            }

            sortedSeries.Add(new QueryChartSeries(s.Name, sortedValues));
        }

        dateTimeCategories = sortedDates;
        cartesianResult = (sortedCategories, sortedSeries, cartesianResult.Reason);
    }

    private static DateTime[]? ParseDateTimeCategories(IReadOnlyList<string> categories)
    {
        // Lenient: parse what we can. If at least one value parses, return the array
        // with unparseable entries set to DateTime.MinValue so chronological sort still
        // operates over the valid timestamps. If nothing parses, return null so the
        // caller falls back to ordinal X axis.
        var result = new DateTime[categories.Count];
        var anyParsed = false;
        for (var i = 0; i < categories.Count; i++)
        {
            if (DateTime.TryParse(
                    categories[i],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out result[i]))
            {
                anyParsed = true;
            }
            else
            {
                result[i] = DateTime.MinValue;
            }
        }

        return anyParsed ? result : null;
    }

    private static IReadOnlyList<string> AutoDetectSeriesColumns(TabularData table, string xColumn)
    {
        // Phase 1: string/non-numeric, non-datetime columns are always grouping dimensions.
        var obvious = table.Columns
            .Where(column => !string.Equals(column, xColumn, StringComparison.OrdinalIgnoreCase))
            .Where(column => !IsNumericColumn(table, column))
            .Where(column => !IsDateTimeColumn(table, column))
            .ToList();

        if (obvious.Count > 0)
        {
            return obvious;
        }

        // Phase 2: numeric grouping dimensions (e.g. Status=0/1/2). Only attempt this if
        // X values are duplicated (= long-format grouped data).
        if (!table.TryGetColumnIndex(xColumn, out var xColIndex))
        {
            return obvious;
        }

        var uniqueXValues = new HashSet<string?>(table.Rows.Select(row => row[xColIndex]), StringComparer.Ordinal);
        if (uniqueXValues.Count == table.Rows.Count)
        {
            // X is unique → wide format. All numeric columns are Y measurements.
            return [];
        }

        var rowCount = table.Rows.Count;
        var uniqueX = uniqueXValues.Count;

        // Pre-compute candidates so we can apply structural checks.
        var candidates = new List<(string Name, int Distinct, bool LooksLikeAggregate)>();
        foreach (var column in table.Columns)
        {
            if (string.Equals(column, xColumn, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsNumericColumn(table, column))
            {
                continue;
            }

            if (!table.TryGetColumnIndex(column, out var colIdx))
            {
                continue;
            }

            // Use parsed-double cardinality so "0", " 0", "0.0" don't count as distinct.
            var distinct = CountDistinctNumericValues(table, colIdx);
            candidates.Add((column, distinct, LooksLikeAggregateColumn(column)));
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        // Strong signal #1: aggregation-name pattern. A column named avg_Foo, sum_Bar,
        // count_, dcount_, percentile_, AvgCpu, P50Cpu, etc. is virtually always a
        // measurement, not a dimension. If at least one column matches the pattern,
        // every aggregate-named column is a Y measurement, and every non-aggregate
        // numeric column is a series-split.
        if (candidates.Any(c => c.LooksLikeAggregate))
        {
            return candidates
                .Where(c => !c.LooksLikeAggregate)
                .Select(c => c.Name)
                .ToList();
        }

        // Strong signal #2: structural fit. A series-split dimension D for long-format
        // (X, D, Y) data should satisfy: distinct(X) * distinct(D) ≈ rowCount, i.e.
        // each (X, D) combination appears about once. Allow ±20% slack for missing
        // points. Pick the column with the cardinality that fits best.
        var fitting = candidates
            .Where(c => c.Distinct >= 2 && c.Distinct <= uniqueX)
            .Select(c => new
            {
                c.Name,
                c.Distinct,
                Expected = (double)(uniqueX * c.Distinct),
                Ratio = (double)rowCount / Math.Max(1, uniqueX * c.Distinct)
            })
            .Where(c => c.Ratio is >= 0.8 and <= 1.2)
            .OrderBy(c => Math.Abs(c.Ratio - 1.0))
            .ToList();

        if (fitting.Count == 0)
        {
            // No column fits the long-format structure; treat all numeric as Y.
            return [];
        }

        // Pick the single best-fitting series column. Multiple grouping dims would
        // require Y-per-(D1,D2,...) which BuildGroupedSeries already handles, but
        // auto-promoting more than one is risky — keep it conservative.
        return [fitting[0].Name];
    }

    private static int CountDistinctNumericValues(TabularData table, int columnIndex)
    {
        var set = new HashSet<double>();
        var sawNonNumeric = false;
        foreach (var row in table.Rows)
        {
            if (columnIndex >= row.Count)
            {
                continue;
            }

            var raw = row[columnIndex];
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                set.Add(v);
            }
            else
            {
                sawNonNumeric = true;
            }
        }

        return sawNonNumeric ? set.Count + 1 : set.Count;
    }

    private static readonly System.Text.RegularExpressions.Regex AggregateColumnPattern =
        new(@"^(avg|sum|count|dcount|min|max|stdev|stdevp|variance|variancep|percentile|p\d+|median|countif|sumif|avgif)([_A-Z]|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool LooksLikeAggregateColumn(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return false;
        }

        return AggregateColumnPattern.IsMatch(columnName);
    }

    private static bool IsDateTimeColumn(TabularData table, string columnName)
    {
        if (!table.TryGetColumnIndex(columnName, out var columnIndex))
        {
            return false;
        }

        // Lenient: a column counts as datetime if a strong majority (≥80%) of its
        // non-empty values parse. A single corrupt row shouldn't disqualify the column.
        var parsed = 0;
        var nonEmpty = 0;
        foreach (var row in table.Rows)
        {
            if (columnIndex >= row.Count)
            {
                continue;
            }

            var value = row[columnIndex];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            nonEmpty++;
            if (DateTime.TryParse(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out _))
            {
                parsed++;
            }
        }

        if (nonEmpty == 0)
        {
            return false;
        }

        return parsed * 5 >= nonEmpty * 4; // ≥80%
    }

    private static bool IsNumericColumn(TabularData table, string columnName)
    {
        if (!table.TryGetColumnIndex(columnName, out var columnIndex))
        {
            return false;
        }

        var hasValue = false;
        foreach (var row in table.Rows)
        {
            if (columnIndex >= row.Count)
            {
                return false;
            }

            var value = row[columnIndex];
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }

            hasValue = true;
        }

        return hasValue;
    }

    private static bool TryGetRequiredDouble(TabularData table, int rowIndex, string columnName, out double value)
    {
        var raw = GetCellValue(table, rowIndex, columnName);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value);
    }

    private static string? GetCellValue(TabularData table, int rowIndex, string columnName)
    {
        if (!table.TryGetColumnIndex(columnName, out var columnIndex))
        {
            return null;
        }

        var row = table.Rows[rowIndex];
        return columnIndex < row.Count ? row[columnIndex] : null;
    }

    private static QueryChartCompatibility Unsupported(string humanReason, string markdownReason)
    {
        return new QueryChartCompatibility
        {
            HumanReason = humanReason,
            MarkdownReason = markdownReason
        };
    }
}
