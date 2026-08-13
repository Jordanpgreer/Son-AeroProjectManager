<#
    Builds the employee-facing Son-Aero Hub installer ZIP from tracked sources.
    The package defaults to https://hub.son4l.local. Supply -HubUri explicitly only for a retained
    HTTP or HTTPS pilot endpoint.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
param(
    [string]$OutputPath,

    # Override this only when deliberately building a package for the retained SON-IIS2 pilot.
    [string]$HubUri = 'https://hub.son4l.local'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot 'artifacts\SonAero-Hub-Employee-Installer.zip'
}

$parsedHubUri = $null
if (-not [Uri]::TryCreate($HubUri, [UriKind]::Absolute, [ref]$parsedHubUri) -or
    $parsedHubUri.Scheme -notin @('http', 'https') -or
    [string]::IsNullOrWhiteSpace($parsedHubUri.Host) -or
    -not [string]::IsNullOrWhiteSpace($parsedHubUri.UserInfo) -or
    $parsedHubUri.AbsolutePath -ne '/' -or
    -not [string]::IsNullOrWhiteSpace($parsedHubUri.Query) -or
    -not [string]::IsNullOrWhiteSpace($parsedHubUri.Fragment)) {
    throw 'HubUri must be an absolute HTTP or HTTPS server origin without credentials, a path, a query, or a fragment.'
}
$normalizedHubUri = $parsedHubUri.AbsoluteUri
$approvedHubUris = @(
    'https://hub.son4l.local/',
    'http://son-iis2:5140/',
    'https://son-iis2:6140/'
)
if ($normalizedHubUri.ToLowerInvariant() -notin $approvedHubUris) {
    throw 'HubUri must be the permanent Portal origin or one of the retained SON-IIS2 Portal pilot origins.'
}

$payloadRoot = Join-Path $PSScriptRoot 'employee-installer'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sources = [ordered]@{
    'Install Son-Aero Hub.cmd' = Join-Path $payloadRoot 'Install Son-Aero Hub.cmd'
    'Install-SonAeroHub.ps1' = Join-Path $payloadRoot 'Install-SonAeroHub.ps1'
    'Install-EmployeeHubShortcut.ps1' = Join-Path $PSScriptRoot 'Install-EmployeeHubShortcut.ps1'
    'son-aero.ico' = Join-Path $repositoryRoot 'shared\branding\son-aero.ico'
    'README.txt' = Join-Path $payloadRoot 'README.txt'
}

foreach ($source in $sources.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $source.Value -PathType Leaf)) {
        throw "Installer source is missing: $($source.Value)"
    }
}

$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutputPath
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

if (-not $PSCmdlet.ShouldProcess($resolvedOutputPath, 'Build Son-Aero Hub employee installer ZIP')) {
    Write-Host 'WHATIF_READY: no installer ZIP was created or replaced.'
    return
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("sonaero-employee-installer-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    foreach ($source in $sources.GetEnumerator()) {
        Copy-Item -LiteralPath $source.Value -Destination (Join-Path $stage $source.Key)
    }

    $installerConfiguration = [ordered]@{
        SchemaVersion = 1
        HubUri = $normalizedHubUri
    }
    $configurationJson = $installerConfiguration | ConvertTo-Json
    [IO.File]::WriteAllText(
        (Join-Path $stage 'SonAeroHubInstaller.json'),
        ($configurationJson + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding($false))
    )

    $temporaryZip = Join-Path ([IO.Path]::GetTempPath()) ("sonaero-employee-installer-{0}.zip" -f [Guid]::NewGuid().ToString('N'))
    try {
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $temporaryZip -CompressionLevel Optimal
        Move-Item -LiteralPath $temporaryZip -Destination $resolvedOutputPath -Force
    } finally {
        if (Test-Path -LiteralPath $temporaryZip -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryZip -Force
        }
    }
} finally {
    if (Test-Path -LiteralPath $stage -PathType Container) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

$archive = Get-Item -LiteralPath $resolvedOutputPath
[pscustomobject]@{
    Status = 'PACKAGE_READY'
    Path = $archive.FullName
    SizeMB = [math]::Round($archive.Length / 1MB, 2)
    SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive.FullName).Hash
    HubUri = $normalizedHubUri
    Files = $sources.Count + 1
} | Format-List
