# Offline table data

`table show` uses on-disk offline table data by default. The CLI caches database schema snapshots, revalidates expired entries with `.show database ['<db>'] schema if_later_than "<version>" as json`, and stores per-table notes alongside that cached schema data.

That means the offline data currently covers:

- cached table schema details used by `table show`
- table-level docstrings
- column-level docstrings when Kusto returns them in the schema payload
- per-table notes created with `table notes`

## Defaults

- Default automatic schema caching: enabled
- Default cache paths:
  - Windows: `%LOCALAPPDATA%\kusto\schema-cache`
  - Linux: `$XDG_CACHE_HOME\kusto\schema-cache` or `~/.cache/kusto/schema-cache`
  - macOS: `~/Library/Caches/kusto/schema-cache`

## Environment overrides

- `KUSTO_SCHEMA_CACHE_ENABLED`
- `KUSTO_SCHEMA_CACHE_PATH`
- `KUSTO_SCHEMA_CACHE_TTL_SECONDS`

Set `KUSTO_SCHEMA_CACHE_ENABLED=false` or `"enabled": false` in `config.json` if you want to turn off automatic schema reuse for `table show`.

Notes and manual offline-data operations still use the same on-disk location, and `table show --refresh-offline-data` can repopulate the cache on demand.

## Example: PowerShell

```powershell
# Override the default TTL for this shell
$env:KUSTO_SCHEMA_CACHE_TTL_SECONDS = "3600"

# First call populates the offline data
kusto table show StormEvents --cluster help --database Samples

# Add a note that will be echoed back in later `table show` output
kusto table notes StormEvents --cluster help --database Samples --add "Use this for weather samples."

# Force a live refresh of the schema/docstrings and overwrite the cached copy
kusto table show StormEvents --cluster help --database Samples --refresh-offline-data

# Export/import offline data as JSON
kusto table --export-offline-data .\offline-table-data.json
kusto table --import-offline-data .\offline-table-data.json

# Purge or clear offline data
kusto table --purge-offline-data --force
kusto table StormEvents --cluster help --database Samples --clear-offline-data --force
```

## Example: config.json

```json
{
  "clusters": [
    {
      "name": "help",
      "url": "https://help.kusto.windows.net"
    }
  ],
  "defaultClusterUrl": "https://help.kusto.windows.net",
  "defaultDatabases": {
    "https://help.kusto.windows.net": "Samples"
  },
  "schemaCache": {
    "ttlSeconds": 86400,
    "overrides": [
      {
        "clusterUrl": "https://help.kusto.windows.net",
        "database": "Samples",
        "ttlSeconds": 3600
      }
    ]
  }
}
```

## Notes on destructive operations

- `table notes --clear` prompts before clearing all notes unless `--force` is supplied.
- `table <table> --clear-offline-data` prompts before removing cached schema data and notes for that table unless `--force` is supplied.
- `table --clear-offline-data` prompts before removing all offline table data unless `--force` is supplied.
- `table --purge-offline-data` prompts before verifying cached tables against live clusters/databases unless `--force` is supplied.
