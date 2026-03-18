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

function Get-CertificateSha512Thumbprint
{
    param([Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    $sha512 = [System.Security.Cryptography.SHA512]::Create()
    try
    {
        $hashBytes = $sha512.ComputeHash($Certificate.RawData)
        return ([System.BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()
    }
    finally
    {
        $sha512.Dispose()
    }
}

function Get-ExpectedSignerIssuerSha512Thumbprint
{
    param([Parameter(Mandatory)][string]$Path)

    $content = Get-Content -Path $Path -Raw
    $pattern = @'
(?m)^\$ExpectedSignerIssuerSha512Thumbprint\s*=\s*'(?<thumbprint>[0-9a-fA-F]{128})'
'@

    $match = [regex]::Match($content, $pattern)
    if (-not $match.Success)
    {
        throw "Installer script '$Path' did not define `$ExpectedSignerIssuerSha512Thumbprint as a 128-character hexadecimal value."
    }

    return $match.Groups['thumbprint'].Value.ToLowerInvariant()
}

function Get-ValidatedCertificateChain
{
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)][string]$Description,
        [switch]$IgnoreTimeValidity
    )

    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
    $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
    $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(15)
    $chain.ChainPolicy.VerificationFlags = if ($IgnoreTimeValidity)
    {
        [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::IgnoreNotTimeValid
    }
    else
    {
        [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
    }

    $ok = $chain.Build($Certificate)
    if ($ok)
    {
        return $chain
    }

    $statuses = @($chain.ChainStatus |
            Where-Object { $_.Status -ne [System.Security.Cryptography.X509Certificates.X509ChainStatusFlags]::NoError } |
            ForEach-Object {
                $statusText = $_.Status.ToString()
                $infoText = $_.StatusInformation.Trim()
                if ([string]::IsNullOrWhiteSpace($infoText))
                {
                    $statusText
                }
                else
                {
                    "$statusText ($infoText)"
                }
            })

    $statusMessage = if ($statuses.Count -gt 0)
    {
        $statuses -join '; '
    }
    else
    {
        'unknown chain validation failure'
    }

    throw "$Description certificate chain validation failed for '$binaryPath': $statusMessage"
}

$expectedThumbprint = Get-ExpectedSignerIssuerSha512Thumbprint -Path $installerScriptPath
$signature = Get-AuthenticodeSignature -FilePath $binaryPath
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid)
{
    $statusMessage = if ([string]::IsNullOrWhiteSpace($signature.StatusMessage)) { 'No additional details were provided.' } else { $signature.StatusMessage }
    throw "Authenticode signature validation failed for '$binaryPath': $($signature.Status) - $statusMessage"
}

if ($null -eq $signature.SignerCertificate)
{
    throw "Authenticode signature on '$binaryPath' did not include a signer certificate."
}

$signerChain = Get-ValidatedCertificateChain -Certificate $signature.SignerCertificate -Description 'Signer' -IgnoreTimeValidity
if ($signerChain.ChainElements.Count -lt 2)
{
    throw "Signer certificate chain for '$binaryPath' did not include an issuing certificate."
}

$issuerCertificate = $signerChain.ChainElements[1].Certificate
if ($null -eq $issuerCertificate)
{
    throw "Signer certificate chain for '$binaryPath' did not provide an issuing certificate."
}

$actualThumbprint = Get-CertificateSha512Thumbprint -Certificate $issuerCertificate
if (-not [string]::Equals($actualThumbprint, $expectedThumbprint, [System.StringComparison]::OrdinalIgnoreCase))
{
    throw "Signer issuer certificate for '$binaryPath' changed. Expected SHA512 '$expectedThumbprint' from '$installerScriptPath', but found '$actualThumbprint' on issuer '$($issuerCertificate.Subject)'. If this rotation is intentional, update '$installerScriptPath' to use the new SHA512 thumbprint '$actualThumbprint'."
}

Write-Host "Verified signer issuer certificate for '$binaryPath': '$($issuerCertificate.Subject)' ($actualThumbprint)."
