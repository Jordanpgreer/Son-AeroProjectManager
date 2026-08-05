<# Builds the employee-facing Son-Aero Hub installer ZIP from tracked sources. #>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Low')]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot 'artifacts\SonAero-Hub-Employee-Installer.zip'
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
    Files = $sources.Count
} | Format-List
