[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Interactive Windows PowerShell loads PSReadLine before running the installer.
Import-Module PSReadLine -ErrorAction Stop
. (Join-Path $PSScriptRoot 'install\install-kusto-cli.ps1') -NoExecute

$savedArchitecture = $env:PROCESSOR_ARCHITECTURE
$savedNativeArchitecture = $env:PROCESSOR_ARCHITEW6432

try
{
    $actualArchitecture = Get-WindowsArchitecture
    if ($actualArchitecture -notin @('x64', 'arm64'))
    {
        throw "Expected a supported architecture with PSReadLine loaded, but got '$actualArchitecture'."
    }

    $cases = @(
        @{ Process = 'AMD64'; Native = $null; Expected = 'x64' }
        @{ Process = 'ARM64'; Native = $null; Expected = 'arm64' }
        @{ Process = 'amd64'; Native = ''; Expected = 'x64' }
        @{ Process = 'arm64'; Native = ' '; Expected = 'arm64' }
        @{ Process = 'x86'; Native = 'AMD64'; Expected = 'x64' }
        @{ Process = 'x86'; Native = 'ARM64'; Expected = 'arm64' }
        @{ Process = 'AMD64'; Native = 'ARM64'; Expected = 'arm64' }
        @{ Process = $null; Native = 'AMD64'; Expected = 'x64' }
    )

    foreach ($case in $cases)
    {
        $env:PROCESSOR_ARCHITECTURE = $case.Process
        $env:PROCESSOR_ARCHITEW6432 = $case.Native
        $actual = Get-WindowsArchitecture
        if ($actual -ne $case.Expected)
        {
            throw "Architecture detection for process '$($case.Process)' / native '$($case.Native)' returned '$actual' instead of '$($case.Expected)'."
        }
    }

    $failureCases = @(
        @{ Process = 'x86'; Native = $null; Message = "Unsupported Windows architecture 'x86'. Only x64 and arm64 are supported." }
        @{ Process = 'AMD64'; Native = 'unsupported'; Message = "Unsupported Windows architecture 'unsupported'. Only x64 and arm64 are supported." }
        @{ Process = $null; Native = $null; Message = 'Unable to determine Windows architecture from PROCESSOR_ARCHITEW6432 or PROCESSOR_ARCHITECTURE. Only x64 and arm64 are supported.' }
        @{ Process = ' '; Native = ' '; Message = 'Unable to determine Windows architecture from PROCESSOR_ARCHITEW6432 or PROCESSOR_ARCHITECTURE. Only x64 and arm64 are supported.' }
    )

    foreach ($case in $failureCases)
    {
        $env:PROCESSOR_ARCHITECTURE = $case.Process
        $env:PROCESSOR_ARCHITEW6432 = $case.Native
        $message = $null
        try
        {
            $null = Get-WindowsArchitecture
        }
        catch
        {
            $message = $_.Exception.Message
        }

        if ($message -ne $case.Message)
        {
            throw "Expected architecture detection to fail with '$($case.Message)', but got '$message'."
        }
    }

    Write-Host 'Installer architecture validation passed with PSReadLine loaded.'
}
finally
{
    $env:PROCESSOR_ARCHITECTURE = $savedArchitecture
    $env:PROCESSOR_ARCHITEW6432 = $savedNativeArchitecture
}
