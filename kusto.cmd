@echo off
setlocal

dotnet run --project ".\src\Kusto.Cli" -- %*
exit /b %ERRORLEVEL%
