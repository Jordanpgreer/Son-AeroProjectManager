<#
    Deploys one immutable Project Tracker release on SON-IIS2 without changing the
    Portal root or the Engineering, Estimating, and Quality applications.

    PackageRoot is the Hub staging root produced by Publish-Hub.ps1 and must contain
    a ProjectTracker folder. The active appsettings.Production.json is carried forward
    byte-for-byte. Both the direct site and Portal gateway paths are switched together
    and restored together if any stop, commit, start, or health verification fails.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$ReleaseId,

    [string]$ReleaseRoot = 'C:\SonAero\releases\project-tracker',
    [string]$ExpectedComputerName = 'SON-IIS2',

    [ValidateRange(30, 600)]
    [int]$HealthTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

$directSiteName = 'ProjectTracker'
$directPoolName = 'ProjectTracker'
$directPort = 5135
$gatewaySiteName = 'SonAeroPortal'
$gatewayPath = '/project-tracker-api'
$gatewayPoolName = 'ProjectTrackerAdminGateway'
$gatewayPort = 5140
$mainDll = 'ProjectTracker.Api.dll'

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

function Assert-ValidWebConfig {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedMainDll
    )
    try { [xml]$configuration = Get-Content -LiteralPath $Path -Raw }
    catch { throw "Invalid web.config XML at '$Path': $($_.Exception.Message)" }
    $nodes = @($configuration.SelectNodes('//aspNetCore'))
    if ($nodes.Count -ne 1) { throw "'$Path' must contain exactly one aspNetCore element." }
    if ([string]$nodes[0].arguments -notmatch [regex]::Escape($ExpectedMainDll)) {
        throw "'$Path' does not launch the expected application DLL '$ExpectedMainDll'."
    }
}

function Assert-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    try {
        $content = Get-Content -LiteralPath $Path -Raw
        if ([string]::IsNullOrWhiteSpace($content)) { throw 'The file is empty.' }
        $null = $content | ConvertFrom-Json
    }
    catch { throw "Invalid JSON in '$Path': $($_.Exception.Message)" }
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

function Get-HealthResult {
    param([Parameter(Mandatory = $true)][string]$Uri)
    try {
        $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $Uri -TimeoutSec 10
        return [pscustomobject]@{ Healthy = ($response.StatusCode -eq 200); Detail = "HTTP $($response.StatusCode)" }
    }
    catch {
        $status = $null
        if ($null -ne $_.Exception.Response) {
            try { $status = [int]$_.Exception.Response.StatusCode } catch {}
        }
        $detail = if ($null -ne $status) { "HTTP $status" } else { $_.Exception.Message }
        return [pscustomobject]@{ Healthy = $false; Detail = $detail }
    }
}

function Wait-EndpointHealth {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastResult = $null
    do {
        $lastResult = Get-HealthResult -Uri $Uri
        if ($lastResult.Healthy) { return }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$Label health verification timed out at '$Uri'. Last result: $($lastResult.Detail)"
}

function Get-IisStateValue {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Site', 'Pool')][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if ($Kind -eq 'Site') { return (Get-WebsiteState -Name $Name).Value }
    return (Get-WebAppPoolState -Name $Name).Value
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
        $current = Get-IisStateValue -Kind $Kind -Name $Name
        if ($current -eq $State) { return }
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
        else { Start-Sleep -Milliseconds 500 }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$Kind '$Name' did not become '$State'; its current state is '$current'."
}

function Stop-ProjectTrackerRuntime {
    Request-IisState -Kind Pool -Name $gatewayPoolName -State Stopped
    Request-IisState -Kind Site -Name $directSiteName -State Stopped
    Request-IisState -Kind Pool -Name $directPoolName -State Stopped
}

function Start-ProjectTrackerRuntime {
    Request-IisState -Kind Pool -Name $directPoolName -State Started
    Request-IisState -Kind Site -Name $directSiteName -State Started
    Wait-EndpointHealth -Label 'Direct Project Tracker' -Uri "http://localhost:$directPort/api/health" `
        -TimeoutSeconds $HealthTimeoutSeconds
    Request-IisState -Kind Pool -Name $gatewayPoolName -State Started
    Wait-EndpointHealth -Label 'Portal Project Tracker gateway' `
        -Uri "http://localhost:$gatewayPort$gatewayPath/api/health" -TimeoutSeconds $HealthTimeoutSeconds
}

function Get-ProjectTrackerIisBoundary {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $directSite = $manager.Sites[$directSiteName]
        $directPool = $manager.ApplicationPools[$directPoolName]
        $portalSite = $manager.Sites[$gatewaySiteName]
        $gatewayPool = $manager.ApplicationPools[$gatewayPoolName]
        if ($null -eq $directSite -or $null -eq $directPool) { throw 'Required Project Tracker IIS site or pool is missing.' }
        if ($null -eq $portalSite -or $null -eq $gatewayPool) { throw 'Required Portal site or gateway pool is missing.' }
        $directApplication = $directSite.Applications['/']
        $gatewayApplication = $portalSite.Applications[$gatewayPath]
        if ($null -eq $directApplication -or $null -eq $gatewayApplication) { throw 'Required direct or gateway IIS application is missing.' }
        if ($directApplication.ApplicationPoolName -ine $directPoolName) { throw "Direct site must use pool '$directPoolName'." }
        if ($gatewayApplication.ApplicationPoolName -ine $gatewayPoolName) { throw "Gateway must use pool '$gatewayPoolName'." }
        if ($gatewayPool.ManagedRuntimeVersion -ne '' -or -not $gatewayPool.AutoStart -or
            $gatewayPool.StartMode -ne [Microsoft.Web.Administration.StartMode]::AlwaysRunning -or
            $gatewayPool.ProcessModel.IdentityType -ne [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity -or
            -not $gatewayPool.ProcessModel.LoadUserProfile -or
            $gatewayPool.ProcessModel.IdleTimeout -ne [TimeSpan]::Zero) {
            throw "Gateway pool '$gatewayPoolName' is not in its required restricted always-running configuration."
        }
        $httpBindings = @($directSite.Bindings | Where-Object Protocol -EQ 'http')
        if ($httpBindings.Count -ne 1 -or $httpBindings[0].BindingInformation -ne "*:${directPort}:") {
            throw "Project Tracker must retain exactly one HTTP binding '*:${directPort}:'."
        }
        $configuration = $manager.GetApplicationHostConfiguration()
        $directAnonymous = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/anonymousAuthentication', $directSiteName).GetAttributeValue('enabled')
        $directWindows = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/windowsAuthentication', $directSiteName).GetAttributeValue('enabled')
        $gatewayLocation = "$gatewaySiteName$gatewayPath"
        $gatewayAnonymous = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/anonymousAuthentication', $gatewayLocation).GetAttributeValue('enabled')
        $gatewayWindows = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/windowsAuthentication', $gatewayLocation).GetAttributeValue('enabled')
        if (-not $directAnonymous -or -not $directWindows) {
            throw 'Direct Project Tracker authentication must remain Anonymous=True and Windows=True.'
        }
        if ($gatewayAnonymous -or -not $gatewayWindows) {
            throw 'Portal gateway authentication must remain Anonymous=False and Windows=True.'
        }
        return [pscustomobject]@{
            DirectPath = Get-FullPath -Path $directApplication.VirtualDirectories['/'].PhysicalPath
            GatewayPath = Get-FullPath -Path $gatewayApplication.VirtualDirectories['/'].PhysicalPath
        }
    }
    finally { $manager.Dispose() }
}

function Set-ProjectTrackerPhysicalPaths {
    param(
        [Parameter(Mandatory = $true)][string]$DirectPath,
        [Parameter(Mandatory = $true)][string]$GatewayPhysicalPath
    )
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $directSite = $manager.Sites[$directSiteName]
        $portalSite = $manager.Sites[$gatewaySiteName]
        if ($null -eq $directSite -or $null -eq $portalSite) { throw 'Required IIS site disappeared during deployment.' }
        $gatewayApplication = $portalSite.Applications[$gatewayPath]
        if ($null -eq $gatewayApplication) { throw "IIS gateway '$gatewaySiteName$gatewayPath' disappeared during deployment." }
        $directSite.Applications['/'].VirtualDirectories['/'].PhysicalPath = $DirectPath
        $gatewayApplication.VirtualDirectories['/'].PhysicalPath = $GatewayPhysicalPath
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}

function Assert-ProjectTrackerPhysicalPaths {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedDirectPath,
        [Parameter(Mandatory = $true)][string]$ExpectedGatewayPath
    )
    $actual = Get-ProjectTrackerIisBoundary
    if ($actual.DirectPath -ine (Get-FullPath -Path $ExpectedDirectPath)) {
        throw "Direct Project Tracker path is '$($actual.DirectPath)', expected '$ExpectedDirectPath'."
    }
    if ($actual.GatewayPath -ine (Get-FullPath -Path $ExpectedGatewayPath)) {
        throw "Portal gateway path is '$($actual.GatewayPath)', expected '$ExpectedGatewayPath'."
    }
}

function Invoke-ProjectTrackerIisSwitch {
    param(
        [Parameter(Mandatory = $true)][string]$CurrentDirectPath,
        [Parameter(Mandatory = $true)][string]$CurrentGatewayPath,
        [Parameter(Mandatory = $true)][string]$CandidatePath
    )
    $switchAttempted = $false
    try {
        Stop-ProjectTrackerRuntime
        $switchAttempted = $true
        Set-ProjectTrackerPhysicalPaths -DirectPath $CandidatePath -GatewayPhysicalPath $CandidatePath
        Assert-ProjectTrackerPhysicalPaths -ExpectedDirectPath $CandidatePath -ExpectedGatewayPath $CandidatePath
        Start-ProjectTrackerRuntime
        Assert-ProjectTrackerPhysicalPaths -ExpectedDirectPath $CandidatePath -ExpectedGatewayPath $CandidatePath
    }
    catch {
        $deploymentFailure = $_.Exception.Message
        $rollbackErrors = New-Object System.Collections.Generic.List[string]
        try { Stop-ProjectTrackerRuntime } catch { $rollbackErrors.Add("Stop: $($_.Exception.Message)") }
        if ($switchAttempted) {
            try {
                Set-ProjectTrackerPhysicalPaths -DirectPath $CurrentDirectPath `
                    -GatewayPhysicalPath $CurrentGatewayPath
            }
            catch { $rollbackErrors.Add("Paths: $($_.Exception.Message)") }
        }
        try {
            Assert-ProjectTrackerPhysicalPaths -ExpectedDirectPath $CurrentDirectPath `
                -ExpectedGatewayPath $CurrentGatewayPath
        }
        catch { $rollbackErrors.Add("Path verification: $($_.Exception.Message)") }
        try { Start-ProjectTrackerRuntime } catch { $rollbackErrors.Add("Start/health: $($_.Exception.Message)") }
        try {
            Assert-ProjectTrackerPhysicalPaths -ExpectedDirectPath $CurrentDirectPath `
                -ExpectedGatewayPath $CurrentGatewayPath
        }
        catch { $rollbackErrors.Add("Final path verification: $($_.Exception.Message)") }
        if ($rollbackErrors.Count -eq 0) {
            throw "Project Tracker release failed and both prior IIS paths were restored healthy. The failed candidate was retained at '$CandidatePath'. $deploymentFailure"
        }
        throw "Project Tracker release failed. Rollback also reported: $($rollbackErrors -join ' | '). The failed candidate was retained at '$CandidatePath'. Original failure: $deploymentFailure"
    }
}

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
}
Assert-DeploymentIdentity
if ($ReleaseId -in @('.', '..')) { throw 'ReleaseId cannot be a relative-path marker.' }
$packagePath = Get-FullPath -Path $PackageRoot
$sourcePath = Join-Path $packagePath 'ProjectTracker'
$releaseRootPath = Get-FullPath -Path $ReleaseRoot
if ($releaseRootPath -eq [IO.Path]::GetPathRoot($releaseRootPath)) { throw 'ReleaseRoot cannot be a drive root.' }
if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
    throw "Project Tracker package folder is missing: $sourcePath"
}
$releasePath = Get-FullPath -Path (Join-Path $releaseRootPath $ReleaseId)
if (-not $releasePath.StartsWith($releaseRootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The resolved release destination escaped ReleaseRoot.'
}
if (Test-Path -LiteralPath $releasePath) {
    throw "Release destination already exists and will not be overwritten: $releasePath"
}
if ($releasePath.StartsWith($sourcePath + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $sourcePath.StartsWith($releasePath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package source and release destination cannot contain one another.'
}
$sourceItem = Get-Item -LiteralPath $sourcePath
if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Package '$sourcePath' is a reparse point and cannot be deployed."
}
$reparsePoints = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -Force | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
})
if ($reparsePoints.Count -gt 0) { throw "Package '$sourcePath' contains a reparse point and cannot be deployed." }
$sourceWebConfig = Join-Path $sourcePath 'web.config'
$sourceMainDll = Join-Path $sourcePath $mainDll
if (-not (Test-Path -LiteralPath $sourceWebConfig -PathType Leaf)) { throw "Package web.config is missing: $sourceWebConfig" }
if (-not (Test-Path -LiteralPath $sourceMainDll -PathType Leaf)) { throw "Package DLL is missing: $sourceMainDll" }
Assert-ValidWebConfig -Path $sourceWebConfig -ExpectedMainDll $mainDll

$priorWhatIfPreference = $WhatIfPreference
try { $WhatIfPreference = $false; Import-Module WebAdministration -ErrorAction Stop }
finally { $WhatIfPreference = $priorWhatIfPreference }
if (-not ('Microsoft.Web.Administration.ServerManager' -as [type])) {
    $assemblyPath = Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) { throw "IIS assembly not found: $assemblyPath" }
    Add-Type -Path $assemblyPath -ErrorAction Stop
}

$boundary = Get-ProjectTrackerIisBoundary
$currentDirectPath = $boundary.DirectPath
$currentGatewayPath = $boundary.GatewayPath
if ($currentDirectPath -ine $currentGatewayPath) {
    throw 'Direct and gateway paths must reference the same active release before deployment.'
}

$currentProductionSettings = Join-Path $currentDirectPath 'appsettings.Production.json'
if (-not (Test-Path -LiteralPath $currentProductionSettings -PathType Leaf)) { throw "Production settings are missing: $currentProductionSettings" }
Assert-JsonFile -Path $currentProductionSettings
foreach ($state in @(
    @{ Kind = 'Site'; Name = $directSiteName },
    @{ Kind = 'Site'; Name = $gatewaySiteName },
    @{ Kind = 'Pool'; Name = $directPoolName },
    @{ Kind = 'Pool'; Name = $gatewayPoolName }
)) {
    if ((Get-IisStateValue -Kind $state.Kind -Name $state.Name) -ne 'Started') {
        throw "$($state.Kind) '$($state.Name)' is not started. No changes were made."
    }
}
Wait-EndpointHealth -Label 'Current direct Project Tracker' -Uri "http://localhost:$directPort/api/health" -TimeoutSeconds 30
Wait-EndpointHealth -Label 'Current Portal gateway' -Uri "http://localhost:$gatewayPort$gatewayPath/api/health" -TimeoutSeconds 30

if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName Project Tracker release '$releasePath'",
        'Create an immutable Project Tracker release, switch only the direct and gateway paths, and verify health with rollback')) {
    Write-Output 'WHATIF_READY_PROJECT_TRACKER_RELEASE'
    return
}
if (Test-Path -LiteralPath $releasePath) { throw "Release destination appeared after preflight: $releasePath" }

try {
    New-Item -ItemType Directory -Path $releaseRootPath -Force | Out-Null
    Copy-SanitizedApplication -Source $sourcePath -Destination $releasePath
    $candidateProductionSettings = Join-Path $releasePath 'appsettings.Production.json'
    Copy-Item -LiteralPath $currentProductionSettings -Destination $candidateProductionSettings
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $currentProductionSettings).Hash -ne
        (Get-FileHash -Algorithm SHA256 -LiteralPath $candidateProductionSettings).Hash) {
        throw 'Copied production settings hash mismatch.'
    }
    Assert-ValidWebConfig -Path (Join-Path $releasePath 'web.config') -ExpectedMainDll $mainDll
    Assert-JsonFile -Path $candidateProductionSettings
    if (-not (Test-Path -LiteralPath (Join-Path $releasePath $mainDll) -PathType Leaf)) {
        throw "Candidate application DLL is missing: $mainDll"
    }
    $developmentSettings = @(Get-ChildItem -LiteralPath $releasePath -File -Recurse -Force |
        Where-Object Name -Like 'appsettings.Development*.json')
    if ($developmentSettings.Count -gt 0) { throw 'Development configuration was found in the candidate release.' }
    foreach ($poolName in @($directPoolName, $gatewayPoolName)) {
        & icacls.exe $releasePath /grant "IIS AppPool\$poolName`:(OI)(CI)RX" /t /c | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Read/execute permission assignment failed for '$poolName'." }
    }
}
catch {
    throw "Project Tracker release preparation failed before IIS was changed. The incomplete candidate was retained at '$releasePath'. $($_.Exception.Message)"
}

$cutoverBoundary = Get-ProjectTrackerIisBoundary
if ($cutoverBoundary.DirectPath -ine $currentDirectPath -or
    $cutoverBoundary.GatewayPath -ine $currentGatewayPath) {
    throw "Project Tracker IIS paths changed during candidate preparation. No IIS state was changed; the candidate was retained at '$releasePath'."
}

Invoke-ProjectTrackerIisSwitch -CurrentDirectPath $currentDirectPath `
    -CurrentGatewayPath $currentGatewayPath -CandidatePath $releasePath

[pscustomobject]@{
    Status = 'PROJECT_TRACKER_RELEASE_DEPLOYED_AND_HEALTHY'
    ReleaseId = $ReleaseId
    ReleasePath = $releasePath
} | Format-List
Write-Output 'PROJECT_TRACKER_RELEASE_DEPLOYED_AND_HEALTHY'
