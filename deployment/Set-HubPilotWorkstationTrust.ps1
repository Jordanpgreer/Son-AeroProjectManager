<#
    PILOT ONLY. Installs or removes the public Son-Aero Hub pilot root on one named workstation.

    Run elevated and locally on the explicitly named pilot workstation. This script has no remote,
    domain, Group Policy, or company-wide mode and refuses all PFX files.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$BundleDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,62}$')]
    [string]$ExpectedComputerName,

    [ValidateSet('Install', 'Remove')]
    [string]$Operation = 'Install'
)

$ErrorActionPreference = 'Stop'
$forbiddenWorkstationNames = @('SON-IIS2', 'SON-SQL2')
$bundleType = 'SonAeroHubPilotPki'
$manifestFileName = 'SonAero-Hub-Pilot-Trust-Manifest.json'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-NormalizedThumbprint {
    param([Parameter(Mandatory)][string]$Thumbprint)
    return ($Thumbprint -replace '\s', '').ToUpperInvariant()
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    $hasher = [Security.Cryptography.SHA256]::Create(); try { return ([BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($Path)))).Replace('-', '') } finally { $hasher.Dispose() }
}

$currentComputer = [string]$env:COMPUTERNAME
if ([string]::IsNullOrWhiteSpace($currentComputer) -or $currentComputer -ine $ExpectedComputerName) {
    throw "This local pilot trust operation is restricted to $ExpectedComputerName; the current computer is '$currentComputer'."
}
if ($forbiddenWorkstationNames -icontains $currentComputer) {
    throw 'This workstation trust script cannot run on SON-IIS2 or SON-SQL2. Use the separate pilot server importer on SON-IIS2.'
}
if (-not $WhatIfPreference -and -not (Test-IsAdministrator)) {
    throw 'Run this script from an elevated Windows PowerShell session on the named pilot workstation.'
}

$fullBundleDirectory = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($BundleDirectory)).TrimEnd('\')
if (-not (Test-Path -LiteralPath $fullBundleDirectory -PathType Container)) {
    throw "BundleDirectory was not found: $fullBundleDirectory"
}
if (@(Get-ChildItem -LiteralPath $fullBundleDirectory -Recurse -File -Filter '*.pfx').Count -gt 0) {
    throw 'PILOT SECURITY STOP: workstation trust handoff must contain public files only; a PFX was found.'
}

$manifestPath = Join-Path $fullBundleDirectory $manifestFileName
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Use only the generated WORKSTATION-PILOT-TRUST directory; $manifestFileName was not found."
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.SchemaVersion -ne 1 -or $manifest.BundleType -ne $bundleType -or
    $manifest.HandoffType -ne 'WorkstationPilotTrustOnly' -or $manifest.PilotOnly -ne $true) {
    throw 'The supplied manifest is not a Son-Aero Hub pilot workstation trust manifest.'
}

$rootFileName = [string]$manifest.Files.RootPublicCer.Name
if ([string]::IsNullOrWhiteSpace($rootFileName) -or [IO.Path]::IsPathRooted($rootFileName) -or
    $rootFileName -ne [IO.Path]::GetFileName($rootFileName) -or $rootFileName.Contains('..')) {
    throw 'The pilot trust manifest contains an unsafe root certificate file name.'
}
$rootCerPath = [IO.Path]::GetFullPath((Join-Path $fullBundleDirectory $rootFileName))
if (-not $rootCerPath.StartsWith($fullBundleDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $rootCerPath -PathType Leaf)) {
    throw "The pilot public root certificate is missing: $rootFileName"
}
$expectedSha256 = [string]$manifest.Files.RootPublicCer.Sha256
if ($expectedSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'The pilot trust manifest root SHA-256 value is invalid.'
}
$actualSha256 = Get-Sha256 $rootCerPath
if ($actualSha256 -ne $expectedSha256.ToUpperInvariant()) {
    throw 'The pilot public root failed its SHA-256 integrity check. No trust-store changes were made.'
}

$rootCertificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($rootCerPath)
$rootThumbprint = Get-NormalizedThumbprint ([string]$manifest.Root.ThumbprintSha1)
if ($rootThumbprint -notmatch '^[A-F0-9]{40}$' -or
    (Get-NormalizedThumbprint $rootCertificate.Thumbprint) -ne $rootThumbprint -or
    $rootCertificate.Subject -ne $rootCertificate.Issuer) {
    throw 'The public root identity does not match the integrity-checked pilot manifest.'
}
$basicExtension = $rootCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' } | Select-Object -First 1
if (-not $basicExtension) {
    throw 'The pilot root has no Basic Constraints extension.'
}
$basic = New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($basicExtension, $basicExtension.Critical)
if (-not $basic.CertificateAuthority -or -not $basic.HasPathLengthConstraint -or $basic.PathLengthConstraint -ne 0) {
    throw 'The pilot root is not constrained to a path-length-zero CA.'
}

$rootStorePath = "Cert:\LocalMachine\Root\$rootThumbprint"
$rootPersonalStorePath = "Cert:\LocalMachine\My\$rootThumbprint"
$installed = Get-Item -LiteralPath $rootStorePath -ErrorAction SilentlyContinue
$privateCopy = Get-Item -LiteralPath $rootPersonalStorePath -ErrorAction SilentlyContinue
if ($Operation -eq 'Install' -and (($installed -and $installed.HasPrivateKey) -or $privateCopy)) {
    throw 'PILOT SECURITY STOP: a root private-key copy appears on this workstation. Remove it before trusting the public root.'
}
Write-Warning "PILOT ONLY: this changes trust on $currentComputer only. It does not deploy through AD, Group Policy, or any other computer."
Write-Warning "Verify the root SHA-256 fingerprint out of band before approving: $actualSha256"

if ($Operation -eq 'Install') {
    if ($installed) {
        [pscustomobject]@{
            Status = 'PILOT_WORKSTATION_ROOT_ALREADY_TRUSTED'
            PilotOnly = $true
            ComputerName = $currentComputer
            RootThumbprintSha1 = $rootThumbprint
            RootSha256 = $actualSha256
            Scope = 'LocalMachine on this workstation only'
        }
        return
    }
    if ($PSCmdlet.ShouldProcess($rootStorePath, "Trust the public Son-Aero Hub PILOT root on $currentComputer only")) {
        Import-Certificate -FilePath $rootCerPath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
        $installed = Get-Item -LiteralPath $rootStorePath -ErrorAction Stop
    }
    [pscustomobject]@{
        Status = if ($WhatIfPreference) { 'WHATIF_READY_PILOT_WORKSTATION_TRUST' } elseif ($installed) { 'PILOT_WORKSTATION_ROOT_TRUSTED' } else { 'PILOT_WORKSTATION_TRUST_CANCELLED' }
        PilotOnly = $true
        ComputerName = $currentComputer
        RootThumbprintSha1 = $rootThumbprint
        RootSha256 = $actualSha256
        Scope = 'LocalMachine on this workstation only'
    }
    return
}

$removed = $false
$dependentCertificates = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object {
    (Get-NormalizedThumbprint $_.Thumbprint) -ne $rootThumbprint -and
    $_.Issuer -eq $rootCertificate.Subject -and $_.NotAfter -gt (Get-Date)
})
if ($dependentCertificates.Count -gt 0) {
    throw 'Removal refuses to untrust the pilot root while an unexpired LocalMachine certificate still depends on it.'
}
if ($installed -and $PSCmdlet.ShouldProcess($rootStorePath, "Remove the Son-Aero Hub PILOT root from $currentComputer")) {
    Remove-Item -LiteralPath $rootStorePath -Force
    $installed = $null
    $removed = $true
}
if ($privateCopy -and $PSCmdlet.ShouldProcess($rootPersonalStorePath, "Remove the prohibited Son-Aero Hub PILOT root private-key copy from $currentComputer")) {
    Remove-Item -LiteralPath $rootPersonalStorePath -Force
    $removed = $true
}
[pscustomobject]@{
    Status = if ($WhatIfPreference) { 'WHATIF_READY_PILOT_WORKSTATION_TRUST_REMOVAL' } elseif ($removed) { 'PILOT_WORKSTATION_ROOT_REMOVED' } elseif ($installed) { 'PILOT_WORKSTATION_TRUST_REMOVAL_CANCELLED' } else { 'PILOT_WORKSTATION_ROOT_ALREADY_ABSENT' }
    PilotOnly = $true
    ComputerName = $currentComputer
    RootThumbprintSha1 = $rootThumbprint
    RootSha256 = $actualSha256
    Scope = 'LocalMachine on this workstation only'
}
