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
$expectedThumbprint = $config.ExpectedSignerIssuerSha512Thumbprint
$evidence = Get-WindowsBinaryTrustEvidence -BinaryPath $binaryPath

if (-not [string]::Equals($evidence.SignerIssuerSha512Thumbprint, $expectedThumbprint, [System.StringComparison]::Ordinal))
{
    throw "Signer issuer certificate for '$binaryPath' changed. Expected SHA512 '$expectedThumbprint' from '$installerScriptPath', but found '$($evidence.SignerIssuerSha512Thumbprint)' on issuer '$($evidence.SignerIssuerCertificate.Subject)'. If this rotation is intentional, update '$installerScriptPath' to use the new SHA512 thumbprint '$($evidence.SignerIssuerSha512Thumbprint)'."
}

Write-Host "Verified signer issuer certificate for '$binaryPath': '$($evidence.SignerIssuerCertificate.Subject)' ($($evidence.SignerIssuerSha512Thumbprint))."
