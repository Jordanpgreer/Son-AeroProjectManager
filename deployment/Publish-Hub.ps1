<# Produces Release artifacts for all SON-AERO Hub applications. #>
[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'artifacts\hub'),
    [string]$ProjectTrackerUrl = 'http://localhost:5135',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedRepoRoot = [System.IO.Path]::GetFullPath($repoRoot)
$resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$trackerUri = $null
if (-not [Uri]::TryCreate($ProjectTrackerUrl, [UriKind]::Absolute, [ref]$trackerUri) -or
    $trackerUri.Scheme -notin @('http', 'https')) {
    throw 'ProjectTrackerUrl must be an absolute http or https URL.'
}

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
if (-not (Get-Command npm.cmd -ErrorAction SilentlyContinue)) {
    throw 'Node.js LTS and npm are required to build the Hub frontends.'
}

$applications = @(
    [pscustomobject]@{ Name = 'ProjectTracker'; Project = 'apps\project-tracker\src\ProjectTracker.Api\ProjectTracker.Api.csproj' },
    [pscustomobject]@{ Name = 'EngineeringHub'; Project = 'apps\engineering-hub\src\EngineeringHub.Api\EngineeringHub.Api.csproj' },
    [pscustomobject]@{ Name = 'EstimatingDashboard'; Project = 'apps\estimating-dashboard\src\EstimatingDashboard.Api\EstimatingDashboard.Api.csproj' },
    [pscustomobject]@{ Name = 'Portal'; Project = 'apps\portal\src\Portal.Api\Portal.Api.csproj' }
)

New-Item -ItemType Directory -Force -Path $resolvedOutputRoot | Out-Null
$priorTrackerUrl = $env:VITE_PROJECT_TRACKER_URL
try {
    $env:VITE_PROJECT_TRACKER_URL = $ProjectTrackerUrl.TrimEnd('/')
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
