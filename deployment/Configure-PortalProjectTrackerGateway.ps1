<#
    Creates the same-origin Project Tracker API application used by the Hub Admin UI.
    Run once on SON-IIS2 before deploying a Portal build that targets /project-tracker-api.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$ExpectedComputerName = 'SON-IIS2',
    [string]$PortalSiteName = 'SonAeroPortal',
    [string]$ProjectTrackerSiteName = 'ProjectTracker',
    [string]$GatewayPath = '/project-tracker-api',
    [string]$GatewayPoolName = 'ProjectTrackerAdminGateway',
    [ValidateRange(15, 300)]
    [int]$HealthTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

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

function Test-GatewayHealth {
    $uri = "http://localhost:5140$GatewayPath/api/health"
    try {
        $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec 10
        return ($response.StatusCode -eq 200)
    }
    catch {
        return $false
    }
}

function Wait-GatewayHealth {
    $deadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
    do {
        if (Test-GatewayHealth) { return }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Gateway health verification timed out at '$GatewayPath'."
}

function Get-GatewayAuthenticationState {
    param(
        [Parameter(Mandatory = $true)]$Manager,
        [Parameter(Mandatory = $true)][string]$Location
    )

    $configuration = $Manager.GetApplicationHostConfiguration()
    $anonymousSection = $configuration.GetSection(
        'system.webServer/security/authentication/anonymousAuthentication',
        $Location)
    $windowsSection = $configuration.GetSection(
        'system.webServer/security/authentication/windowsAuthentication',
        $Location)
    return [pscustomobject]@{
        AnonymousEnabled = [bool]$anonymousSection.GetAttributeValue('enabled')
        WindowsEnabled = [bool]$windowsSection.GetAttributeValue('enabled')
    }
}

function Set-GatewayAuthenticationState {
    param(
        [Parameter(Mandatory = $true)]$Manager,
        [Parameter(Mandatory = $true)][string]$Location,
        [Parameter(Mandatory = $true)][bool]$AnonymousEnabled,
        [Parameter(Mandatory = $true)][bool]$WindowsEnabled
    )

    $configuration = $Manager.GetApplicationHostConfiguration()
    $configuration.GetSection(
        'system.webServer/security/authentication/anonymousAuthentication',
        $Location).SetAttributeValue('enabled', $AnonymousEnabled)
    $configuration.GetSection(
        'system.webServer/security/authentication/windowsAuthentication',
        $Location).SetAttributeValue('enabled', $WindowsEnabled)
}

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
}
if ($GatewayPath -notmatch '^/[a-z0-9][a-z0-9-]{0,62}$') {
    throw 'GatewayPath must be one lowercase root application segment such as /project-tracker-api.'
}

$priorWhatIfPreference = $WhatIfPreference
try {
    $WhatIfPreference = $false
    Import-Module WebAdministration -ErrorAction Stop
}
finally {
    $WhatIfPreference = $priorWhatIfPreference
}

if (-not ('Microsoft.Web.Administration.ServerManager' -as [type])) {
    $assemblyPath = Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "The IIS administration assembly was not found: $assemblyPath"
    }
    Add-Type -Path $assemblyPath -ErrorAction Stop
}

$manager = New-Object Microsoft.Web.Administration.ServerManager
$priorApplication = $null
$priorPool = $null
$priorAuthentication = $null
$poolExisted = $false
try {
    $portalSite = $manager.Sites[$PortalSiteName]
    $trackerSite = $manager.Sites[$ProjectTrackerSiteName]
    if (-not $portalSite) { throw "Required IIS site '$PortalSiteName' is missing." }
    if (-not $trackerSite) { throw "Required IIS site '$ProjectTrackerSiteName' is missing." }
    if (-not $manager.ApplicationPools[$ProjectTrackerSiteName]) {
        throw "Required IIS application pool '$ProjectTrackerSiteName' is missing."
    }

    $trackerRoot = $trackerSite.Applications['/']
    $trackerVirtualDirectory = $trackerRoot.VirtualDirectories['/']
    $trackerPhysicalPath = Get-FullPath -Path $trackerVirtualDirectory.PhysicalPath
    if (-not (Test-Path -LiteralPath $trackerPhysicalPath -PathType Container)) {
        throw "The active Project Tracker path does not exist: $trackerPhysicalPath"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $trackerPhysicalPath 'ProjectTracker.Api.dll') -PathType Leaf)) {
        throw "The active Project Tracker publication is incomplete: $trackerPhysicalPath"
    }

    $poolExisted = $null -ne $manager.ApplicationPools[$GatewayPoolName]
    if ($poolExisted) {
        $existingPool = $manager.ApplicationPools[$GatewayPoolName]
        $priorPool = [pscustomobject]@{
            ManagedRuntimeVersion = $existingPool.ManagedRuntimeVersion
            AutoStart = $existingPool.AutoStart
            StartMode = $existingPool.StartMode
            IdentityType = $existingPool.ProcessModel.IdentityType
            LoadUserProfile = $existingPool.ProcessModel.LoadUserProfile
            IdleTimeout = $existingPool.ProcessModel.IdleTimeout
        }
    }
    $existingApplication = $portalSite.Applications[$GatewayPath]
    if ($existingApplication) {
        $existingVirtualDirectory = $existingApplication.VirtualDirectories['/']
        $priorApplication = [pscustomobject]@{
            Path = Get-FullPath -Path $existingVirtualDirectory.PhysicalPath
            Pool = $existingApplication.ApplicationPoolName
            PreloadEnabled = [bool]$existingApplication.GetAttributeValue('preloadEnabled')
        }
        $priorAuthentication = Get-GatewayAuthenticationState `
            -Manager $manager -Location "$PortalSiteName$GatewayPath"
    }

    $alreadyConfigured = $poolExisted `
        -and $null -ne $priorApplication `
        -and $priorPool.ManagedRuntimeVersion -eq '' `
        -and $priorPool.AutoStart `
        -and $priorPool.StartMode -eq [Microsoft.Web.Administration.StartMode]::AlwaysRunning `
        -and $priorPool.IdentityType -eq [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity `
        -and $priorPool.LoadUserProfile `
        -and $priorPool.IdleTimeout -eq [TimeSpan]::Zero `
        -and $priorApplication.Path -ieq $trackerPhysicalPath `
        -and $priorApplication.Pool -ieq $GatewayPoolName `
        -and $priorApplication.PreloadEnabled `
        -and -not $priorAuthentication.AnonymousEnabled `
        -and $priorAuthentication.WindowsEnabled
}
finally {
    $manager.Dispose()
}

if ($alreadyConfigured -and (Test-GatewayHealth)) {
    Write-Output 'PROJECT_TRACKER_GATEWAY_ALREADY_CONFIGURED_AND_HEALTHY'
    return
}

if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName/$PortalSiteName$GatewayPath",
        "Create a dedicated Windows-authenticated IIS application backed by the active Project Tracker release")) {
    Write-Output 'WHATIF_READY: no IIS applications or application pools were changed.'
    return
}

Assert-Administrator

$iisChanged = $false
try {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $gatewayPool = $manager.ApplicationPools[$GatewayPoolName]
        if (-not $gatewayPool) {
            $gatewayPool = $manager.ApplicationPools.Add($GatewayPoolName)
        }
        $gatewayPool.ManagedRuntimeVersion = ''
        $gatewayPool.AutoStart = $true
        $gatewayPool.StartMode = [Microsoft.Web.Administration.StartMode]::AlwaysRunning
        $gatewayPool.ProcessModel.IdentityType = [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity
        $gatewayPool.ProcessModel.LoadUserProfile = $true
        $gatewayPool.ProcessModel.IdleTimeout = [TimeSpan]::Zero

        $portalSite = $manager.Sites[$PortalSiteName]
        $gatewayApplication = $portalSite.Applications[$GatewayPath]
        if (-not $gatewayApplication) {
            $gatewayApplication = $portalSite.Applications.Add($GatewayPath, $trackerPhysicalPath)
        }
        $gatewayApplication.ApplicationPoolName = $GatewayPoolName
        $gatewayApplication.VirtualDirectories['/'].PhysicalPath = $trackerPhysicalPath
        $gatewayApplication.SetAttributeValue('preloadEnabled', $true)
        Set-GatewayAuthenticationState -Manager $manager `
            -Location "$PortalSiteName$GatewayPath" `
            -AnonymousEnabled $false -WindowsEnabled $true
        $manager.CommitChanges()
        $iisChanged = $true
    }
    finally {
        $manager.Dispose()
    }

    # The IIS virtual account is resolvable only after its application pool exists.
    & icacls.exe $trackerPhysicalPath /grant "IIS AppPool\$GatewayPoolName`:(OI)(CI)RX" /t /c | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Gateway read/execute permission assignment failed for '$trackerPhysicalPath'."
    }

    $poolState = (Get-WebAppPoolState -Name $GatewayPoolName).Value
    if ($poolState -ne 'Started') {
        Start-WebAppPool -Name $GatewayPoolName
    }
    Wait-GatewayHealth

    [pscustomobject]@{
        Status = 'PROJECT_TRACKER_GATEWAY_CONFIGURED_AND_HEALTHY'
        Url = "http://$ExpectedComputerName`:5140$GatewayPath"
        ProjectTrackerPath = $trackerPhysicalPath
        ApplicationPool = $GatewayPoolName
    } | Format-List
    Write-Output 'PROJECT_TRACKER_GATEWAY_CONFIGURED_AND_HEALTHY'
}
catch {
    $failure = $_.Exception.Message
    $rollbackErrors = New-Object System.Collections.Generic.List[string]
    if ($iisChanged) {
        try {
            $manager = New-Object Microsoft.Web.Administration.ServerManager
            try {
                $portalSite = $manager.Sites[$PortalSiteName]
                $gatewayApplication = $portalSite.Applications[$GatewayPath]
                if ($priorApplication) {
                    if (-not $gatewayApplication) {
                        $gatewayApplication = $portalSite.Applications.Add($GatewayPath, $priorApplication.Path)
                    }
                    $gatewayApplication.ApplicationPoolName = $priorApplication.Pool
                    $gatewayApplication.VirtualDirectories['/'].PhysicalPath = $priorApplication.Path
                    $gatewayApplication.SetAttributeValue('preloadEnabled', $priorApplication.PreloadEnabled)
                    Set-GatewayAuthenticationState -Manager $manager `
                        -Location "$PortalSiteName$GatewayPath" `
                        -AnonymousEnabled $priorAuthentication.AnonymousEnabled `
                        -WindowsEnabled $priorAuthentication.WindowsEnabled
                }
                elseif ($gatewayApplication) {
                    $portalSite.Applications.Remove($gatewayApplication)
                }

                if ($poolExisted) {
                    $gatewayPool = $manager.ApplicationPools[$GatewayPoolName]
                    if ($gatewayPool) {
                        $gatewayPool.ManagedRuntimeVersion = $priorPool.ManagedRuntimeVersion
                        $gatewayPool.AutoStart = $priorPool.AutoStart
                        $gatewayPool.StartMode = $priorPool.StartMode
                        $gatewayPool.ProcessModel.IdentityType = $priorPool.IdentityType
                        $gatewayPool.ProcessModel.LoadUserProfile = $priorPool.LoadUserProfile
                        $gatewayPool.ProcessModel.IdleTimeout = $priorPool.IdleTimeout
                    }
                }
                else {
                    $gatewayPool = $manager.ApplicationPools[$GatewayPoolName]
                    if ($gatewayPool) { $manager.ApplicationPools.Remove($gatewayPool) }
                }
                $manager.CommitChanges()
            }
            finally {
                $manager.Dispose()
            }
        }
        catch {
            $rollbackErrors.Add($_.Exception.Message)
        }
    }

    if ($rollbackErrors.Count -eq 0) {
        throw "Gateway configuration failed and the prior IIS configuration was restored. $failure"
    }
    throw "Gateway configuration failed. Rollback also reported: $($rollbackErrors -join ' | '). Original failure: $failure"
}
