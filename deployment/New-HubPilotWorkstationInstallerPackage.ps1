<# Builds one manifest-locked workstation ZIP for the two-person HTTPS pilot. #>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$TrustBundleDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,62}$')]
    [string]$ExpectedComputerName,

    [Parameter(Mandatory)]
    [ValidatePattern('^[^\\\s]+\\[^\\\s]+$')]
    [string]$ExpectedAccountName,

    [ValidatePattern('^https://SON-IIS2:6140/?$')]
    [string]$HubUri = 'https://SON-IIS2:6140',

    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

$resolvedTrustBundle = [IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($TrustBundleDirectory)).TrimEnd('\')
$trustManifestPath = Join-Path $resolvedTrustBundle 'SonAero-Hub-Pilot-Trust-Manifest.json'
$rootCertificatePath = Join-Path $resolvedTrustBundle 'SonAero-Hub-Pilot-Root-PUBLIC.cer'
foreach ($path in @($trustManifestPath, $rootCertificatePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The generated workstation trust bundle is incomplete: $path"
    }
}
if (@(Get-ChildItem -LiteralPath $resolvedTrustBundle -Recurse -File -Filter '*.pfx').Count -gt 0) {
    throw 'PILOT SECURITY STOP: the workstation package source contains a PFX private key.'
}

$trustManifest = Get-Content -LiteralPath $trustManifestPath -Raw | ConvertFrom-Json
if ($trustManifest.SchemaVersion -ne 1 -or $trustManifest.PilotOnly -ne $true -or
    $trustManifest.HandoffType -ne 'WorkstationPilotTrustOnly') {
    throw 'The supplied directory is not the generated pilot workstation trust handoff.'
}
$rootSha256 = Get-Sha256 $rootCertificatePath
if ($rootSha256 -ne ([string]$trustManifest.Files.RootPublicCer.Sha256).ToUpperInvariant()) {
    throw 'The public pilot root failed its trust-manifest SHA-256 check.'
}

$normalizedComputer = $ExpectedComputerName.ToUpperInvariant()
$normalizedAccount = $ExpectedAccountName.Trim().Replace('/', '\')
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $safeComputer = $normalizedComputer -replace '[^A-Za-z0-9-]', '-'
    $OutputPath = Join-Path $PSScriptRoot "artifacts\SonAero-Hub-Pilot-$safeComputer.zip"
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput

$payloadRoot = Join-Path $PSScriptRoot 'pilot-workstation-installer'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sources = [ordered]@{
    'Install Son-Aero Hub Pilot.cmd' = Join-Path $payloadRoot 'Install Son-Aero Hub Pilot.cmd'
    'Install-SonAeroHubPilot.ps1' = Join-Path $payloadRoot 'Install-SonAeroHubPilot.ps1'
    'Set-HubPilotWorkstationTrust.ps1' = Join-Path $PSScriptRoot 'Set-HubPilotWorkstationTrust.ps1'
    'Install-EmployeeHubShortcut.ps1' = Join-Path $PSScriptRoot 'Install-EmployeeHubShortcut.ps1'
    'arda-transparent.ico' = Join-Path $repositoryRoot 'shared\branding\arda-transparent.ico'
    'README.txt' = Join-Path $payloadRoot 'README.txt'
    'SonAero-Hub-Pilot-Trust-Manifest.json' = $trustManifestPath
    'SonAero-Hub-Pilot-Root-PUBLIC.cer' = $rootCertificatePath
}
foreach ($source in $sources.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $source.Value -PathType Leaf)) {
        throw "Pilot installer source is missing: $($source.Value)"
    }
}

if (-not $PSCmdlet.ShouldProcess($resolvedOutput,
    "Build a PILOT-ONLY installer restricted to $normalizedComputer and $normalizedAccount")) {
    Write-Host 'WHATIF_READY: no pilot workstation ZIP was created or replaced.'
    return
}
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("sonaero-pilot-installer-{0}" -f [Guid]::NewGuid().ToString('N'))
$temporaryZip = Join-Path ([IO.Path]::GetTempPath()) ("sonaero-pilot-installer-{0}.zip" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    foreach ($source in $sources.GetEnumerator()) {
        Copy-Item -LiteralPath $source.Value -Destination (Join-Path $stage $source.Key)
    }
    $fileRecords = @($sources.Keys | ForEach-Object {
        $path = Join-Path $stage $_
        [ordered]@{ RelativePath = $_; Sha256 = Get-Sha256 $path }
    })
    $configuration = [ordered]@{
        SchemaVersion = 1
        PilotOnly = $true
        ExpectedComputerName = $normalizedComputer
        ExpectedAccountName = $normalizedAccount
        HubUri = ([Uri]$HubUri).AbsoluteUri
        RootThumbprintSha1 = [string]$trustManifest.Root.ThumbprintSha1
        RootCertificateSha256 = $rootSha256
        Files = $fileRecords
        Warning = 'PILOT ONLY. This ZIP is locked to one named employee and computer and must not be redistributed.'
    }
    $configuration | ConvertTo-Json -Depth 8 | Set-Content `
        -LiteralPath (Join-Path $stage 'pilot-installer-config.json') -Encoding UTF8
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $temporaryZip -CompressionLevel Optimal
    Move-Item -LiteralPath $temporaryZip -Destination $resolvedOutput -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryZip -PathType Leaf) { Remove-Item -LiteralPath $temporaryZip -Force }
    if (Test-Path -LiteralPath $stage -PathType Container) { Remove-Item -LiteralPath $stage -Recurse -Force }
}

$archive = Get-Item -LiteralPath $resolvedOutput
[pscustomobject]@{
    Status = 'PILOT_WORKSTATION_PACKAGE_READY'
    PilotOnly = $true
    ComputerName = $normalizedComputer
    AccountName = $normalizedAccount
    HubUri = ([Uri]$HubUri).AbsoluteUri
    Path = $archive.FullName
    SizeMB = [math]::Round($archive.Length / 1MB, 2)
    SHA256 = Get-Sha256 $archive.FullName
    RootCertificateSha256 = $rootSha256
} | Format-List
