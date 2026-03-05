using System.Globalization;

namespace Kusto.Cli;

internal static class ListQueryBuilder
{
    public static BuiltListQuery Build(string baseCommand, string nameColumn, string? filterValue, int? takeValue)
    {
        if (string.IsNullOrWhiteSpace(baseCommand))
        {
            throw new ArgumentException("Base command is required.", nameof(baseCommand));
        }

        if (string.IsNullOrWhiteSpace(nameColumn))
        {
            throw new ArgumentException("Name column is required.", nameof(nameColumn));
        }

        if (takeValue is <= 0)
        {
            throw new UserFacingException("The --take value must be a positive integer.");
        }

        var filter = ParseFilter(filterValue);
        var command = baseCommand;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        if (filter is not null)
        {
            var filterLiteral = ToKqlStringLiteral(filter.Value);

            command = filter.Mode switch
            {
                FilterMode.Contains => $"{command} | where {nameColumn} contains {filterLiteral}",
                FilterMode.StartsWith => $"{command} | where {nameColumn} startswith {filterLiteral}",
                FilterMode.EndsWith => $"{command} | where {nameColumn} endswith {filterLiteral}",
                FilterMode.StartsWithAndEndsWith => $"{command} | where {nameColumn} startswith {filterLiteral} | where {nameColumn} endswith {filterLiteral}",
                _ => command
            };
        }

        if (takeValue.HasValue)
        {
            command = $"{command} | take {takeValue.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return new BuiltListQuery(command, parameters);
    }

    private static ParsedFilter? ParseFilter(string? filterValue)
    {
        if (filterValue is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(filterValue))
        {
            throw new UserFacingException("The --filter value cannot be empty. Use plain text, ^prefix, suffix$, or ^exact$.");
        }

        var startsWithAnchor = filterValue.StartsWith("^", StringComparison.Ordinal);
        var endsWithAnchor = filterValue.EndsWith("$", StringComparison.Ordinal);
        var valueStart = startsWithAnchor ? 1 : 0;
        var valueLength = filterValue.Length - valueStart - (endsWithAnchor ? 1 : 0);
        if (valueLength <= 0)
        {
            throw new UserFacingException("The --filter value is invalid. Use plain text, ^prefix, suffix$, or ^exact$.");
        }

        var value = filterValue.Substring(valueStart, valueLength);
        if (value.Contains('^') || value.Contains('$'))
        {
            throw new UserFacingException("The --filter value is invalid. Use plain text, ^prefix, suffix$, or ^exact$.");
        }

        var mode = (startsWithAnchor, endsWithAnchor) switch
        {
            (false, false) => FilterMode.Contains,
            (true, false) => FilterMode.StartsWith,
            (false, true) => FilterMode.EndsWith,
            (true, true) => FilterMode.StartsWithAndEndsWith
        };

        return new ParsedFilter(mode, value);
    }

    private enum FilterMode
    {
        Contains,
        StartsWith,
        EndsWith,
        StartsWithAndEndsWith
    }

    private sealed record ParsedFilter(FilterMode Mode, string Value);

    private static string ToKqlStringLiteral(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}

internal sealed record BuiltListQuery(string Command, IReadOnlyDictionary<string, string> Parameters);
