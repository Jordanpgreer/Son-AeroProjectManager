<#
    Deploys one immutable Quality Assurance release on SON-IIS2 without changing any other site.

    Use -FirstActivation only when the current Quality application cannot become healthy before the
    corrected SQL Server migration chain is installed. Normal updates require the current endpoint
    to be healthy. The active Production settings are preserved byte-for-byte, the candidate must
    become healthy, and a failed cutover restores the exact prior IIS path and pool state.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$ReleaseId,

    [string]$ReleaseRoot = 'C:\SonAero\releases\quality-assurance',

    [ValidateSet('SON-IIS2')]
    [string]$ExpectedComputerName = 'SON-IIS2',

    [switch]$FirstActivation,

    [ValidateRange(30, 600)]
    [int]$HealthTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$siteName = 'QualityAssurance'
$poolName = 'QualityAssurance'
$packageFolder = 'QualityAssurance'
$mainDll = 'QualityAssurance.Api.dll'
$healthUri = 'https://quality.hub.son4l.local/api/health'
$httpPort = 5170

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

function Read-QualityProductionConfiguration {
    param([Parameter(Mandatory = $true)][string]$Path)
    try {
        $content = Get-Content -LiteralPath $Path -Raw
        if ([string]::IsNullOrWhiteSpace($content)) { throw 'The file is empty.' }
        $configuration = $content | ConvertFrom-Json -ErrorAction Stop
    }
    catch { throw "Invalid Quality Production JSON at '$Path': $($_.Exception.Message)" }

    if ([string]$configuration.Authentication.Mode -cne 'Windows') {
        throw "Quality Production configuration must use Windows authentication: $Path"
    }
    if ([string]$configuration.Database.Provider -cne 'SqlServer' -or
        [string]$configuration.QualityDatabase.Provider -cne 'SqlServer') {
        throw "Quality Production configuration must use SQL Server for both database providers: $Path"
    }
    $qualityStore = [string]$configuration.ConnectionStrings.QualityStore
    if ([string]::IsNullOrWhiteSpace($qualityStore) -or
        $qualityStore -notmatch '(?i)(?:^|;)\s*(?:Database|Initial Catalog)\s*=\s*QualityAssurance\s*(?:;|$)') {
        throw "Quality Production configuration must target the QualityAssurance database: $Path"
    }
    return $configuration
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
        return [pscustomobject]@{
            Healthy = ([int]$response.StatusCode -eq 200)
            Detail = "HTTP $($response.StatusCode)"
        }
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

function Wait-QualityHealth {
    param([Parameter(Mandatory = $true)][int]$TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastResult = $null
    do {
        $lastResult = Get-HealthResult -Uri $healthUri
        if ($lastResult.Healthy) { return }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Quality health verification timed out at '$healthUri'. Last result: $($lastResult.Detail)"
}

function Get-PoolStateValue {
    return (Get-WebAppPoolState -Name $poolName).Value
}

function Assert-QualityActivationState {
    param(
        [Parameter(Mandatory = $true)][string]$PoolState,
        [Parameter(Mandatory = $true)][bool]$Healthy,
        [Parameter(Mandatory = $true)][bool]$FirstActivationRequested,
        [Parameter(Mandatory = $true)][string]$Phase
    )
    if ($PoolState -cne 'Started') {
        throw "Quality pool must already be Started during $Phase; found '$PoolState'."
    }
    if ($FirstActivationRequested -and $Healthy) {
        throw "Quality is already healthy during $Phase; omit -FirstActivation for a normal update."
    }
    if (-not $FirstActivationRequested -and -not $Healthy) {
        throw "Quality is not healthy during $Phase. Use -FirstActivation only for the reviewed first SQL Server activation."
    }
}

function Request-QualityPoolState {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Started', 'Stopped')][string]$State,
        [int]$TimeoutSeconds = 120
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $current = Get-PoolStateValue
        if ($current -eq $State) { return }
        if ($State -eq 'Stopped' -and $current -eq 'Started') {
            try { Stop-WebAppPool -Name $poolName } catch { Start-Sleep -Milliseconds 500 }
        }
        elseif ($State -eq 'Started' -and $current -eq 'Stopped') {
            try { Start-WebAppPool -Name $poolName } catch { Start-Sleep -Milliseconds 500 }
        }
        else { Start-Sleep -Milliseconds 500 }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Quality pool '$poolName' did not become '$State'; its current state is '$current'."
}

function Get-QualityIisBoundary {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$siteName]
        $pool = $manager.ApplicationPools[$poolName]
        if ($null -eq $site -or $null -eq $pool) { throw 'Required Quality IIS site or pool is missing.' }
        $application = $site.Applications['/']
        if ($null -eq $application -or $application.ApplicationPoolName -ine $poolName) {
            throw "Quality root application must use pool '$poolName'."
        }
        $virtualDirectory = $application.VirtualDirectories['/']
        if ($null -eq $virtualDirectory) { throw 'Quality root virtual directory is missing.' }
        $httpBindings = @($site.Bindings | Where-Object Protocol -EQ 'http')
        if ($httpBindings.Count -ne 1 -or $httpBindings[0].BindingInformation -ne "*:${httpPort}:") {
            throw "Quality must retain exactly one HTTP binding '*:${httpPort}:'."
        }
        $configuration = $manager.GetApplicationHostConfiguration()
        $anonymousEnabled = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/anonymousAuthentication', $siteName).GetAttributeValue('enabled')
        $windowsEnabled = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/windowsAuthentication', $siteName).GetAttributeValue('enabled')
        if ($anonymousEnabled -or -not $windowsEnabled) {
            throw 'Quality authentication must remain Anonymous=False and Windows=True.'
        }
        $allApplicationPaths = @(
            foreach ($iisSite in $manager.Sites) {
                foreach ($iisApplication in $iisSite.Applications) {
                    foreach ($iisVirtualDirectory in $iisApplication.VirtualDirectories) {
                        if (-not [string]::IsNullOrWhiteSpace([string]$iisVirtualDirectory.PhysicalPath)) {
                            Get-FullPath -Path $iisVirtualDirectory.PhysicalPath
                        }
                    }
                }
            }
        )
        return [pscustomobject]@{
            QualityPath = Get-FullPath -Path $virtualDirectory.PhysicalPath
            AllApplicationPaths = @($allApplicationPaths | Sort-Object -Unique)
        }
    }
    finally { $manager.Dispose() }
}

function Set-QualityPhysicalPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$siteName]
        if ($null -eq $site) { throw "Quality site '$siteName' disappeared during deployment." }
        $site.Applications['/'].VirtualDirectories['/'].PhysicalPath = $Path
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}

function Assert-QualityPhysicalPath {
    param([Parameter(Mandatory = $true)][string]$ExpectedPath)
    $actualPath = (Get-QualityIisBoundary).QualityPath
    if ($actualPath -ine (Get-FullPath -Path $ExpectedPath)) {
        throw "Quality IIS path is '$actualPath', expected '$ExpectedPath'."
    }
}

function Restore-PriorQualityRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$PriorPath,
        [Parameter(Mandatory = $true)][ValidateSet('Started', 'Stopped')][string]$PriorPoolState,
        [Parameter(Mandatory = $true)][bool]$PriorWasHealthy
    )
    Request-QualityPoolState -State Stopped
    Set-QualityPhysicalPath -Path $PriorPath
    Assert-QualityPhysicalPath -ExpectedPath $PriorPath
    Request-QualityPoolState -State $PriorPoolState
    if ($PriorPoolState -eq 'Started' -and $PriorWasHealthy) {
        Wait-QualityHealth -TimeoutSeconds $HealthTimeoutSeconds
    }
    Assert-QualityPhysicalPath -ExpectedPath $PriorPath
}

function Invoke-QualityIisSwitch {
    param(
        [Parameter(Mandatory = $true)][string]$CurrentPath,
        [Parameter(Mandatory = $true)][string]$CandidatePath,
        [Parameter(Mandatory = $true)][ValidateSet('Started', 'Stopped')][string]$PriorPoolState,
        [Parameter(Mandatory = $true)][bool]$PriorWasHealthy
    )
    try {
        Request-QualityPoolState -State Stopped
        Set-QualityPhysicalPath -Path $CandidatePath
        Assert-QualityPhysicalPath -ExpectedPath $CandidatePath
        Request-QualityPoolState -State Started
        Wait-QualityHealth -TimeoutSeconds $HealthTimeoutSeconds
        Assert-QualityPhysicalPath -ExpectedPath $CandidatePath
    }
    catch {
        $deploymentFailure = $_.Exception.Message
        $rollbackErrors = New-Object System.Collections.Generic.List[string]
        try {
            Restore-PriorQualityRuntime -PriorPath $CurrentPath -PriorPoolState $PriorPoolState `
                -PriorWasHealthy $PriorWasHealthy
        }
        catch { $rollbackErrors.Add($_.Exception.Message) }
        if ($rollbackErrors.Count -eq 0) {
            throw "Quality release failed and the exact prior IIS path and pool state were restored. The failed candidate was retained at '$CandidatePath'. $deploymentFailure"
        }
        throw "Quality release failed. Rollback also reported: $($rollbackErrors -join ' | '). The failed candidate was retained at '$CandidatePath'. Original failure: $deploymentFailure"
    }
}

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
}
Assert-DeploymentIdentity
if ($ReleaseId -in @('.', '..')) { throw 'ReleaseId cannot be a relative-path marker.' }
$packagePath = Get-FullPath -Path $PackageRoot
$sourcePath = Join-Path $packagePath $packageFolder
$releaseRootPath = Get-FullPath -Path $ReleaseRoot
if ($releaseRootPath -eq [IO.Path]::GetPathRoot($releaseRootPath)) { throw 'ReleaseRoot cannot be a drive root.' }
if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
    throw "Quality package folder is missing: $sourcePath"
}
$releasePath = Get-FullPath -Path (Join-Path $releaseRootPath $ReleaseId)
if (-not $releasePath.StartsWith($releaseRootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The resolved release destination escaped ReleaseRoot.'
}
if (Test-Path -LiteralPath $releasePath) {
    throw "Release destination already exists and will not be overwritten: $releasePath"
}
if (Test-PathContainmentOverlap -FirstPath $sourcePath -SecondPath $releasePath) {
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
Assert-ValidWebConfig -Path (Join-Path $sourcePath 'web.config') -ExpectedMainDll $mainDll
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath $mainDll) -PathType Leaf)) {
    throw "Package DLL is missing: $mainDll"
}

$priorWhatIfPreference = $WhatIfPreference
try { $WhatIfPreference = $false; Import-Module WebAdministration -ErrorAction Stop }
finally { $WhatIfPreference = $priorWhatIfPreference }
if (-not ('Microsoft.Web.Administration.ServerManager' -as [type])) {
    $assemblyPath = Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) { throw "IIS assembly not found: $assemblyPath" }
    Add-Type -Path $assemblyPath -ErrorAction Stop
}
if ((Get-WebsiteState -Name $siteName).Value -ne 'Started') {
    throw "Quality IIS site '$siteName' must be started. No changes were made."
}
$boundary = Get-QualityIisBoundary
$currentPath = $boundary.QualityPath
foreach ($activePath in $boundary.AllApplicationPaths) {
    if (Test-PathContainmentOverlap -FirstPath $releasePath -SecondPath $activePath) {
        throw "Quality release destination '$releasePath' overlaps active IIS path '$activePath'."
    }
}
if (-not (Test-Path -LiteralPath $currentPath -PathType Container)) {
    throw "Current Quality IIS path does not exist: $currentPath"
}
$currentProductionSettings = Join-Path $currentPath 'appsettings.Production.json'
if (-not (Test-Path -LiteralPath $currentProductionSettings -PathType Leaf)) {
    throw "Quality Production settings are missing: $currentProductionSettings"
}
[void](Read-QualityProductionConfiguration -Path $currentProductionSettings)
$priorPoolState = Get-PoolStateValue
$currentHealth = Get-HealthResult -Uri $healthUri
$currentHealthy = [bool]$currentHealth.Healthy
Assert-QualityActivationState -PoolState $priorPoolState -Healthy $currentHealthy `
    -FirstActivationRequested ([bool]$FirstActivation) -Phase 'preflight'

if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName Quality release '$releasePath'",
        'Create an immutable Quality release, switch only its IIS path, and verify candidate health with exact path/state rollback')) {
    Write-Output 'WHATIF_READY_QUALITY_ASSURANCE_RELEASE'
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
        throw 'Copied Quality Production settings hash mismatch.'
    }
    [void](Read-QualityProductionConfiguration -Path $candidateProductionSettings)
    Assert-ValidWebConfig -Path (Join-Path $releasePath 'web.config') -ExpectedMainDll $mainDll
    if (-not (Test-Path -LiteralPath (Join-Path $releasePath $mainDll) -PathType Leaf)) {
        throw "Candidate application DLL is missing: $mainDll"
    }
    $developmentSettings = @(Get-ChildItem -LiteralPath $releasePath -File -Recurse -Force |
        Where-Object Name -Like 'appsettings.Development*.json')
    if ($developmentSettings.Count -gt 0) {
        throw 'Development configuration was found in the Quality candidate release.'
    }
    & icacls.exe $releasePath /grant "IIS AppPool\$poolName`:(OI)(CI)RX" /t /c | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Read/execute permission assignment failed for '$poolName'." }
}
catch {
    throw "Quality release preparation failed before IIS was changed. The incomplete candidate was retained at '$releasePath'. $($_.Exception.Message)"
}

$cutoverBoundary = Get-QualityIisBoundary
if ($cutoverBoundary.QualityPath -ine $currentPath) {
    throw "Quality IIS path changed during candidate preparation. No IIS state was changed; the candidate was retained at '$releasePath'."
}
if ((Get-PoolStateValue) -ne $priorPoolState) {
    throw "Quality pool state changed during candidate preparation. The candidate was retained at '$releasePath'."
}
if ((Get-WebsiteState -Name $siteName).Value -ne 'Started') {
    throw "Quality IIS site stopped during candidate preparation. The candidate was retained at '$releasePath'."
}
$cutoverHealth = Get-HealthResult -Uri $healthUri
Assert-QualityActivationState -PoolState (Get-PoolStateValue) -Healthy ([bool]$cutoverHealth.Healthy) `
    -FirstActivationRequested ([bool]$FirstActivation) -Phase 'cutover preflight'

Invoke-QualityIisSwitch -CurrentPath $currentPath -CandidatePath $releasePath `
    -PriorPoolState $priorPoolState -PriorWasHealthy $currentHealthy

[pscustomobject]@{
    Status = 'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY'
    ReleaseId = $ReleaseId
    ReleasePath = $releasePath
    FirstActivation = [bool]$FirstActivation
} | Format-List
Write-Output 'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY'
