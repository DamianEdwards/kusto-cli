# Copilot instructions for `kusto`

## Build and test commands

Use these from the repository root:

```powershell
# Build all projects
dotnet build kusto.slnx

# Run the full test suite
dotnet test kusto.slnx

# Run a single test (xUnit filter by fully-qualified name)
dotnet test .\tests\Kusto.Cli.Tests\Kusto.Cli.Tests.csproj --filter "FullyQualifiedName~ParserTests.Parse_DatabaseList_AcceptsFilterAndTake"
```

There is no separate lint command configured in this repository.

## High-level architecture

### 1) CLI composition and command wiring
- `src/Kusto.Cli/Program.cs` is the minimal entry point: it builds the root command via `CommandFactory.CreateRootCommand()` and invokes parsing/execution.
- `src/Kusto.Cli/CommandFactory.cs` defines the entire command surface (`cluster`, `database`, `table`, `query`) and binds each action through `CliRunner.RunAsync(...)`.

### 2) Runtime composition and execution flow
- `src/Kusto.Cli/CliRunner.cs` is the command execution wrapper: it parses common options (`--format`, `--log-level`), constructs runtime dependencies, formats output, and centralizes error handling.
- `CliRunner.CreateRuntime(...)` wires `FileConfigStore`, `KustoConnectionResolver`, `AzureTokenProvider`, `KustoHttpService`, and `OutputFormatter` into a `CliRuntime`.
- `src/Kusto.Cli/Contracts.cs` defines the core interfaces (`IConfigStore`, `IKustoService`, `IKustoConnectionResolver`, `IOutputFormatter`) and `CliRuntime` container.

### 3) Data/config and connection resolution
- `src/Kusto.Cli/FileConfigStore.cs` persists config to `%USERPROFILE%\.kusto\config.json` (or `KUSTO_CONFIG_PATH` override).
- `src/Kusto.Cli/ClusterUtilities.cs` normalizes cluster URLs and config values; defaults and lookups rely on normalized URLs.
- `src/Kusto.Cli/KustoConnectionResolver.cs` resolves effective cluster/database from explicit options or configured defaults.

### 4) Kusto transport layer
- `src/Kusto.Cli/KustoHttpService.cs` calls Kusto REST endpoints:
  - management commands: `/v1/rest/mgmt`
  - queries: `/v2/rest/query`
- Response parsing handles both object payloads (`Tables`) and frame-array payloads (`FrameType == DataTable`), then selects `PrimaryResult` when available.
- Request payloads and output JSON use source-generated serializers (`src/Kusto.Cli/KustoJsonSerializerContext.cs`) for AOT-safe serialization.

### 5) Output and logging pipeline
- `src/Kusto.Cli/OutputFormatter.cs` supports `human`, `json`, and `markdown` output; human output uses `Spectre.Console` tables.
- Query result tables are flagged with `CliOutput.IsQueryResultTable` for query-specific table styling/alignment.
- `src/Kusto.Cli/Logging.cs` configures always-on file logging (`%TEMP%\kusto\kusto.log`) and optional stderr logging when `--log-level` is explicitly set.
- `src/Kusto.Cli/KustoConsoleFormatter.cs` and `src/Kusto.Cli/ConsoleStyling.cs` apply console styling (light-gray logs, red errors, ANSI-aware behavior).

### 6) Chart rendering pipeline
- `src/Kusto.Cli/KustoVisualizationExtractor.cs` parses Kusto `render` annotations from query responses into `QueryVisualization`.
- `src/Kusto.Cli/KustoChartCompatibilityAnalyzer.cs` is the compatibility gate for chart rendering. It maps Kusto render kinds to `QueryChartKind`, validates columns/layouts, and produces either a `HumanChart`, a `MarkdownChart`, or explicit reasons why rendering is not supported.
- `src/Kusto.Cli/Models.cs` defines the chart model surface: `QueryChartKind` (`Column`, `Bar`, `Line`, `Pie`) and `QueryChartLayout` (`Simple`, `Grouped`, `Stacked`, `Stacked100`).
- `src/Kusto.Cli/Hex1bChartRenderer.cs` is the terminal renderer used for human output. It renders `Column`, `Bar`, `Line`, and `Pie`.
- `src/Kusto.Cli/MermaidChartRenderer.cs` is the markdown renderer. It renders Mermaid `xychart` output for cartesian charts and Mermaid `pie` output for pie charts.
- In `src/Kusto.Cli/CommandFactory.cs`, `query --chart` chooses Hex1b for `human` output and Mermaid for `markdown`/`md`; JSON output does not support chart rendering.

## Key repository conventions

1. **User-facing errors must be actionable and implementation-agnostic**
   - Use `UserFacingException` for expected CLI/user errors.
   - Let `ErrorMapper`/`CliRunner` surface concise messages; do not expose stack traces or raw HTTP status-code wording to users.

2. **Authentication path is fixed**
   - Token acquisition uses `DefaultAzureCredential` only (`AzureTokenProvider`).
   - For credential/access failures, the UX guidance is to run `az login`; do not add alternate interactive auth flows in CLI commands.

3. **Always normalize cluster URLs before persistence or lookup**
   - Use `ClusterUtilities.NormalizeClusterUrl(...)` and `ClusterUtilities.NormalizeConfig(...)`.
   - Default database mappings are keyed by normalized cluster URL.

4. **List filtering behavior is centralized**
   - `database list` and `table list` filtering/take logic goes through `ListQueryBuilder.Build(...)`.
   - `--filter` supports anchor semantics:
     - `value` => `contains`
     - `^prefix` => `startswith`
     - `suffix$` => `endswith`
     - `^exact$` => startswith + endswith
   - Invalid `--filter`/`--take` must fail locally with `UserFacingException` before sending a request.

5. **Output formatting contract**
   - Command handlers should return `CliOutput` (`Message`, `Properties`, `Table`) and rely on `OutputFormatter`.
   - Prefer returning structured data and let formatter handle rendering differences across output modes.

6. **Chart compatibility rules are centralized**
   - Always route render-kind and layout decisions through `KustoChartCompatibilityAnalyzer`; do not duplicate chart compatibility logic in command handlers or formatters.
   - Current render-kind mapping is:
     - `columnchart` => `QueryChartKind.Column`
     - `barchart` => `QueryChartKind.Bar`
     - `linechart` and `timechart` => `QueryChartKind.Line`
     - `piechart` => `QueryChartKind.Pie`
   - Any other Kusto render kind should surface an explicit "not supported" reason rather than silently falling back.

7. **Human vs markdown chart support differs intentionally**
   - Terminal (`human` + `--chart`) currently supports `columnchart`, `barchart`, `linechart`/`timechart`, and `piechart`.
   - Markdown (`markdown`/`md` + `--chart`) supports those same chart kinds.
   - `piechart` terminal output should render via Hex1b's donut/pie support and include a legend with values/percentages when feasible.
   - Mermaid cartesian output currently requires `Simple` layout and exactly one series; if the chart can't be represented faithfully, preserve the table and emit the markdown reason instead of approximating.

8. **Layout support is chart-kind specific**
   - For line charts, supported human layouts are `default`/`unstacked`, `stacked`, and `stacked100`.
   - For column/bar charts, supported human layouts are `default`/`unstacked`, `grouped`, `stacked`, and `stacked100`.
   - Invalid or unsupported layouts must fail compatibility analysis with a clear reason before rendering.
