$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$templateRoot = Join-Path $repoRoot 'deployment\templates'
$hubOrigin = 'https://hub.son4l.local'

$publishSource = Get-Content -LiteralPath (Join-Path $repoRoot 'deployment\Publish-Hub.ps1') -Raw
if ($publishSource -match 'VITE_(?:HUB_URL|ENGINEERING_HUB_URL|ESTIMATING_DASHBOARD_URL|QUALITY_ASSURANCE_URL)' -or
    $publishSource -match '\$(?:HubUrl|EngineeringHubUrl|EstimatingDashboardUrl|QualityAssuranceUrl)\b') {
    throw 'Publish-Hub.ps1 still bakes a topology-specific Hub or module origin into the shared release.'
}

$webPushSource = Get-Content -LiteralPath (Join-Path $repoRoot 'deployment\Configure-ProjectTrackerWebPush.ps1') -Raw
if (-not $webPushSource.Contains("[string]`$VerificationUri = 'https://projects.hub.son4l.local/api/push/public-key'")) {
    throw 'Project Tracker Web Push does not default verification to the permanent HTTPS origin.'
}

$expectedApplications = [ordered]@{
    'project-tracker' = 'https://projects.hub.son4l.local'
    'engineering-hub' = 'https://engineering.hub.son4l.local'
    'estimating-dashboard' = 'https://estimating.hub.son4l.local'
    'quality-assurance' = 'https://quality.hub.son4l.local'
}

$portal = Get-Content -LiteralPath (Join-Path $templateRoot 'portal.appsettings.Production.json') -Raw |
    ConvertFrom-Json

foreach ($entry in $expectedApplications.GetEnumerator()) {
    $applications = @($portal.Portal.Applications | Where-Object Id -eq $entry.Key)
    if ($applications.Count -ne 1) {
        throw "Portal production template must contain exactly one '$($entry.Key)' application."
    }
    if ($applications[0].Url -cne $entry.Value) {
        throw "Portal application '$($entry.Key)' must use '$($entry.Value)'; found '$($applications[0].Url)'."
    }
}

$productionDisabledRole = '__production-disabled__'
$expectedProductionHiddenIds = @()
$actualProductionHiddenIds = @($portal.Portal.Applications |
    Where-Object {
        $roles = @($_.AllowedRoles)
        $roles.Count -eq 1 -and $roles[0] -ceq $productionDisabledRole
    } |
    ForEach-Object Id |
    Sort-Object)
if (($actualProductionHiddenIds -join '|') -cne (($expectedProductionHiddenIds | Sort-Object) -join '|')) {
    throw "Portal production template must not hide any reviewed module with '$productionDisabledRole'; found: $($actualProductionHiddenIds -join ', ')."
}

foreach ($applicationId in @('project-tracker', 'engineering-hub', 'estimating-dashboard', 'quality-assurance', 'admin-console')) {
    $application = @($portal.Portal.Applications | Where-Object Id -eq $applicationId)
    if ($application.Count -ne 1) {
        throw "Portal production template must contain exactly one '$applicationId' application."
    }
    if (@($application[0].AllowedRoles) -contains $productionDisabledRole) {
        throw "Portal production template must not hide core application '$applicationId'."
    }
}

$localPortal = Get-Content -LiteralPath (Join-Path $repoRoot 'apps\portal\src\Portal.Api\appsettings.json') -Raw |
    ConvertFrom-Json
foreach ($applicationId in @('engineering-hub', 'quality-assurance')) {
    $application = @($localPortal.Portal.Applications | Where-Object Id -eq $applicationId)
    if ($application.Count -ne 1 -or @($application[0].AllowedRoles).Count -ne 0) {
        throw "Local Portal application '$applicationId' must remain visible with an empty AllowedRoles list."
    }
}

foreach ($catalog in @($portal, $localPortal)) {
    $adminConsole = @($catalog.Portal.Applications | Where-Object Id -eq 'admin-console')
    if ($adminConsole.Count -ne 1 -or (@($adminConsole[0].AllowedRoles) -join '|') -cne 'Admin') {
        throw 'Portal Admin Console must be visible only to the Admin role.'
    }
}

$tracker = Get-Content -LiteralPath (Join-Path $templateRoot 'project-tracker.appsettings.Production.json') -Raw |
    ConvertFrom-Json
$trackerOrigins = @($tracker.Cors.HubOrigins)
if ($trackerOrigins.Count -lt 1 -or $trackerOrigins[0] -cne $hubOrigin) {
    throw "Project Tracker must prefer the permanent Hub origin '$hubOrigin'."
}
if ($trackerOrigins -notcontains 'http://SON-IIS2:5140') {
    throw 'Project Tracker must retain the existing HTTP Hub origin during the HTTPS transition.'
}
if ($trackerOrigins -notcontains 'https://SON-IIS2:6140') {
    throw 'Project Tracker must retain the existing HTTPS pilot Hub origin during stabilization.'
}
if (@($trackerOrigins | Sort-Object -Unique).Count -ne $trackerOrigins.Count) {
    throw 'Project Tracker Hub origins must not contain duplicates.'
}

foreach ($templateName in @(
    'engineering-hub.appsettings.Production.json',
    'estimating-dashboard.appsettings.Production.json',
    'quality-assurance.appsettings.Production.json'
)) {
    $config = Get-Content -LiteralPath (Join-Path $templateRoot $templateName) -Raw | ConvertFrom-Json
    if ($config.Portal.Url -cne $hubOrigin) {
        throw "$templateName must configure Portal.Url as '$hubOrigin'."
    }
}

$frontendFiles = @(
    'apps\engineering-hub\src\EngineeringHub.Api\ClientApp\src\App.tsx',
    'apps\estimating-dashboard\src\EstimatingDashboard.Api\ClientApp\src\App.tsx',
    'apps\quality-assurance\src\QualityAssurance.Api\ClientApp\src\App.tsx',
    'apps\project-tracker\src\ProjectTracker.Api\ClientApp\src\lib.tsx'
)
foreach ($relativePath in $frontendFiles) {
    $source = Get-Content -LiteralPath (Join-Path $repoRoot $relativePath) -Raw
    if ($source -notmatch [regex]::Escape("'https://hub.son4l.local'")) {
        throw "$relativePath does not contain the permanent production Hub fallback."
    }
    foreach ($permanentHost in @(
        'hub.son4l.local',
        'projects.hub.son4l.local',
        'engineering.hub.son4l.local',
        'estimating.hub.son4l.local',
        'quality.hub.son4l.local'
    )) {
        if ($source -notmatch [regex]::Escape("'$permanentHost'")) {
            throw "$relativePath does not explicitly allow permanent hostname '$permanentHost'."
        }
    }
    if ($source -notmatch 'permanentHosts\.has\(hostname\)' -or
        $source -match "endsWith\('\.hub\.son4l\.local'\)") {
        throw "$relativePath does not use the exact permanent-host allowlist."
    }
    if ($source -notmatch '\b5140\b') {
        throw "$relativePath no longer retains the local-development Hub fallback."
    }
    if ($source -notmatch '\b6140\b') {
        throw "$relativePath no longer retains the HTTPS pilot Hub fallback."
    }
}

Write-Output 'PRODUCTION_HTTPS_APPLICATION_CONFIG_TESTS_PASSED'
