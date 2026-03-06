#!/usr/bin/env sh
set -e

dotnet run --project "./src/Kusto.Cli/" -- "$@"