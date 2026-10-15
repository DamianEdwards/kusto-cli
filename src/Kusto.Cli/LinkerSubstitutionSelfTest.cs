namespace Kusto.Cli;

internal static class LinkerSubstitutionSelfTest
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        // Unknown names are valid syntax: binding is intentionally left to the server.
        QueryValidator.Validate("UnknownTable | where UnknownColumn > 0 | take 5");
        QueryValidator.Validate("let events = datatable(value:long)[1, 2]; events | summarize sum(value)");
        QueryValidator.Validate(".show tables");
        QueryValidator.Validate(".show database ['Samples'] schema as json");

        try
        {
            QueryValidator.Validate("print 1\r\n| where");
            throw new UserFacingException("Trimming self-test failed: invalid query syntax was accepted.");
        }
        catch (UserFacingException ex) when (ex.Message.Contains("at line 2, column 8", StringComparison.Ordinal))
        {
        }

        const string text = "headless text self-test";
        if (!Hex1bHumanRenderer.RenderText(text).Contains(text, StringComparison.Ordinal))
        {
            throw new UserFacingException("Trimming self-test failed: headless text was not rendered.");
        }

        foreach (var kind in Enum.GetValues<QueryChartKind>())
        {
            var title = $"headless {kind} self-test";
            var chart = new QueryChartDefinition
            {
                Kind = kind,
                Title = title,
                Categories = ["alpha", "beta"],
                Series = [new QueryChartSeries("sample", [10, 20])]
            };
            var rendered = await Hex1bChartRenderer.RenderAsync(chart, cancellationToken);
            if (!rendered.PlainText.Contains(title, StringComparison.Ordinal) ||
                !rendered.PlainText.Contains("alpha", StringComparison.Ordinal) ||
                rendered.AnsiText is null ||
                !rendered.AnsiText.Contains("\u001b[", StringComparison.Ordinal))
            {
                throw new UserFacingException($"Trimming self-test failed: headless {kind} chart was not rendered.");
            }
        }
    }
}
