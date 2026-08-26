<#
    Deploys one immutable SON-AERO Hub release on SON-IIS2.

    The package must contain five published application folders. Development settings are
    deliberately not copied. Each application's current appsettings.Production.json is
    carried forward before IIS is stopped or changed.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$ReleaseId,

    [string]$ReleaseRoot = 'C:\SonAero\releases',
    [string]$ExpectedComputerName = 'SON-IIS2',

    [switch]$RetainVerifiedQuality,

    [ValidateRange(30, 600)]
    [int]$HealthTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

$portalCatalogModule = Join-Path $PSScriptRoot 'PortalApplicationCatalog.psm1'
if (-not (Test-Path -LiteralPath $portalCatalogModule -PathType Leaf)) {
    throw "Portal catalog deployment module was not found: $portalCatalogModule"
}
Import-Module $portalCatalogModule -Force -ErrorAction Stop

$qualityProductionConfigurationModule = Join-Path $PSScriptRoot 'QualityAssuranceProductionConfiguration.psm1'
if (-not (Test-Path -LiteralPath $qualityProductionConfigurationModule -PathType Leaf)) {
    throw "Quality Production configuration module was not found: $qualityProductionConfigurationModule"
}
Import-Module $qualityProductionConfigurationModule -Force -ErrorAction Stop

if ($RetainVerifiedQuality) {
    $retainedQualityModule = Join-Path $PSScriptRoot 'HubReleaseRetainedQuality.psm1'
    if (-not (Test-Path -LiteralPath $retainedQualityModule -PathType Leaf)) {
        throw "Retained Quality deployment module was not found: $retainedQualityModule"
    }
    Import-Module $retainedQualityModule -Force -ErrorAction Stop
}

$applications = @(
    [pscustomobject]@{
        Name = 'ProjectTracker'
        Folder = 'ProjectTracker'
        Port = 5135
        MainDll = 'ProjectTracker.Api.dll'
    },
    [pscustomobject]@{
        Name = 'SonAeroPortal'
        Folder = 'Portal'
        Port = 5140
        MainDll = 'Portal.Api.dll'
    },
    [pscustomobject]@{
        Name = 'EngineeringHub'
        Folder = 'EngineeringHub'
        Port = 5150
        MainDll = 'EngineeringHub.Api.dll'
    },
    [pscustomobject]@{
        Name = 'EstimatingDashboard'
        Folder = 'EstimatingDashboard'
        Port = 5160
        MainDll = 'EstimatingDashboard.Api.dll'
    },
    [pscustomobject]@{
        Name = 'QualityAssurance'
        Folder = 'QualityAssurance'
        Port = 5170
        MainDll = 'QualityAssurance.Api.dll'
    }
)
$qualityApplication = @($applications | Where-Object Name -EQ 'QualityAssurance')[0]
$deploymentApplications = if ($RetainVerifiedQuality) {
    @($applications | Where-Object Name -NE $qualityApplication.Name)
}
else {
    @($applications)
}

$projectTrackerGateway = [pscustomobject]@{
    Site = 'SonAeroPortal'
    Path = '/project-tracker-api'
    Pool = 'ProjectTrackerAdminGateway'
    Port = 5140
}

function Assert-DeploymentIdentity {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity -or $identity.IsSystem -or $identity.Name -ieq 'NT AUTHORITY\SYSTEM') {
        throw 'Run this script interactively as an authorized domain user, not Local System.'
    }
    if ($identity.Name -notlike 'SON4L\*') {
        throw "Run this script as an authorized SON4L domain user, not '$($identity.Name)'."
    }
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated Windows PowerShell session.'
    }
}

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path)).TrimEnd('\')
}

function Test-PathContainmentOverlap {
    param(
        [Parameter(Mandatory = $true)][string]$FirstPath,
        [Parameter(Mandatory = $true)][string]$SecondPath
    )

    $first = Get-FullPath -Path $FirstPath
    $second = Get-FullPath -Path $SecondPath
    return $first -ieq $second -or
        $first.StartsWith($second + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $second.StartsWith($first + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ValidWebConfig {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$MainDll
    )

    try {
        [xml]$configuration = Get-Content -LiteralPath $Path -Raw
    }
    catch {
        throw "Invalid web.config XML at '$Path': $($_.Exception.Message)"
    }

    $aspNetCoreNodes = @($configuration.SelectNodes('//aspNetCore'))
    if ($aspNetCoreNodes.Count -ne 1) {
        throw "'$Path' must contain exactly one aspNetCore element."
    }
    $arguments = ([string]$aspNetCoreNodes[0].arguments).Trim()
    if ([string]$aspNetCoreNodes[0].processPath -ine 'dotnet' -or
        [string]$aspNetCoreNodes[0].hostingModel -ine 'inprocess' -or
        $arguments -cne ".\$MainDll") {
        throw "'$Path' must launch only '$MainDll' with the approved in-process dotnet command and no application arguments."
    }
}

function Assert-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    try {
        $content = Get-Content -LiteralPath $Path -Raw
        if ([string]::IsNullOrWhiteSpace($content)) { throw 'The file is empty.' }
        $null = $content | ConvertFrom-Json
    }
    catch {
        throw "Invalid JSON in '$Path': $($_.Exception.Message)"
    }
}

function Get-HealthResult {
    param([Parameter(Mandatory = $true)][object]$Application)
    $uri = "http://localhost:$($Application.Port)/api/health"
    try {
        $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec 10
        return [pscustomobject]@{
            Healthy = ($response.StatusCode -eq 200)
            Detail = "HTTP $([int]$response.StatusCode) from $uri"
        }
    }
    catch {
        $status = $null
        if ($null -ne $_.Exception.Response) {
            try { $status = [int]$_.Exception.Response.StatusCode } catch {}
        }
        $detail = if ($null -ne $status) {
            "HTTP $status from $uri"
        }
        else {
            "$uri failed: $($_.Exception.Message)"
        }
        return [pscustomobject]@{
            Healthy = $false
            Detail = $detail
        }
    }
}

function Wait-ApplicationHealth {
    param(
        [Parameter(Mandatory = $true)][object[]]$Targets,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $pending = @($Targets)
    $lastResults = @{}
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        foreach ($application in @($pending)) {
            $result = Get-HealthResult -Application $application
            if ($result.Healthy) {
                $pending = @($pending | Where-Object Name -NE $application.Name)
            }
            else {
                $lastResults[$application.Name] = [string]$result.Detail
            }
        }
        if ($pending.Count -gt 0) { Start-Sleep -Milliseconds 750 }
    } while ($pending.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)

    if ($pending.Count -gt 0) {
        $details = @(
            foreach ($application in $pending) {
                $detail = [string]$lastResults[$application.Name]
                if ([string]::IsNullOrWhiteSpace($detail)) { $detail = 'No response detail was captured.' }
                '{0}: {1}' -f $application.Name, $detail
            }
        )
        throw "Health verification timed out for: $($pending.Name -join ', '). Last results: $($details -join ' | ')"
    }
}

function Get-ProjectTrackerGatewayHealthResult {
    $uri = "http://localhost:$($projectTrackerGateway.Port)$($projectTrackerGateway.Path)/api/health"
    try {
        $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec 10
        return [pscustomobject]@{
            Healthy = ($response.StatusCode -eq 200)
            Detail = "HTTP $([int]$response.StatusCode) from $uri"
        }
    }
    catch {
        $status = $null
        if ($null -ne $_.Exception.Response) {
            try { $status = [int]$_.Exception.Response.StatusCode } catch {}
        }
        $detail = if ($null -ne $status) {
            "HTTP $status from $uri"
        }
        else {
            "$uri failed: $($_.Exception.Message)"
        }
        return [pscustomobject]@{
            Healthy = $false
            Detail = $detail
        }
    }
}

function Wait-ProjectTrackerGatewayHealth {
    param([Parameter(Mandatory = $true)][int]$TimeoutSeconds)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastResult = $null
    do {
        $lastResult = Get-ProjectTrackerGatewayHealthResult
        if ($lastResult.Healthy) { return }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)

    $detail = if ($null -eq $lastResult) { 'No response detail was captured.' } else { [string]$lastResult.Detail }
    throw "Project Tracker gateway health verification timed out at '$($projectTrackerGateway.Path)'. Last result: $detail"
}

function Wait-IisState {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Site', 'Pool')][string]$Kind,
        [Parameter(Mandatory = $true)][string[]]$Names,
        [Parameter(Mandatory = $true)][ValidateSet('Started', 'Stopped')][string]$State,
        [int]$TimeoutSeconds = 120
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $notReady = @($Names | Where-Object {
            $value = if ($Kind -eq 'Site') {
                (Get-WebsiteState -Name $_).Value
            }
            else {
                (Get-WebAppPoolState -Name $_).Value
            }
            $value -ne $State
        })
        if ($notReady.Count -eq 0) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "$Kind state did not become '$State' for: $($notReady -join ', ')."
}

function Request-IisState {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Site', 'Pool')][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][ValidateSet('Started', 'Stopped')][string]$State,
        [int]$TimeoutSeconds = 120
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $current = if ($Kind -eq 'Site') {
            (Get-WebsiteState -Name $Name).Value
        }
        else {
            (Get-WebAppPoolState -Name $Name).Value
        }
        if ($current -eq $State) { return }

        # IIS rejects control messages during Starting/Stopping. Wait out that transition,
        # then issue the requested command only from the opposite stable state.
        if ($State -eq 'Stopped' -and $current -eq 'Started') {
            try {
                if ($Kind -eq 'Site') { Stop-Website -Name $Name }
                else { Stop-WebAppPool -Name $Name }
            }
            catch { Start-Sleep -Milliseconds 500 }
        }
        elseif ($State -eq 'Started' -and $current -eq 'Stopped') {
            try {
                if ($Kind -eq 'Site') { Start-Website -Name $Name }
                else { Start-WebAppPool -Name $Name }
            }
            catch { Start-Sleep -Milliseconds 500 }
        }
        else {
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "$Kind '$Name' did not become '$State'; its current state is '$current'."
}

function Stop-HubApplications {
    foreach ($application in $deploymentApplications) {
        Request-IisState -Kind Site -Name $application.Name -State Stopped
    }
    Wait-IisState -Kind Site -Names @($deploymentApplications.Name) -State Stopped

    $poolNames = @($deploymentApplications.Name) + @($projectTrackerGateway.Pool)
    foreach ($poolName in $poolNames) {
        Request-IisState -Kind Pool -Name $poolName -State Stopped
    }
    Wait-IisState -Kind Pool -Names $poolNames -State Stopped
}

function Start-OneApplication {
    param([Parameter(Mandatory = $true)][object]$Application)

    Request-IisState -Kind Pool -Name $Application.Name -State Started
    Wait-IisState -Kind Pool -Names @($Application.Name) -State Started
    Request-IisState -Kind Site -Name $Application.Name -State Started
    Wait-IisState -Kind Site -Names @($Application.Name) -State Started
}

function Start-HubApplications {
    $tracker = @($deploymentApplications | Where-Object Name -EQ 'ProjectTracker')[0]
    Start-OneApplication -Application $tracker
    Wait-ApplicationHealth -Targets @($tracker) -TimeoutSeconds $HealthTimeoutSeconds

    # Start the Portal root while the separate preloaded gateway pool is still stopped. This keeps
    # the gateway's Project Tracker cold start from overlapping the Portal root startup.
    $portal = @($deploymentApplications | Where-Object Name -EQ $projectTrackerGateway.Site)[0]
    Start-OneApplication -Application $portal
    Wait-ApplicationHealth -Targets @($portal) -TimeoutSeconds $HealthTimeoutSeconds

    Request-IisState -Kind Pool -Name $projectTrackerGateway.Pool -State Started
    Wait-IisState -Kind Pool -Names @($projectTrackerGateway.Pool) -State Started
    Wait-ProjectTrackerGatewayHealth -TimeoutSeconds $HealthTimeoutSeconds

    $remaining = @($deploymentApplications | Where-Object {
        $_.Name -ne 'ProjectTracker' -and $_.Name -ne $projectTrackerGateway.Site
    })
    # Each application can perform SQL migrations or shared permission seeding before its health
    # endpoint begins listening. Start and verify each cold application completely before starting
    # the next one so first-run SQL work cannot overlap across modules.
    foreach ($application in $remaining) {
        Start-OneApplication -Application $application
        Wait-ApplicationHealth -Targets @($application) -TimeoutSeconds $HealthTimeoutSeconds
    }
}

function Set-IisPhysicalPaths {
    param([Parameter(Mandatory = $true)][hashtable]$PathsBySite)

    $serverManager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        foreach ($application in $deploymentApplications) {
            $site = $serverManager.Sites[$application.Name]
            if (-not $site) { throw "IIS site '$($application.Name)' disappeared during deployment." }
            $rootApplication = $site.Applications['/']
            $rootVirtualDirectory = $rootApplication.VirtualDirectories['/']
            $rootVirtualDirectory.PhysicalPath = $PathsBySite[$application.Name]
        }
        $portalSite = $serverManager.Sites[$projectTrackerGateway.Site]
        $gatewayApplication = $portalSite.Applications[$projectTrackerGateway.Path]
        if (-not $gatewayApplication) {
            throw "IIS application '$($projectTrackerGateway.Site)$($projectTrackerGateway.Path)' disappeared during deployment."
        }
        $gatewayApplication.ApplicationPoolName = $projectTrackerGateway.Pool
        $gatewayApplication.VirtualDirectories['/'].PhysicalPath = $PathsBySite['ProjectTracker']

        # CommitChanges performs one IIS configuration commit for all root paths and the gateway.
        $serverManager.CommitChanges()
    }
    finally {
        $serverManager.Dispose()
    }
}

function Copy-SanitizedApplication {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination | Out-Null
    $sourcePrefix = $Source.TrimEnd('\')
    foreach ($file in Get-ChildItem -LiteralPath $sourcePrefix -File -Recurse -Force) {
        if ($file.Name -ieq 'appsettings.Production.json') { continue }
        if ($file.Name -like 'appsettings.Development*.json') { continue }

        $relativePath = $file.FullName.Substring($sourcePrefix.Length).TrimStart('\')
        $destinationFile = Join-Path $Destination $relativePath
        $destinationDirectory = Split-Path -Parent $destinationFile
        if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destinationFile
    }
}

function Assert-RetainedQualityPreserved {
    param(
        [Parameter(Mandatory = $true)]$ExpectedSnapshot,
        [Parameter(Mandatory = $true)][string]$PackageQualityPath,
        [Parameter(Mandatory = $true)][string]$ActiveQualityPath,
        [Parameter(Mandatory = $true)][string]$Phase
    )

    [void](Read-QualityProductionConfiguration -Path (
        Join-Path $ActiveQualityPath 'appsettings.Production.json'))
    Assert-QualitySanitizedApplicationManifestEqual `
        -SourceRoot $PackageQualityPath `
        -CandidateRoot $ActiveQualityPath
    Assert-HubRetainedQualityBoundaryUnchanged `
        -ExpectedSnapshot $ExpectedSnapshot `
        -SiteName $qualityApplication.Name `
        -PoolName $qualityApplication.Name `
        -MainDll $qualityApplication.MainDll `
        -HealthUri "http://localhost:$($qualityApplication.Port)/api/health" `
        -Phase $Phase
}

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
}
Assert-DeploymentIdentity

if ($ReleaseId -in @('.', '..')) { throw 'ReleaseId cannot be a relative-path marker.' }
$packagePath = Get-FullPath -Path $PackageRoot
$releaseRootPath = Get-FullPath -Path $ReleaseRoot
if ($releaseRootPath -eq [IO.Path]::GetPathRoot($releaseRootPath)) {
    throw 'ReleaseRoot cannot be the root of a drive.'
}
if (-not (Test-Path -LiteralPath $packagePath -PathType Container)) {
    throw "PackageRoot does not exist: $packagePath"
}
$releasePath = Get-FullPath -Path (Join-Path $releaseRootPath $ReleaseId)
if (-not $releasePath.StartsWith($releaseRootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The resolved release destination escaped ReleaseRoot.'
}
if (Test-Path -LiteralPath $releasePath) {
    throw "Release destination already exists and will not be overwritten: $releasePath"
}
if (Test-PathContainmentOverlap -FirstPath $packagePath -SecondPath $releasePath) {
    throw 'PackageRoot and the release destination cannot contain one another.'
}

# The script's -WhatIf preference must not suppress loading the read-only IIS
# types needed for preflight inspection. Import-Module honors WhatIfPreference
# internally but doesn't expose a WhatIf parameter in Windows PowerShell 5.1, so
# disable the preference only while loading the module and restore it immediately.
$priorWhatIfPreference = $WhatIfPreference
try {
    $WhatIfPreference = $false
    Import-Module WebAdministration -ErrorAction Stop
}
finally {
    $WhatIfPreference = $priorWhatIfPreference
}

# Importing WebAdministration doesn't consistently load its management assembly
# in Windows PowerShell 5.1. Load the IIS-installed assembly explicitly so the
# read-only ServerManager preflight works in both normal and WhatIf execution.
if (-not ('Microsoft.Web.Administration.ServerManager' -as [type])) {
    $webAdministrationAssembly = Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path -LiteralPath $webAdministrationAssembly -PathType Leaf)) {
        throw "The IIS administration assembly was not found: $webAdministrationAssembly"
    }
    Add-Type -Path $webAdministrationAssembly -ErrorAction Stop
}
if (-not ('Microsoft.Web.Administration.ServerManager' -as [type])) {
    throw 'The IIS administration assembly loaded without exposing ServerManager.'
}
$serverManager = New-Object Microsoft.Web.Administration.ServerManager
$currentPaths = @{}
try {
    foreach ($application in $applications) {
        $sourcePath = Join-Path $packagePath $application.Folder
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
            throw "Package application folder is missing: $sourcePath"
        }
        if ($RetainVerifiedQuality) {
            # The retained recovery path is allowed only when every package tree has a stable,
            # sanitized manifest. This also rejects a reparse-point root, descendant reparse
            # points, and paths that collide under Windows case-insensitive comparison.
            [void](Get-QualitySanitizedApplicationManifest -Root $sourcePath)
        }
        $reparsePoints = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -Force | Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        })
        if ($reparsePoints.Count -gt 0) {
            throw "Package '$sourcePath' contains a reparse point and cannot be deployed."
        }
        $sourceWebConfig = Join-Path $sourcePath 'web.config'
        $sourceMainDll = Join-Path $sourcePath $application.MainDll
        if (-not (Test-Path -LiteralPath $sourceWebConfig -PathType Leaf)) {
            throw "Package web.config is missing: $sourceWebConfig"
        }
        if (-not (Test-Path -LiteralPath $sourceMainDll -PathType Leaf)) {
            throw "Package application DLL is missing: $sourceMainDll"
        }
        Assert-ValidWebConfig -Path $sourceWebConfig -MainDll $application.MainDll

        $site = $serverManager.Sites[$application.Name]
        if (-not $site) { throw "Required IIS site is missing: $($application.Name)" }
        $pool = $serverManager.ApplicationPools[$application.Name]
        if (-not $pool) { throw "Required IIS application pool is missing: $($application.Name)" }
        $rootApplication = $site.Applications['/']
        if (-not $rootApplication) { throw "IIS site '$($application.Name)' has no root application." }
        if ($rootApplication.ApplicationPoolName -ine $application.Name) {
            throw "IIS site '$($application.Name)' must use application pool '$($application.Name)'."
        }
        $rootVirtualDirectory = $rootApplication.VirtualDirectories['/']
        if (-not $rootVirtualDirectory) { throw "IIS site '$($application.Name)' has no root virtual directory." }

        $httpBindings = @($site.Bindings | Where-Object Protocol -EQ 'http')
        if ($httpBindings.Count -ne 1 -or
            $httpBindings[0].BindingInformation -ne "*:$($application.Port):") {
            throw "IIS site '$($application.Name)' must have exactly one HTTP binding '*:$($application.Port):'."
        }
        foreach ($otherSite in $serverManager.Sites) {
            if ($otherSite.Name -ieq $application.Name) { continue }
            $conflicts = @($otherSite.Bindings | Where-Object {
                $_.Protocol -eq 'http' -and $_.BindingInformation -match ":$($application.Port):"
            })
            if ($conflicts.Count -gt 0) {
                throw "HTTP port $($application.Port) is also bound by IIS site '$($otherSite.Name)'."
            }
        }

        $currentPath = Get-FullPath -Path $rootVirtualDirectory.PhysicalPath
        if (-not (Test-Path -LiteralPath $currentPath -PathType Container)) {
            throw "Current IIS physical path does not exist: $currentPath"
        }
        if (Test-PathContainmentOverlap -FirstPath $releasePath -SecondPath $currentPath) {
            throw "Release destination and active IIS path cannot contain one another: $currentPath"
        }
        if (Test-PathContainmentOverlap -FirstPath $packagePath -SecondPath $currentPath) {
            throw "PackageRoot and active IIS path cannot contain one another: $currentPath"
        }
        $productionSettings = Join-Path $currentPath 'appsettings.Production.json'
        if (-not (Test-Path -LiteralPath $productionSettings -PathType Leaf)) {
            throw "Current production settings are missing: $productionSettings"
        }
        Assert-JsonFile -Path $productionSettings
        if ($application.Name -eq $qualityApplication.Name) {
            # Full releases carry Production settings forward; they must never perpetuate a
            # partial Quality SQL configuration. Repair is owned by the scoped Quality deploy.
            [void](Read-QualityProductionConfiguration -Path $productionSettings)
        }
        $currentPaths[$application.Name] = $currentPath
    }

    $gatewayPool = $serverManager.ApplicationPools[$projectTrackerGateway.Pool]
    if (-not $gatewayPool) {
        throw "Required IIS application pool '$($projectTrackerGateway.Pool)' is missing. Run Configure-PortalProjectTrackerGateway.ps1 first."
    }
    if ($gatewayPool.ManagedRuntimeVersion -ne '' `
        -or -not $gatewayPool.AutoStart `
        -or $gatewayPool.StartMode -ne [Microsoft.Web.Administration.StartMode]::AlwaysRunning `
        -or $gatewayPool.ProcessModel.IdentityType -ne [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity `
        -or -not $gatewayPool.ProcessModel.LoadUserProfile `
        -or $gatewayPool.ProcessModel.IdleTimeout -ne [TimeSpan]::Zero) {
        throw "IIS application pool '$($projectTrackerGateway.Pool)' is not in the required restricted always-running configuration. Run Configure-PortalProjectTrackerGateway.ps1 first."
    }
    $portalSite = $serverManager.Sites[$projectTrackerGateway.Site]
    $gatewayApplication = $portalSite.Applications[$projectTrackerGateway.Path]
    if (-not $gatewayApplication) {
        throw "Required IIS application '$($projectTrackerGateway.Site)$($projectTrackerGateway.Path)' is missing. Run Configure-PortalProjectTrackerGateway.ps1 first."
    }
    if ($gatewayApplication.ApplicationPoolName -ine $projectTrackerGateway.Pool) {
        throw "IIS application '$($projectTrackerGateway.Path)' must use application pool '$($projectTrackerGateway.Pool)'."
    }
    $gatewayVirtualDirectory = $gatewayApplication.VirtualDirectories['/']
    if (-not $gatewayVirtualDirectory) {
        throw "IIS application '$($projectTrackerGateway.Path)' has no root virtual directory."
    }
    $gatewayCurrentPath = Get-FullPath -Path $gatewayVirtualDirectory.PhysicalPath
    if ($gatewayCurrentPath -ine $currentPaths['ProjectTracker']) {
        throw "IIS application '$($projectTrackerGateway.Path)' must point to the current Project Tracker release."
    }
    $gatewayLocation = "$($projectTrackerGateway.Site)$($projectTrackerGateway.Path)"
    $applicationHostConfiguration = $serverManager.GetApplicationHostConfiguration()
    $anonymousEnabled = [bool]$applicationHostConfiguration.GetSection(
        'system.webServer/security/authentication/anonymousAuthentication',
        $gatewayLocation).GetAttributeValue('enabled')
    $windowsEnabled = [bool]$applicationHostConfiguration.GetSection(
        'system.webServer/security/authentication/windowsAuthentication',
        $gatewayLocation).GetAttributeValue('enabled')
    if ($anonymousEnabled -or -not $windowsEnabled) {
        throw "IIS application '$($projectTrackerGateway.Path)' must disable Anonymous Authentication and enable Windows Authentication."
    }
}
finally {
    $serverManager.Dispose()
}

foreach ($application in $applications) {
    if ((Get-WebsiteState -Name $application.Name).Value -ne 'Started') {
        throw "IIS site '$($application.Name)' is not started. No changes were made."
    }
    if ((Get-WebAppPoolState -Name $application.Name).Value -ne 'Started') {
        throw "IIS application pool '$($application.Name)' is not started. No changes were made."
    }
    $currentHealth = Get-HealthResult -Application $application
    if (-not $currentHealth.Healthy) {
        throw "The current '$($application.Name)' health endpoint is not HTTP 200. No changes were made. $($currentHealth.Detail)"
    }
}
if ((Get-WebAppPoolState -Name $projectTrackerGateway.Pool).Value -ne 'Started') {
    throw "IIS application pool '$($projectTrackerGateway.Pool)' is not started. No changes were made."
}
$currentGatewayHealth = Get-ProjectTrackerGatewayHealthResult
if (-not $currentGatewayHealth.Healthy) {
    throw "The current Project Tracker gateway health endpoint is not HTTP 200. No changes were made. $($currentGatewayHealth.Detail)"
}

$retainedQualitySnapshot = $null
$packageQualityPath = Join-Path $packagePath $qualityApplication.Folder
$activeQualityPath = $currentPaths[$qualityApplication.Name]
if ($RetainVerifiedQuality) {
    [void](Read-QualityProductionConfiguration -Path (
        Join-Path $activeQualityPath 'appsettings.Production.json'))
    Assert-QualitySanitizedApplicationManifestEqual `
        -SourceRoot $packageQualityPath `
        -CandidateRoot $activeQualityPath
    $retainedQualitySnapshot = Get-HubRetainedQualityBoundarySnapshot `
        -SiteName $qualityApplication.Name `
        -PoolName $qualityApplication.Name `
        -MainDll $qualityApplication.MainDll `
        -HealthUri "http://localhost:$($qualityApplication.Port)/api/health"
}

$deploymentAction = if ($RetainVerifiedQuality) {
    'Create a sanitized immutable release for four applications, retain verified Quality without mutation, switch IIS paths including the Project Tracker gateway, and verify health with rollback on failure'
}
else {
    'Create a sanitized immutable release, switch IIS paths including the Project Tracker gateway, and verify health with rollback on failure'
}
if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName release '$releasePath'",
        $deploymentAction)) {
    if ($RetainVerifiedQuality) {
        Write-Output 'WHATIF_READY_HUB_RELEASE_WITH_VERIFIED_QUALITY_RETAINED'
    }
    else {
        Write-Output 'WHATIF_READY'
    }
    return
}

if (Test-Path -LiteralPath $releasePath) {
    throw "Release destination appeared after preflight and will not be overwritten: $releasePath"
}

$liveIisTouched = $false
$pathsSwitchAttempted = $false
$newPaths = @{}
try {
    New-Item -ItemType Directory -Path $releasePath -Force | Out-Null
    foreach ($application in $deploymentApplications) {
        $sourcePath = Join-Path $packagePath $application.Folder
        $candidatePath = Join-Path $releasePath $application.Folder
        Copy-SanitizedApplication -Source $sourcePath -Destination $candidatePath

        $currentProductionSettings = Join-Path $currentPaths[$application.Name] 'appsettings.Production.json'
        $candidateProductionSettings = Join-Path $candidatePath 'appsettings.Production.json'
        Copy-Item -LiteralPath $currentProductionSettings -Destination $candidateProductionSettings

        $oldHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $currentProductionSettings).Hash
        $copiedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $candidateProductionSettings).Hash
        if ($oldHash -ne $copiedHash) {
            throw "Copied production settings hash mismatch for '$($application.Name)'."
        }
        if ($application.Name -eq 'SonAeroPortal') {
            $portalProductionTemplate = Join-Path $PSScriptRoot 'templates\portal.appsettings.Production.json'
            $catalogResult = Sync-PortalProductionApplicationCatalog `
                -CandidatePortalPath $candidatePath `
                -ProductionTemplatePath $portalProductionTemplate
            Write-Host ($catalogResult | Format-List | Out-String)
        }

        $candidateWebConfig = Join-Path $candidatePath 'web.config'
        $candidateMainDll = Join-Path $candidatePath $application.MainDll
        if (-not (Test-Path -LiteralPath $candidateMainDll -PathType Leaf)) {
            throw "Candidate application DLL is missing: $candidateMainDll"
        }
        Assert-ValidWebConfig -Path $candidateWebConfig -MainDll $application.MainDll
        Assert-JsonFile -Path $candidateProductionSettings
        if ($application.Name -eq $qualityApplication.Name) {
            [void](Read-QualityProductionConfiguration -Path $candidateProductionSettings)
        }
        $developmentSettings = @(Get-ChildItem -LiteralPath $candidatePath -Recurse -File -Force |
            Where-Object Name -Like 'appsettings.Development*.json')
        if ($developmentSettings.Count -gt 0) {
            throw "Development configuration was found in candidate '$candidatePath'."
        }
        if ($RetainVerifiedQuality) {
            Assert-QualitySanitizedApplicationManifestEqual `
                -SourceRoot $sourcePath `
                -CandidateRoot $candidatePath
        }

        & icacls.exe $candidatePath /grant "IIS AppPool\$($application.Name):(OI)(CI)RX" /t /c | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Read/execute permission assignment failed for '$candidatePath'."
        }
        if ($application.Name -eq 'ProjectTracker') {
            & icacls.exe $candidatePath /grant "IIS AppPool\$($projectTrackerGateway.Pool):(OI)(CI)RX" /t /c | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Gateway read/execute permission assignment failed for '$candidatePath'."
            }
        }
        $newPaths[$application.Name] = $candidatePath
    }

    # No live IIS state is touched until every immutable candidate is complete and validated.
    if ($RetainVerifiedQuality) {
        Assert-RetainedQualityPreserved `
            -ExpectedSnapshot $retainedQualitySnapshot `
            -PackageQualityPath $packageQualityPath `
            -ActiveQualityPath $activeQualityPath `
            -Phase 'pre-IIS-mutation verification'
    }
    $liveIisTouched = $true
    Stop-HubApplications
    $pathsSwitchAttempted = $true
    Set-IisPhysicalPaths -PathsBySite $newPaths
    Start-HubApplications

    if ($RetainVerifiedQuality) {
        Assert-RetainedQualityPreserved `
            -ExpectedSnapshot $retainedQualitySnapshot `
            -PackageQualityPath $packageQualityPath `
            -ActiveQualityPath $activeQualityPath `
            -Phase 'successful deployment verification'
    }

    $successStatus = if ($RetainVerifiedQuality) {
        'HUB_RELEASE_DEPLOYED_AND_HEALTHY_WITH_VERIFIED_QUALITY_RETAINED'
    }
    else {
        'HUB_RELEASE_DEPLOYED_AND_HEALTHY'
    }
    $successResult = [ordered]@{
        Status = $successStatus
        ReleaseId = $ReleaseId
        ReleasePath = $releasePath
        PortalUrl = "http://$ExpectedComputerName`:5140"
    }
    if ($RetainVerifiedQuality) {
        $successResult.RetainedQualityPath = $activeQualityPath
    }
    [pscustomobject]$successResult | Format-List
    Write-Output $successStatus
}
catch {
    $deploymentFailure = $_.Exception.Message
    if (-not $liveIisTouched) {
        throw "Release preparation failed before IIS was changed. The incomplete release was retained for inspection at '$releasePath'. $deploymentFailure"
    }

    $rollbackErrors = New-Object System.Collections.Generic.List[string]
    try { Stop-HubApplications } catch { $rollbackErrors.Add("Stop: $($_.Exception.Message)") }
    if ($pathsSwitchAttempted) {
        try { Set-IisPhysicalPaths -PathsBySite $currentPaths } catch { $rollbackErrors.Add("Paths: $($_.Exception.Message)") }
    }
    try { Start-HubApplications } catch { $rollbackErrors.Add("Start/health: $($_.Exception.Message)") }
    if ($RetainVerifiedQuality) {
        try {
            Assert-RetainedQualityPreserved `
                -ExpectedSnapshot $retainedQualitySnapshot `
                -PackageQualityPath $packageQualityPath `
                -ActiveQualityPath $activeQualityPath `
                -Phase 'rollback verification'
        }
        catch { $rollbackErrors.Add("Retained Quality: $($_.Exception.Message)") }
    }

    if ($rollbackErrors.Count -eq 0) {
        throw "Release deployment failed and all previous IIS paths were restored healthy. The failed release was retained at '$releasePath'. $deploymentFailure"
    }
    throw "Release deployment failed. Rollback also reported: $($rollbackErrors -join ' | '). The release was retained at '$releasePath'. Original failure: $deploymentFailure"
}
