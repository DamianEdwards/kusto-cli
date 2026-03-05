using System.Globalization;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kusto.Cli;

public sealed class OutputFormatter : IOutputFormatter
{
    public string Format(CliOutput output, OutputFormat format)
    {
        return format switch
        {
            OutputFormat.Json => JsonSerializer.Serialize(output, KustoJsonSerializerContext.Default.CliOutput),
            OutputFormat.Markdown => FormatMarkdown(output),
            _ => FormatHuman(output)
        };
    }

    private static string FormatHuman(CliOutput output)
    {
        using var writer = new StringWriter();
        var useAnsi = ConsoleRendering.ShouldUseAnsiForStandardOutput();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = useAnsi ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = useAnsi ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
            Interactive = useAnsi ? InteractionSupport.Yes : InteractionSupport.No
        });

        var wroteSection = false;
        if (!string.IsNullOrWhiteSpace(output.Message))
        {
            console.MarkupLine(Markup.Escape(output.Message));
            wroteSection = true;
        }

        if (output.Properties is { Count: > 0 })
        {
            if (wroteSection)
            {
                console.WriteLine();
            }

            var propertiesTable = new Table().Border(TableBorder.Rounded).Expand();
            propertiesTable.AddColumn(new TableColumn("Property"));
            propertiesTable.AddColumn(new TableColumn("Value"));

            foreach (var pair in output.Properties.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                propertiesTable.AddRow(
                    Markup.Escape(pair.Key),
                    Markup.Escape(pair.Value ?? string.Empty));
            }

            console.Write(propertiesTable);
            wroteSection = true;
        }

        if (output.Table is not null)
        {
            if (wroteSection)
            {
                console.WriteLine();
            }

            var table = CreateDataTable(output.Table, output.IsQueryResultTable, useAnsi);

            console.Write(table);
        }

        return writer.ToString().TrimEnd();
    }

    private static string FormatMarkdown(CliOutput output)
    {
        var buffer = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(output.Message))
        {
            buffer.AppendLine(output.Message);
            buffer.AppendLine();
        }

        if (output.Properties is not null && output.Properties.Count > 0)
        {
            buffer.AppendLine("| Property | Value |");
            buffer.AppendLine("|---|---|");
            foreach (var pair in output.Properties.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                buffer.AppendLine($"| {EscapeMarkdown(pair.Key)} | {EscapeMarkdown(pair.Value ?? string.Empty)} |");
            }
            buffer.AppendLine();
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

        return buffer.ToString().TrimEnd();
    }

    private static string EscapeMarkdown(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static Table CreateDataTable(TabularData data, bool isQueryResultTable, bool useAnsi)
    {
        var table = new Table
        {
            Border = isQueryResultTable ? TableBorder.MinimalHeavyHead : TableBorder.Rounded,
            Expand = !isQueryResultTable,
            ShowRowSeparators = isQueryResultTable,
            UseSafeBorder = !useAnsi
        };

        if (data.Columns.Count == 0)
        {
            table.AddColumn(new TableColumn("Value"));
            foreach (var row in data.Rows)
            {
                table.AddRow(Markup.Escape(string.Join(", ", row.Select(value => value ?? string.Empty))));
            }

            return table;
        }

        var rightAlignedColumns = new bool[data.Columns.Count];
        for (var i = 0; i < data.Columns.Count; i++)
        {
            var column = new TableColumn(Markup.Escape(data.Columns[i]));
            if (isQueryResultTable && ShouldRightAlignColumn(data, i))
            {
                rightAlignedColumns[i] = true;
            }

            table.AddColumn(column);
        }

        foreach (var row in data.Rows)
        {
            var rowCells = new IRenderable[data.Columns.Count];
            for (var i = 0; i < data.Columns.Count; i++)
            {
                var value = i < row.Count ? row[i] : string.Empty;
                IRenderable renderedCell = new Markup(Markup.Escape(value ?? string.Empty));
                if (isQueryResultTable && rightAlignedColumns[i])
                {
                    renderedCell = Align.Right(renderedCell);
                }

                rowCells[i] = renderedCell;
            }

            table.AddRow(rowCells);
        }

        return table;
    }

    private static bool ShouldRightAlignColumn(TabularData data, int columnIndex)
    {
        var hasValue = false;
        foreach (var row in data.Rows)
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

            hasValue = true;
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return hasValue;
    }
}
