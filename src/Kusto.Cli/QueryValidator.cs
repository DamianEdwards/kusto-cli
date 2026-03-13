using Kusto.Language;

namespace Kusto.Cli;

internal static class QueryValidator
{
    public static void Validate(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new UserFacingException("The query text cannot be empty.");
        }

        var parsedQuery = KustoCode.Parse(query);
        var errors = parsedQuery.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        if (errors.Length == 0)
        {
            return;
        }

        var primaryError = errors[0];
        var additionalErrorsSuffix = errors.Length > 1
            ? $" (and {errors.Length - 1} more error(s))"
            : string.Empty;
        var locationSuffix = primaryError.HasLocation
            ? FormatLocation(query, primaryError.Start)
            : string.Empty;

        throw new UserFacingException($"The query is invalid{locationSuffix}: {primaryError.Message}{additionalErrorsSuffix}");
    }

    private static string FormatLocation(string query, int start)
    {
        var line = 1;
        var column = 1;

        for (var i = 0; i < start && i < query.Length; i++)
        {
            if (query[i] == '\r')
            {
                if (i + 1 < query.Length && query[i + 1] == '\n')
                {
                    i++;
                }

                line++;
                column = 1;
                continue;
            }

            if (query[i] == '\n')
            {
                line++;
                column = 1;
                continue;
            }

            column++;
        }

        return $" at line {line}, column {column}";
    }
}
