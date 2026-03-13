using System.Globalization;
using System.Text.Json;

namespace Kusto.Cli;

internal static class KustoVisualizationExtractor
{
    public static QueryVisualization? Extract(IReadOnlyList<ParsedKustoTable> tables)
    {
        foreach (var table in tables)
        {
            if (!string.Equals(table.TableName, "@ExtendedProperties", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(table.TableKind, "QueryProperties", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                if (TryExtractPayload(table, rowIndex, out var payload))
                {
                    return Normalize(payload);
                }
            }
        }

        return null;
    }

    private static bool TryExtractPayload(ParsedKustoTable table, int rowIndex, out JsonElement payload)
    {
        var key = GetRowValue(table, rowIndex, "Key");
        if (string.Equals(key, "Visualization", StringComparison.OrdinalIgnoreCase) &&
            TryParseJsonObject(GetRowValue(table, rowIndex, "Value"), out payload))
        {
            return true;
        }

        var row = table.Rows[rowIndex];
        if (row.Count == 0)
        {
            payload = default;
            return false;
        }

        var value = row[0];
        if (table.Columns.Count > 0 &&
            string.Equals(table.Columns[0], "TableId", StringComparison.OrdinalIgnoreCase) &&
            row.Count > 2)
        {
            value = row[2];
        }

        return TryParseJsonObject(value, out payload) &&
            GetString(payload, "Visualization") is not null;
    }

    private static QueryVisualization? Normalize(JsonElement source)
    {
        var visualization = GetString(source, "Visualization");
        if (string.IsNullOrWhiteSpace(visualization))
        {
            return null;
        }

        var additionalProperties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in source.EnumerateObject())
        {
            if (IsKnownProperty(property.Name))
            {
                continue;
            }

            var converted = Convert(property.Value);
            if (!string.IsNullOrWhiteSpace(converted))
            {
                additionalProperties[property.Name] = converted;
            }
        }

        return new QueryVisualization
        {
            Visualization = visualization,
            Title = GetString(source, "Title"),
            XTitle = GetString(source, "XTitle"),
            YTitle = GetString(source, "YTitle"),
            XColumn = GetString(source, "XColumn"),
            YColumns = ParseList(source, "YColumns"),
            Series = ParseList(source, "Series"),
            Kind = GetString(source, "Kind"),
            Legend = GetString(source, "Legend"),
            YMin = GetDouble(source, "YMin"),
            YMax = GetDouble(source, "YMax"),
            AdditionalProperties = additionalProperties.Count > 0 ? additionalProperties : null,
            Raw = source.GetRawText()
        };
    }

    private static bool IsKnownProperty(string propertyName)
    {
        return propertyName.Equals("Visualization", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("Title", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("XTitle", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("YTitle", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("XColumn", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("YColumns", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("Series", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("Kind", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("Legend", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("YMin", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("YMax", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string>? ParseList(JsonElement source, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            JsonValueKind.Array => value
                .EnumerateArray()
                .Select(Convert)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item!)
                .ToArray(),
            _ => null
        };
    }

    private static string? GetString(JsonElement source, string propertyName)
    {
        return TryGetPropertyIgnoreCase(source, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double? GetDouble(JsonElement source, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            var parsed = value.GetDouble();
            return double.IsFinite(parsed) ? parsed : null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue))
        {
            return double.IsFinite(parsedValue) ? parsedValue : null;
        }

        return null;
    }

    private static string? GetRowValue(ParsedKustoTable table, int rowIndex, string columnName)
    {
        var columnIndex = GetColumnIndex(table.Columns, columnName);
        if (columnIndex < 0)
        {
            return null;
        }

        var row = table.Rows[rowIndex];
        return columnIndex < row.Count ? row[columnIndex] : null;
    }

    private static int GetColumnIndex(IReadOnlyList<string> columns, string columnName)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryParseJsonObject(string? rawValue, out JsonElement payload)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            payload = default;
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawValue);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                payload = default;
                return false;
            }

            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            payload = default;
            return false;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? Convert(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }
}
