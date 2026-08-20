<#
    Deploys one immutable Portal root release on SON-IIS2 without changing any
    module application or the existing Project Tracker gateway.

    PackageRoot is the Hub staging root produced by Publish-Hub.ps1 and must
    contain a Portal folder. The active appsettings.Production.json is carried
    forward byte-for-byte. Only the SonAeroPortal root application's physical
    path and root application pool are changed; the site and gateway remain up.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$ReleaseId,

    [string]$ReleaseRoot = 'C:\SonAero\releases\portal',
    [string]$ExpectedComputerName = 'SON-IIS2',

    [ValidateRange(30, 600)]
    [int]$HealthTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

$siteName = 'SonAeroPortal'
$poolName = 'SonAeroPortal'
$port = 5140
$gatewayPath = '/project-tracker-api'
$gatewayPoolName = 'ProjectTrackerAdminGateway'
$mainDll = 'Portal.Api.dll'

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

function Get-PoolStateValue {
    param([Parameter(Mandatory = $true)][string]$Name)
    return (Get-WebAppPoolState -Name $Name).Value
}

function Request-PortalPoolState {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Started', 'Stopped')][string]$State,
        [int]$TimeoutSeconds = 120
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $current = Get-PoolStateValue -Name $poolName
        if ($current -eq $State) { return }
        if ($State -eq 'Stopped' -and $current -eq 'Started') {
            try { Stop-WebAppPool -Name $poolName }
            catch { Start-Sleep -Milliseconds 500 }
        }
        elseif ($State -eq 'Started' -and $current -eq 'Stopped') {
            try { Start-WebAppPool -Name $poolName }
            catch { Start-Sleep -Milliseconds 500 }
        }
        else { Start-Sleep -Milliseconds 500 }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Portal root pool '$poolName' did not become '$State'; its current state is '$current'."
}

function Assert-NonTargetRuntimeStarted {
    if ((Get-WebsiteState -Name $siteName).Value -ne 'Started') {
        throw "Portal site '$siteName' must remain started."
    }
    if ((Get-PoolStateValue -Name $gatewayPoolName) -ne 'Started') {
        throw "Gateway pool '$gatewayPoolName' must remain started."
    }
}

function Get-PortalIisBoundary {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$siteName]
        $rootPool = $manager.ApplicationPools[$poolName]
        $gatewayPool = $manager.ApplicationPools[$gatewayPoolName]
        if ($null -eq $site -or $null -eq $rootPool) { throw 'Required Portal IIS site or root pool is missing.' }
        if ($null -eq $gatewayPool) { throw "Required gateway pool '$gatewayPoolName' is missing." }
        $rootApplication = $site.Applications['/']
        $gatewayApplication = $site.Applications[$gatewayPath]
        if ($null -eq $rootApplication -or $null -eq $gatewayApplication) {
            throw 'Required Portal root or Project Tracker gateway application is missing.'
        }
        if ($rootApplication.ApplicationPoolName -ine $poolName) {
            throw "Portal root must use application pool '$poolName'."
        }
        if ($gatewayApplication.ApplicationPoolName -ine $gatewayPoolName) {
            throw "Portal gateway must use application pool '$gatewayPoolName'."
        }
        $rootVirtualDirectory = $rootApplication.VirtualDirectories['/']
        $gatewayVirtualDirectory = $gatewayApplication.VirtualDirectories['/']
        if ($null -eq $rootVirtualDirectory -or $null -eq $gatewayVirtualDirectory) {
            throw 'Required Portal root or gateway virtual directory is missing.'
        }
        $httpBindings = @($site.Bindings | Where-Object Protocol -EQ 'http')
        if ($httpBindings.Count -ne 1 -or $httpBindings[0].BindingInformation -ne "*:${port}:") {
            throw "Portal must retain exactly one HTTP binding '*:${port}:'."
        }
        $configuration = $manager.GetApplicationHostConfiguration()
        $rootAnonymous = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/anonymousAuthentication', $siteName).GetAttributeValue('enabled')
        $rootWindows = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/windowsAuthentication', $siteName).GetAttributeValue('enabled')
        if ($rootAnonymous -or -not $rootWindows) {
            throw 'Portal root authentication must remain Anonymous=False and Windows=True.'
        }
        $gatewayLocation = "$siteName$gatewayPath"
        $gatewayAnonymous = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/anonymousAuthentication', $gatewayLocation).GetAttributeValue('enabled')
        $gatewayWindows = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/windowsAuthentication', $gatewayLocation).GetAttributeValue('enabled')
        if ($gatewayAnonymous -or -not $gatewayWindows) {
            throw 'Portal gateway authentication must remain Anonymous=False and Windows=True.'
        }
        $allApplicationPaths = @(
            foreach ($iisSite in $manager.Sites) {
                foreach ($application in $iisSite.Applications) {
                    foreach ($virtualDirectory in $application.VirtualDirectories) {
                        if (-not [string]::IsNullOrWhiteSpace([string]$virtualDirectory.PhysicalPath)) {
                            Get-FullPath -Path $virtualDirectory.PhysicalPath
                        }
                    }
                }
            }
        )
        return [pscustomobject]@{
            PortalPath = Get-FullPath -Path $rootVirtualDirectory.PhysicalPath
            GatewayPath = Get-FullPath -Path $gatewayVirtualDirectory.PhysicalPath
            AllApplicationPaths = @($allApplicationPaths | Sort-Object -Unique)
        }
    }
    finally { $manager.Dispose() }
}

function Set-PortalRootPhysicalPath {
    param([Parameter(Mandatory = $true)][string]$PortalPath)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$siteName]
        if ($null -eq $site) { throw "Portal site '$siteName' disappeared during deployment." }
        $rootApplication = $site.Applications['/']
        if ($null -eq $rootApplication) { throw 'Portal root application disappeared during deployment.' }
        $rootApplication.VirtualDirectories['/'].PhysicalPath = $PortalPath
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}

function Assert-PortalPhysicalPaths {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedPortalPath,
        [Parameter(Mandatory = $true)][string]$ExpectedGatewayPath
    )
    $actual = Get-PortalIisBoundary
    if ($actual.PortalPath -ine (Get-FullPath -Path $ExpectedPortalPath)) {
        throw "Portal root path is '$($actual.PortalPath)', expected '$ExpectedPortalPath'."
    }
    if ($actual.GatewayPath -ine (Get-FullPath -Path $ExpectedGatewayPath)) {
        throw "Portal gateway path changed to '$($actual.GatewayPath)', expected '$ExpectedGatewayPath'."
    }
}

function Wait-PortalAndGatewayHealth {
    param([Parameter(Mandatory = $true)][int]$TimeoutSeconds)
    Wait-EndpointHealth -Label 'Portal root' -Uri "http://localhost:$port/api/health" -TimeoutSeconds $TimeoutSeconds
    Wait-EndpointHealth -Label 'Portal Project Tracker gateway' `
        -Uri "http://localhost:$port$gatewayPath/api/health" -TimeoutSeconds $TimeoutSeconds
}

function Invoke-PortalIisSwitch {
    param(
        [Parameter(Mandatory = $true)][string]$CurrentPortalPath,
        [Parameter(Mandatory = $true)][string]$CurrentGatewayPath,
        [Parameter(Mandatory = $true)][string]$CandidatePath
    )
    $switchAttempted = $false
    try {
        Request-PortalPoolState -State Stopped
        Assert-NonTargetRuntimeStarted
        Wait-EndpointHealth -Label 'Portal Project Tracker gateway during root cutover' `
            -Uri "http://localhost:$port$gatewayPath/api/health" -TimeoutSeconds 30
        $switchAttempted = $true
        Set-PortalRootPhysicalPath -PortalPath $CandidatePath
        Assert-PortalPhysicalPaths -ExpectedPortalPath $CandidatePath -ExpectedGatewayPath $CurrentGatewayPath
        Request-PortalPoolState -State Started
        Assert-NonTargetRuntimeStarted
        Wait-PortalAndGatewayHealth -TimeoutSeconds $HealthTimeoutSeconds
        Assert-PortalPhysicalPaths -ExpectedPortalPath $CandidatePath -ExpectedGatewayPath $CurrentGatewayPath
    }
    catch {
        $deploymentFailure = $_.Exception.Message
        $rollbackErrors = New-Object System.Collections.Generic.List[string]
        try { Request-PortalPoolState -State Stopped } catch { $rollbackErrors.Add("Stop root pool: $($_.Exception.Message)") }
        if ($switchAttempted) {
            try { Set-PortalRootPhysicalPath -PortalPath $CurrentPortalPath }
            catch { $rollbackErrors.Add("Root path: $($_.Exception.Message)") }
        }
        try {
            Assert-PortalPhysicalPaths -ExpectedPortalPath $CurrentPortalPath -ExpectedGatewayPath $CurrentGatewayPath
        }
        catch { $rollbackErrors.Add("Path verification: $($_.Exception.Message)") }
        try {
            Request-PortalPoolState -State Started
            Assert-NonTargetRuntimeStarted
            Wait-PortalAndGatewayHealth -TimeoutSeconds $HealthTimeoutSeconds
        }
        catch { $rollbackErrors.Add("Start/health: $($_.Exception.Message)") }
        try {
            Assert-PortalPhysicalPaths -ExpectedPortalPath $CurrentPortalPath -ExpectedGatewayPath $CurrentGatewayPath
        }
        catch { $rollbackErrors.Add("Final path verification: $($_.Exception.Message)") }
        if ($rollbackErrors.Count -eq 0) {
            throw "Portal release failed and the prior Portal root path was restored healthy. The failed candidate was retained at '$CandidatePath'. $deploymentFailure"
        }
        throw "Portal release failed. Rollback also reported: $($rollbackErrors -join ' | '). The failed candidate was retained at '$CandidatePath'. Original failure: $deploymentFailure"
    }
}

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
}
Assert-DeploymentIdentity
if ($ReleaseId -in @('.', '..')) { throw 'ReleaseId cannot be a relative-path marker.' }
$packagePath = Get-FullPath -Path $PackageRoot
$sourcePath = Join-Path $packagePath 'Portal'
$releaseRootPath = Get-FullPath -Path $ReleaseRoot
if ($releaseRootPath -eq [IO.Path]::GetPathRoot($releaseRootPath)) { throw 'ReleaseRoot cannot be a drive root.' }
if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) { throw "Portal package folder is missing: $sourcePath" }
$releasePath = Get-FullPath -Path (Join-Path $releaseRootPath $ReleaseId)
if (-not $releasePath.StartsWith($releaseRootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The resolved release destination escaped ReleaseRoot.'
}
if (Test-Path -LiteralPath $releasePath) { throw "Release destination already exists and will not be overwritten: $releasePath" }
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

$boundary = Get-PortalIisBoundary
$currentPortalPath = $boundary.PortalPath
$currentGatewayPath = $boundary.GatewayPath
foreach ($activeApplicationPath in $boundary.AllApplicationPaths) {
    if (Test-PathContainmentOverlap -FirstPath $releasePath -SecondPath $activeApplicationPath) {
        throw "Portal release destination '$releasePath' overlaps active IIS path '$activeApplicationPath'."
    }
}
foreach ($currentPath in @($currentPortalPath, $currentGatewayPath)) {
    if (-not (Test-Path -LiteralPath $currentPath -PathType Container)) { throw "Current IIS path does not exist: $currentPath" }
}
$currentProductionSettings = Join-Path $currentPortalPath 'appsettings.Production.json'
if (-not (Test-Path -LiteralPath $currentProductionSettings -PathType Leaf)) {
    throw "Portal production settings are missing: $currentProductionSettings"
}
Assert-JsonFile -Path $currentProductionSettings
Assert-NonTargetRuntimeStarted
if ((Get-PoolStateValue -Name $poolName) -ne 'Started') { throw "Portal root pool '$poolName' is not started. No changes were made." }
Wait-PortalAndGatewayHealth -TimeoutSeconds 30

if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName Portal release '$releasePath'",
        'Create an immutable Portal root release, switch only its root path, and verify root plus unchanged gateway health with rollback')) {
    Write-Output 'WHATIF_READY_PORTAL_RELEASE'
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
        throw 'Copied Portal production settings hash mismatch.'
    }
    Assert-ValidWebConfig -Path (Join-Path $releasePath 'web.config') -ExpectedMainDll $mainDll
    Assert-JsonFile -Path $candidateProductionSettings
    if (-not (Test-Path -LiteralPath (Join-Path $releasePath $mainDll) -PathType Leaf)) {
        throw "Candidate application DLL is missing: $mainDll"
    }
    $developmentSettings = @(Get-ChildItem -LiteralPath $releasePath -File -Recurse -Force |
        Where-Object Name -Like 'appsettings.Development*.json')
    if ($developmentSettings.Count -gt 0) { throw 'Development configuration was found in the candidate release.' }
    & icacls.exe $releasePath /grant "IIS AppPool\$poolName`:(OI)(CI)RX" /t /c | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Read/execute permission assignment failed for '$poolName'." }
}
catch {
    throw "Portal release preparation failed before IIS was changed. The incomplete candidate was retained at '$releasePath'. $($_.Exception.Message)"
}

$cutoverBoundary = Get-PortalIisBoundary
if ($cutoverBoundary.PortalPath -ine $currentPortalPath -or $cutoverBoundary.GatewayPath -ine $currentGatewayPath) {
    throw "Portal or gateway IIS paths changed during candidate preparation. No IIS state was changed; the candidate was retained at '$releasePath'."
}
Assert-NonTargetRuntimeStarted
if ((Get-PoolStateValue -Name $poolName) -ne 'Started') {
    throw "Portal root pool changed state during candidate preparation. The candidate was retained at '$releasePath'."
}
Wait-PortalAndGatewayHealth -TimeoutSeconds 30

Invoke-PortalIisSwitch -CurrentPortalPath $currentPortalPath `
    -CurrentGatewayPath $currentGatewayPath -CandidatePath $releasePath

[pscustomobject]@{
    Status = 'PORTAL_RELEASE_DEPLOYED_AND_HEALTHY'
    ReleaseId = $ReleaseId
    ReleasePath = $releasePath
} | Format-List
Write-Output 'PORTAL_RELEASE_DEPLOYED_AND_HEALTHY'
