<#
    Deploys one immutable SON-AERO Hub release on SON-IIS2.

    The package must contain four published application folders. Development settings are
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

    [ValidateRange(30, 600)]
    [int]$HealthTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

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
    }
)

$projectTrackerGateway = [pscustomobject]@{
    Site = 'SonAeroPortal'
    Path = '/project-tracker-api'
    Pool = 'ProjectTrackerAdminGateway'
    Port = 5140
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated Windows PowerShell session.'
    }
}

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path)).TrimEnd('\')
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
    $arguments = [string]$aspNetCoreNodes[0].arguments
    if ($arguments -notmatch [regex]::Escape($MainDll)) {
        throw "'$Path' does not launch the expected application DLL '$MainDll'."
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

function Test-HealthOnce {
    param([Parameter(Mandatory = $true)][object]$Application)
    $uri = "http://localhost:$($Application.Port)/api/health"
    try {
        $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec 10
        return ($response.StatusCode -eq 200)
    }
    catch {
        return $false
    }
}

function Wait-ApplicationHealth {
    param(
        [Parameter(Mandatory = $true)][object[]]$Targets,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $pending = @($Targets)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        foreach ($application in @($pending)) {
            if (Test-HealthOnce -Application $application) {
                $pending = @($pending | Where-Object Name -NE $application.Name)
            }
        }
        if ($pending.Count -gt 0) { Start-Sleep -Milliseconds 750 }
    } while ($pending.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)

    if ($pending.Count -gt 0) {
        throw "Health verification timed out for: $($pending.Name -join ', ')."
    }
}

function Test-ProjectTrackerGatewayHealthOnce {
    $uri = "http://localhost:$($projectTrackerGateway.Port)$($projectTrackerGateway.Path)/api/health"
    try {
        $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec 10
        return ($response.StatusCode -eq 200)
    }
    catch {
        return $false
    }
}

function Wait-ProjectTrackerGatewayHealth {
    param([Parameter(Mandatory = $true)][int]$TimeoutSeconds)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (Test-ProjectTrackerGatewayHealthOnce) { return }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Project Tracker gateway health verification timed out at '$($projectTrackerGateway.Path)'."
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
    foreach ($application in $applications) {
        Request-IisState -Kind Site -Name $application.Name -State Stopped
    }
    Wait-IisState -Kind Site -Names @($applications.Name) -State Stopped

    $poolNames = @($applications.Name) + @($projectTrackerGateway.Pool)
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
    $tracker = @($applications | Where-Object Name -EQ 'ProjectTracker')[0]
    Start-OneApplication -Application $tracker
    Wait-ApplicationHealth -Targets @($tracker) -TimeoutSeconds $HealthTimeoutSeconds

    Request-IisState -Kind Pool -Name $projectTrackerGateway.Pool -State Started
    Wait-IisState -Kind Pool -Names @($projectTrackerGateway.Pool) -State Started

    $remaining = @($applications | Where-Object Name -NE 'ProjectTracker')
    foreach ($application in $remaining) {
        Start-OneApplication -Application $application
    }
    Wait-ApplicationHealth -Targets $remaining -TimeoutSeconds $HealthTimeoutSeconds
    Wait-ProjectTrackerGatewayHealth -TimeoutSeconds $HealthTimeoutSeconds
}

function Set-IisPhysicalPaths {
    param([Parameter(Mandatory = $true)][hashtable]$PathsBySite)

    $serverManager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        foreach ($application in $applications) {
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

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
}
Assert-Administrator

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
        $productionSettings = Join-Path $currentPath 'appsettings.Production.json'
        if (-not (Test-Path -LiteralPath $productionSettings -PathType Leaf)) {
            throw "Current production settings are missing: $productionSettings"
        }
        Assert-JsonFile -Path $productionSettings
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
    if (-not (Test-HealthOnce -Application $application)) {
        throw "The current '$($application.Name)' health endpoint is not HTTP 200. No changes were made."
    }
}
if ((Get-WebAppPoolState -Name $projectTrackerGateway.Pool).Value -ne 'Started') {
    throw "IIS application pool '$($projectTrackerGateway.Pool)' is not started. No changes were made."
}
if (-not (Test-ProjectTrackerGatewayHealthOnce)) {
    throw "The current Project Tracker gateway health endpoint is not HTTP 200. No changes were made."
}

if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName release '$releasePath'",
        'Create a sanitized immutable release, switch IIS paths including the Project Tracker gateway, and verify health with rollback on failure')) {
    Write-Output 'WHATIF_READY'
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
    foreach ($application in $applications) {
        $sourcePath = Join-Path $packagePath $application.Folder
        $candidatePath = Join-Path $releasePath $application.Folder
        Copy-SanitizedApplication -Source $sourcePath -Destination $candidatePath

        $currentProductionSettings = Join-Path $currentPaths[$application.Name] 'appsettings.Production.json'
        $candidateProductionSettings = Join-Path $candidatePath 'appsettings.Production.json'
        Copy-Item -LiteralPath $currentProductionSettings -Destination $candidateProductionSettings

        $candidateWebConfig = Join-Path $candidatePath 'web.config'
        $candidateMainDll = Join-Path $candidatePath $application.MainDll
        if (-not (Test-Path -LiteralPath $candidateMainDll -PathType Leaf)) {
            throw "Candidate application DLL is missing: $candidateMainDll"
        }
        Assert-ValidWebConfig -Path $candidateWebConfig -MainDll $application.MainDll
        Assert-JsonFile -Path $candidateProductionSettings
        $oldHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $currentProductionSettings).Hash
        $newHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $candidateProductionSettings).Hash
        if ($oldHash -ne $newHash) {
            throw "Production settings hash mismatch for '$($application.Name)'."
        }
        $developmentSettings = @(Get-ChildItem -LiteralPath $candidatePath -Recurse -File -Force |
            Where-Object Name -Like 'appsettings.Development*.json')
        if ($developmentSettings.Count -gt 0) {
            throw "Development configuration was found in candidate '$candidatePath'."
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
    $liveIisTouched = $true
    Stop-HubApplications
    $pathsSwitchAttempted = $true
    Set-IisPhysicalPaths -PathsBySite $newPaths
    Start-HubApplications

    [pscustomobject]@{
        Status = 'HUB_RELEASE_DEPLOYED_AND_HEALTHY'
        ReleaseId = $ReleaseId
        ReleasePath = $releasePath
        PortalUrl = "http://$ExpectedComputerName`:5140"
    } | Format-List
    Write-Output 'HUB_RELEASE_DEPLOYED_AND_HEALTHY'
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

    if ($rollbackErrors.Count -eq 0) {
        throw "Release deployment failed and all previous IIS paths were restored healthy. The failed release was retained at '$releasePath'. $deploymentFailure"
    }
    throw "Release deployment failed. Rollback also reported: $($rollbackErrors -join ' | '). The release was retained at '$releasePath'. Original failure: $deploymentFailure"
}
