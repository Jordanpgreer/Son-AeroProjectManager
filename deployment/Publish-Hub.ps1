<# Produces Release artifacts for all SON-AERO Hub applications. #>
[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'artifacts\hub'),
    [string]$ProjectTrackerUrl = '/project-tracker-api',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedRepoRoot = [System.IO.Path]::GetFullPath($repoRoot)
$resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

function ConvertTo-ApprovedProjectTrackerUrl {
    param([Parameter(Mandatory = $true)][string]$Value)
    $candidate = $Value.Trim()
    if ($candidate.TrimEnd('/') -ceq '/project-tracker-api') { return '/project-tracker-api' }

    $uri = $null
    if (-not [Uri]::TryCreate($candidate, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -notin @('http', 'https') -or
        [string]::IsNullOrWhiteSpace($uri.Host) -or
        -not [string]::IsNullOrWhiteSpace($uri.UserInfo) -or
        -not [string]::IsNullOrWhiteSpace($uri.Query) -or
        -not [string]::IsNullOrWhiteSpace($uri.Fragment) -or
        $uri.AbsolutePath -ne '/') {
        throw 'ProjectTrackerUrl must be /project-tracker-api or an approved Project Tracker server origin.'
    }
    $normalized = $uri.GetLeftPart([UriPartial]::Authority)
    if (@(
        'https://projects.hub.son4l.local',
        'http://SON-IIS2:5135',
        'https://SON-IIS2:6135'
    ) -inotcontains $normalized) {
        throw 'ProjectTrackerUrl is not an approved production or retained pilot Project Tracker origin.'
    }
    return $normalized
}

$trackerUrlValue = ConvertTo-ApprovedProjectTrackerUrl $ProjectTrackerUrl

function Find-DotNetSdk {
    $candidates = @(
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
        (Join-Path $env:LOCALAPPDATA 'CodexDotnetSdk8\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe'
    )
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($command) { $candidates = @($command.Source) + $candidates }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        if ((& $candidate --list-sdks 2>$null) -match '^8\.') { return $candidate }
    }

    throw 'The .NET 8 SDK is required to publish the Hub.'
}
$dotnet = Find-DotNetSdk
$nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
$npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
if (-not $nodeCommand -or -not $npmCommand) {
    throw 'Node.js and npm are required to build the Hub frontends.'
}
$nodeVersionText = (& $nodeCommand.Source --version).Trim().TrimStart('v')
$nodeVersion = $null
if (-not [Version]::TryParse($nodeVersionText, [ref]$nodeVersion)) {
    throw "Could not parse the installed Node.js version: $nodeVersionText"
}
$supportedNode = ($nodeVersion.Major -eq 20 -and $nodeVersion -ge [Version]'20.19.0') `
    -or $nodeVersion -ge [Version]'22.12.0'
if (-not $supportedNode) {
    throw "Node.js 20.19+ or 22.12+ is required; installed version is $nodeVersion."
}

$applications = @(
    [pscustomobject]@{ Name = 'ProjectTracker'; Project = 'apps\project-tracker\src\ProjectTracker.Api\ProjectTracker.Api.csproj' },
    [pscustomobject]@{ Name = 'EngineeringHub'; Project = 'apps\engineering-hub\src\EngineeringHub.Api\EngineeringHub.Api.csproj' },
    [pscustomobject]@{ Name = 'EstimatingDashboard'; Project = 'apps\estimating-dashboard\src\EstimatingDashboard.Api\EstimatingDashboard.Api.csproj' },
    [pscustomobject]@{ Name = 'QualityAssurance'; Project = 'apps\quality-assurance\src\QualityAssurance.Api\QualityAssurance.Api.csproj' },
    [pscustomobject]@{ Name = 'Portal'; Project = 'apps\portal\src\Portal.Api\Portal.Api.csproj' }
)

if (Test-Path -LiteralPath $resolvedOutputRoot) {
    if (-not (Test-Path -LiteralPath $resolvedOutputRoot -PathType Container)) {
        throw "OutputRoot is not a directory: $resolvedOutputRoot"
    }
    if (@(Get-ChildItem -LiteralPath $resolvedOutputRoot -Force).Count -gt 0) {
        throw "OutputRoot must be new or empty so stale release files cannot be reused: $resolvedOutputRoot"
    }
}
else {
    New-Item -ItemType Directory -Path $resolvedOutputRoot | Out-Null
}
$priorTrackerUrl = $env:VITE_PROJECT_TRACKER_URL
try {
    $env:VITE_PROJECT_TRACKER_URL = $trackerUrlValue.TrimEnd('/')
    foreach ($application in $applications) {
        $projectPath = Join-Path $resolvedRepoRoot $application.Project
        if (-not (Test-Path -LiteralPath $projectPath)) {
            throw "Project file not found: $projectPath"
        }

        $outputPath = Join-Path $resolvedOutputRoot $application.Name
        New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
        Write-Host "Publishing $($application.Name) to $outputPath"
        & $dotnet restore $projectPath --ignore-failed-sources
        if ($LASTEXITCODE -ne 0) {
            throw "Restoring $($application.Name) failed with exit code $LASTEXITCODE."
        }
        & $dotnet publish $projectPath --configuration $Configuration --output $outputPath --no-restore --property:UseAppHost=false
        if ($LASTEXITCODE -ne 0) {
            throw "Publishing $($application.Name) failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    $env:VITE_PROJECT_TRACKER_URL = $priorTrackerUrl
}

Write-Host ''
Write-Host "Hub artifacts are ready at $resolvedOutputRoot"
Write-Host 'Copy the matching appsettings.Production.json files into each application folder before starting IIS.'
