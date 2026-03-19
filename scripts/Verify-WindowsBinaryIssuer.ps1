[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BinaryPath,

    [string]$InstallerScriptPath = (Join-Path $PSScriptRoot 'install\install-kusto-cli.ps1')
)

$ErrorActionPreference = 'Stop'

$binaryPath = [System.IO.Path]::GetFullPath($BinaryPath)
$installerScriptPath = [System.IO.Path]::GetFullPath($InstallerScriptPath)

if (-not (Test-Path $binaryPath))
{
    throw "Signed binary '$binaryPath' was not found."
}

if (-not (Test-Path $installerScriptPath))
{
    throw "Installer script '$installerScriptPath' was not found."
}

Write-Verbose "Loading installer trust helpers from '$installerScriptPath'."
. $installerScriptPath -NoExecute

$config = Get-KustoInstallerTrustConfiguration
$expectedThumbprints = @($config.ExpectedSignerIssuerSha512Thumbprints)
$evidence = Get-WindowsBinaryTrustEvidence -BinaryPath $binaryPath

Assert-SignerIssuerSha512Thumbprint `
    -IssuerCertificate $evidence.SignerIssuerCertificate `
    -ActualThumbprint $evidence.SignerIssuerSha512Thumbprint `
    -ExpectedThumbprints $expectedThumbprints

$formattedExpectedThumbprints = ($expectedThumbprints | ForEach-Object { "'$_'" }) -join ', '
Write-Host "Verified signer issuer certificate for '$binaryPath': '$($evidence.SignerIssuerCertificate.Subject)' ($($evidence.SignerIssuerSha512Thumbprint)). Allowed SHA512 thumbprints: $formattedExpectedThumbprints."
