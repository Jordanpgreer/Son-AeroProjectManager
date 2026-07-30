<#
    Sync-Branding.ps1

    Copies the canonical SON-AERO web brand assets from shared/branding/web into each
    application's ClientApp/public/brand folder, and the canonical design tokens from
    shared/branding/brand-tokens.css into each ClientApp/src (imported by that app's
    index.css). Uses plain file copies (no symlinks) so it works on any Windows checkout,
    including paths that contain spaces.
#>
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'shared\branding\web'
$tokens = Join-Path $repoRoot 'shared\branding\brand-tokens.css'

if (-not (Test-Path -LiteralPath $source)) {
    throw "Branding source not found: $source"
}

# ClientApp roots for each frontend that consumes brand assets.
$clientApps = @(
    (Join-Path $repoRoot 'apps\project-tracker\src\ProjectTracker.Api\ClientApp'),
    (Join-Path $repoRoot 'apps\portal\src\Portal.Api\ClientApp'),
    (Join-Path $repoRoot 'apps\estimating-dashboard\src\EstimatingDashboard.Api\ClientApp')
)

foreach ($clientApp in $clientApps) {
    if (-not (Test-Path -LiteralPath $clientApp)) {
        Write-Host "Skipping (not found): $clientApp"
        continue
    }

    $brandDir = Join-Path $clientApp 'public\brand'
    New-Item -ItemType Directory -Force -Path $brandDir | Out-Null

    Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -ne 'favicon.svg' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $brandDir -Force
    }

    # favicon lives at the public root, not under /brand.
    $favicon = Join-Path $source 'favicon.svg'
    if (Test-Path -LiteralPath $favicon) {
        Copy-Item -LiteralPath $favicon -Destination (Join-Path $clientApp 'public') -Force
    }

    # Design tokens: imported by src/index.css, so they land next to it in src.
    if (Test-Path -LiteralPath $tokens) {
        Copy-Item -LiteralPath $tokens -Destination (Join-Path $clientApp 'src') -Force
    }

    Write-Host "Synced branding -> $brandDir"
}

Write-Host 'Branding sync complete.'
