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
                HumanReason = "The 'piechart' render kind is not supported for terminal chart rendering.",
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
                    HumanReason = "The 'piechart' render kind is not supported for terminal chart rendering.",
                    MarkdownReason = "This piechart has an empty label value that Mermaid pie output can't represent safely."
                };
            }

            if (!TryGetRequiredDouble(table, rowIndex, valueColumns[0], out var value))
            {
                return new QueryChartCompatibility
                {
                    HumanReason = "The 'piechart' render kind is not supported for terminal chart rendering.",
                    MarkdownReason = $"This piechart can't be rendered as Mermaid because column '{valueColumns[0]}' contains a non-numeric value."
                };
            }

            if (value <= 0)
            {
                return new QueryChartCompatibility
                {
                    HumanReason = "The 'piechart' render kind is not supported for terminal chart rendering.",
                    MarkdownReason = "Mermaid pie charts require positive values greater than zero."
                };
            }

            labels.Add(label);
            values.Add(value);
        }

        return new QueryChartCompatibility
        {
            HumanReason = "The 'piechart' render kind is not supported for terminal chart rendering.",
            MarkdownChart = new QueryChartDefinition
            {
                Kind = QueryChartKind.Pie,
                Title = visualization.Title,
                Categories = labels,
                Series =
                [
                    new QueryChartSeries(valueColumns[0], values)
                ]
            }
        };
    }

    private static QueryChartCompatibility AnalyzeCartesianChart(
        TabularData table,
        QueryVisualization visualization,
        QueryChartKind kind,
        bool horizontal)
    {
        var xColumn = ResolveColumnName(table, visualization.XColumn) ?? table.Columns.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(xColumn))
        {
            return Unsupported(
                "This render can't be charted because the X column couldn't be determined.",
                "This render can't be charted because the X column couldn't be determined.");
        }

        var seriesColumns = ResolveSeriesColumns(table, visualization, xColumn);
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

        var definition = new QueryChartDefinition
        {
            Kind = kind,
            Horizontal = horizontal,
            Layout = humanLayout.Value,
            Title = visualization.Title,
            XTitle = visualization.XTitle,
            YTitle = visualization.YTitle,
            Categories = cartesianResult.Categories!,
            Series = cartesianResult.Series!
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
            if (pair.Value.Count != categories.Count)
            {
                return (null, null, "The result contains missing X/series combinations that can't be charted faithfully.");
            }

            var values = new double[categories.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = pair.Value[i];
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
