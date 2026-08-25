<#
    Sync-Branding.ps1

    Copies the canonical Arda application-shell assets from shared/branding/web into each
    application's ClientApp/public/brand folder, along with the canonical design tokens
    and Arda shell treatment in each ClientApp/src. Uses plain file copies (no symlinks)
    so it works on any Windows checkout, including paths that contain spaces.
#>
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'shared\branding\web'
$tokens = Join-Path $repoRoot 'shared\branding\brand-tokens.css'
$shellStyles = Join-Path $repoRoot 'shared\branding\arda-shell.css'
$assetNames = @(
    'arda-lockup.png',
    'arda-mark.png',
    'arda-lockup-reversed.png',
    'arda-mark-reversed.png',
    'arda-favicon.png'
)

if (-not (Test-Path -LiteralPath $source)) {
    throw "Branding source not found: $source"
}

# ClientApp roots for each frontend that consumes brand assets.
$clientApps = @(
    (Join-Path $repoRoot 'apps\project-tracker\src\ProjectTracker.Api\ClientApp'),
    (Join-Path $repoRoot 'apps\portal\src\Portal.Api\ClientApp'),
    (Join-Path $repoRoot 'apps\estimating-dashboard\src\EstimatingDashboard.Api\ClientApp'),
    (Join-Path $repoRoot 'apps\engineering-hub\src\EngineeringHub.Api\ClientApp'),
    (Join-Path $repoRoot 'apps\quality-assurance\src\QualityAssurance.Api\ClientApp')
)

foreach ($clientApp in $clientApps) {
    if (-not (Test-Path -LiteralPath $clientApp)) {
        Write-Host "Skipping (not found): $clientApp"
        continue
    }

    $brandDir = Join-Path $clientApp 'public\brand'
    New-Item -ItemType Directory -Force -Path $brandDir | Out-Null

    foreach ($assetName in $assetNames) {
        $asset = Join-Path $source $assetName
        if (-not (Test-Path -LiteralPath $asset)) {
            throw "Canonical Arda asset not found: $asset"
        }
        Copy-Item -LiteralPath $asset -Destination $brandDir -Force
    }

    # Design tokens: imported by src/index.css, so they land next to it in src.
    if (Test-Path -LiteralPath $tokens) {
        Copy-Item -LiteralPath $tokens -Destination (Join-Path $clientApp 'src') -Force
    }

    if (Test-Path -LiteralPath $shellStyles) {
        Copy-Item -LiteralPath $shellStyles -Destination (Join-Path $clientApp 'src') -Force
    }

    Write-Host "Synced branding -> $brandDir"
}

Write-Host 'Branding sync complete.'
