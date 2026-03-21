using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kusto.Cli;

internal static class DatabaseSchemaJson
{
    public static TableSchemaDetails BuildTableSchemaDetails(
        string schemaJson,
        string database,
        string tableName,
        IReadOnlyList<string>? notes)
    {
        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var databaseElement = FindDatabaseElement(document.RootElement, database);
            if (!TryGetNamedProperty(databaseElement, "Tables", out var tablesElement) ||
                tablesElement.ValueKind != JsonValueKind.Object ||
                !TryGetNamedProperty(tablesElement, tableName, out var tableElement) ||
                tableElement.ValueKind != JsonValueKind.Object)
            {
                throw new UserFacingException($"Table '{tableName}' was not found.");
            }

            if (!TryGetNamedProperty(tableElement, "OrderedColumns", out var orderedColumnsElement) ||
                orderedColumnsElement.ValueKind != JsonValueKind.Array)
            {
                throw new UserFacingException($"Kusto returned a schema for table '{tableName}' without OrderedColumns.");
            }

            var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["TableName"] = GetStringProperty(tableElement, "Name") ?? tableName,
                ["DatabaseName"] = GetStringProperty(databaseElement, "Name") ?? database,
                ["ColumnCount"] = orderedColumnsElement.GetArrayLength().ToString(CultureInfo.InvariantCulture)
            };

            AddOptionalProperty(properties, tableElement, "Folder");
            AddOptionalProperty(properties, tableElement, "DocString");
            if (notes is { Count: > 0 })
            {
                properties["NoteCount"] = notes.Count.ToString(CultureInfo.InvariantCulture);
            }

            return new TableSchemaDetails
            {
                Properties = properties,
                Columns = BuildColumnsTable(orderedColumnsElement),
                NotesMessage = BuildNotesMessage(notes)
            };
        }
        catch (UserFacingException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new UserFacingException("Kusto returned an unexpected database schema format.", ex);
        }
    }

    public static string ExtractSchemaJson(TabularData result)
    {
        if (result.Rows.Count == 0)
        {
            throw new UserFacingException("Kusto did not return a database schema.");
        }

        var row = result.Rows[0];
        if (TryGetPreferredValue(result, row, "DatabaseSchema", out var preferredValue) ||
            TryGetPreferredValue(result, row, "Schema", out preferredValue))
        {
            return preferredValue!;
        }

        for (var i = 0; i < row.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(row[i]))
            {
                return row[i]!;
            }
        }

        throw new UserFacingException("Kusto returned an empty database schema payload.");
    }

    public static string? ExtractDatabaseSchemaVersion(string schemaJson, string database)
    {
        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var databaseElement = FindDatabaseElement(document.RootElement, database);

            var explicitVersion = GetStringProperty(databaseElement, "Version");
            if (!string.IsNullOrWhiteSpace(explicitVersion))
            {
                return explicitVersion;
            }

            if (TryGetNamedProperty(databaseElement, "MajorVersion", out var majorVersionElement) &&
                TryGetNamedProperty(databaseElement, "MinorVersion", out var minorVersionElement) &&
                TryGetInt32(majorVersionElement, out var majorVersion) &&
                TryGetInt32(minorVersionElement, out var minorVersion))
            {
                return $"v{majorVersion}.{minorVersion}";
            }

            return null;
        }
        catch (JsonException ex)
        {
            throw new UserFacingException("Kusto returned an unexpected database schema format.", ex);
        }
    }

    public static IReadOnlyList<string> GetTableNames(string schemaJson, string database)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var databaseElement = FindDatabaseElement(document.RootElement, database);
            if (!TryGetNamedProperty(databaseElement, "Tables", out var tablesElement) ||
                tablesElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return tablesElement
                .EnumerateObject()
                .Select(property => property.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        }
        catch (JsonException ex)
        {
            throw new UserFacingException("Kusto returned an unexpected database schema format.", ex);
        }
    }

    public static string RemoveTable(string schemaJson, string database, string tableName)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return string.Empty;
        }

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(schemaJson);
        }
        catch (JsonException ex)
        {
            throw new UserFacingException("Kusto returned an unexpected database schema format.", ex);
        }

        if (rootNode is null)
        {
            throw new UserFacingException("Kusto returned an empty database schema payload.");
        }

        var tablesNode = FindTablesNode(rootNode, database);
        if (tablesNode is not null)
        {
            var keyToRemove = tablesNode
                .Select(pair => pair.Key)
                .FirstOrDefault(key => string.Equals(key, tableName, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(keyToRemove))
            {
                tablesNode.Remove(keyToRemove);
            }

            if (tablesNode.Count == 0)
            {
                return string.Empty;
            }
        }

        return rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static TabularData BuildColumnsTable(JsonElement orderedColumnsElement)
    {
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var columnElement in orderedColumnsElement.EnumerateArray())
        {
            rows.Add(
            [
                GetStringProperty(columnElement, "Name"),
                GetStringProperty(columnElement, "Type") ?? GetStringProperty(columnElement, "DataType"),
                GetStringProperty(columnElement, "CslType"),
                GetStringProperty(columnElement, "DocString")
            ]);
        }

        return new TabularData(["Name", "Type", "CslType", "DocString"], rows);
    }

    private static string? BuildNotesMessage(IReadOnlyList<string>? notes)
    {
        if (notes is not { Count: > 0 })
        {
            return null;
        }

        var buffer = new StringBuilder();
        buffer.AppendLine("Table notes:");
        for (var i = 0; i < notes.Count; i++)
        {
            buffer.Append(i + 1);
            buffer.Append(". ");
            buffer.AppendLine(notes[i]);
        }

        return buffer.ToString().TrimEnd();
    }

    private static JsonElement FindDatabaseElement(JsonElement rootElement, string database)
    {
        if (TryGetNamedProperty(rootElement, "Databases", out var databasesElement) &&
            databasesElement.ValueKind == JsonValueKind.Object)
        {
            if (TryGetNamedProperty(databasesElement, database, out var databaseElement))
            {
                return databaseElement;
            }

            if (databasesElement.EnumerateObject().FirstOrDefault() is { Name: not null } firstDatabase)
            {
                return firstDatabase.Value;
            }
        }

        if (TryGetNamedProperty(rootElement, "Tables", out _))
        {
            return rootElement;
        }

        throw new UserFacingException($"Kusto did not return a schema for database '{database}'.");
    }

    private static JsonObject? FindTablesNode(JsonNode rootNode, string database)
    {
        if (rootNode is not JsonObject rootObject)
        {
            return null;
        }

        if (TryGetNamedNode(rootObject, "Databases", out var databasesNode) &&
            databasesNode is JsonObject databasesObject)
        {
            if (TryGetNamedNode(databasesObject, database, out var databaseNode) &&
                databaseNode is JsonObject databaseObject &&
                TryGetNamedNode(databaseObject, "Tables", out var tablesNode) &&
                tablesNode is JsonObject tablesObject)
            {
                return tablesObject;
            }

            var firstDatabase = databasesObject.FirstOrDefault(pair => pair.Value is JsonObject);
            if (firstDatabase.Value is JsonObject firstDatabaseObject &&
                TryGetNamedNode(firstDatabaseObject, "Tables", out var firstTablesNode) &&
                firstTablesNode is JsonObject firstTablesObject)
            {
                return firstTablesObject;
            }
        }

        if (TryGetNamedNode(rootObject, "Tables", out var directTablesNode) &&
            directTablesNode is JsonObject directTablesObject)
        {
            return directTablesObject;
        }

        return null;
    }

    private static bool TryGetNamedProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

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

    private static bool TryGetNamedNode(JsonObject obj, string propertyName, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(propertyName, out value))
        {
            return true;
        }

        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return TryGetNamedProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetInt32(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt32(out value);
        }

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetPreferredValue(
        TabularData result,
        IReadOnlyList<string?> row,
        string preferredColumnName,
        out string? value)
    {
        value = null;
        if (!result.TryGetColumnIndex(preferredColumnName, out var columnIndex) ||
            columnIndex < 0 ||
            columnIndex >= row.Count ||
            string.IsNullOrWhiteSpace(row[columnIndex]))
        {
            return false;
        }

        value = row[columnIndex];
        return true;
    }

    private static void AddOptionalProperty(IDictionary<string, string?> properties, JsonElement element, string propertyName)
    {
        var value = GetStringProperty(element, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            properties[propertyName] = value;
        }
    }
}
