<#
    Atomic production configuration transaction for HTTPS module URLs and dual Hub CORS.

    Preview:
      .\Configure-HubHttpsApplicationConfig.ps1 -WhatIf
    Apply:
      .\Configure-HubHttpsApplicationConfig.ps1 -Confirm:$false
    Roll back the last successful apply:
      .\Configure-HubHttpsApplicationConfig.ps1 -Rollback -Confirm:$false

    The active IIS production files are backed up with restricted ACLs. Both files are restored
    automatically if replacement, targeted pool restart, CORS, dual-scheme, or gateway health fails.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'Apply')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Rollback')]
    [switch]$Rollback,

    [ValidateRange(30, 600)]
    [int]$HealthTimeoutSeconds = 180,

    [string]$StatePath = 'C:\ProgramData\SonAero\deployment-state\https-application-config.json'
)

$ErrorActionPreference = 'Stop'
$expectedComputerName = 'SON-IIS2'
$portalSiteName = 'SonAeroPortal'
$trackerSiteName = 'ProjectTracker'
$gatewayPath = '/project-tracker-api'
$poolNames = @('ProjectTracker', 'SonAeroPortal', 'ProjectTrackerAdminGateway')
$applications = @(
    [pscustomobject]@{ Id = 'project-tracker'; Site = 'ProjectTracker'; HttpPort = 5135; HttpsPort = 6135 },
    [pscustomobject]@{ Id = 'portal'; Site = 'SonAeroPortal'; HttpPort = 5140; HttpsPort = 6140 },
    [pscustomobject]@{ Id = 'engineering-hub'; Site = 'EngineeringHub'; HttpPort = 5150; HttpsPort = 6150 },
    [pscustomobject]@{ Id = 'estimating-dashboard'; Site = 'EstimatingDashboard'; HttpPort = 5160; HttpsPort = 6160 }
)
$moduleUrls = @{
    'project-tracker' = 'https://SON-IIS2:6135'
    'engineering-hub' = 'https://SON-IIS2:6150'
    'estimating-dashboard' = 'https://SON-IIS2:6160'
}
$hubOrigins = @('https://SON-IIS2:6140', 'http://SON-IIS2:5140')

function Assert-Host {
    if ($env:COMPUTERNAME -ine $expectedComputerName) {
        throw "This transaction is restricted to $expectedComputerName; current computer is $env:COMPUTERNAME."
    }
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated Windows PowerShell session.'
    }
}

function Import-IisAdministration {
    $priorWhatIf = $WhatIfPreference
    try {
        $WhatIfPreference = $false
        Import-Module WebAdministration -Global -ErrorAction Stop
    }
    finally { $WhatIfPreference = $priorWhatIf }
    $assemblyPath = Join-Path $env:WINDIR 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "IIS administration assembly was not found at '$assemblyPath'."
    }
    Add-Type -Path $assemblyPath -ErrorAction Stop
}

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path)).TrimEnd('\')
}

function Get-ActiveSitePath {
    param([Parameter(Mandatory = $true)][string]$SiteName)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$SiteName]
        if (-not $site) { throw "Required IIS site '$SiteName' is missing." }
        $application = $site.Applications['/']
        $virtualDirectory = if ($application) { $application.VirtualDirectories['/'] } else { $null }
        if (-not $virtualDirectory) { throw "IIS site '$SiteName' has no root virtual directory." }
        $path = Get-FullPath $virtualDirectory.PhysicalPath
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Active IIS path does not exist: '$path'." }
        return $path
    }
    finally { $manager.Dispose() }
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required production configuration is missing: '$Path'." }
    try {
        $raw = Get-Content -LiteralPath $Path -Raw
        if ([string]::IsNullOrWhiteSpace($raw)) { throw 'The file is empty.' }
        return $raw | ConvertFrom-Json
    }
    catch { throw "Invalid JSON at '$Path': $($_.Exception.Message)" }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($Path)))).Replace('-', '') }
    finally { $hasher.Dispose() }
}

function Convert-ToUtf8JsonBytes {
    param([Parameter(Mandatory = $true)]$Value)
    $json = $Value | ConvertTo-Json -Depth 100
    return (New-Object Text.UTF8Encoding($false)).GetBytes($json + [Environment]::NewLine)
}

function Assert-PortalShape {
    param([Parameter(Mandatory = $true)]$Config)
    if (-not $Config.Portal -or -not $Config.Portal.Applications) { throw 'Portal configuration has no Portal.Applications catalog.' }
    foreach ($id in $moduleUrls.Keys) {
        $matches = @($Config.Portal.Applications | Where-Object { $_.Id -eq $id })
        if ($matches.Count -ne 1) { throw "Portal.Applications must contain exactly one '$id' entry; found $($matches.Count)." }
        if ($matches[0].PSObject.Properties.Name -notcontains 'Url') { throw "Portal application '$id' has no Url property." }
    }
}

function Assert-TrackerShape {
    param([Parameter(Mandatory = $true)]$Config)
    if (-not $Config.Cors -or $Config.Cors.PSObject.Properties.Name -notcontains 'HubOrigins') {
        throw 'Project Tracker configuration has no Cors.HubOrigins property.'
    }
}

function New-TransformedConfig {
    param([Parameter(Mandatory = $true)]$PortalConfig, [Parameter(Mandatory = $true)]$TrackerConfig)
    Assert-PortalShape $PortalConfig
    Assert-TrackerShape $TrackerConfig
    foreach ($id in $moduleUrls.Keys) {
        $entry = @($PortalConfig.Portal.Applications | Where-Object { $_.Id -eq $id })[0]
        $entry.Url = $moduleUrls[$id]
    }
    $TrackerConfig.Cors.HubOrigins = @($hubOrigins)
    Assert-PortalShape $PortalConfig
    Assert-TrackerShape $TrackerConfig
    foreach ($id in $moduleUrls.Keys) {
        $entry = @($PortalConfig.Portal.Applications | Where-Object { $_.Id -eq $id })[0]
        if ($entry.Url -ne $moduleUrls[$id]) { throw "Portal URL transform failed for '$id'." }
    }
    if ((@($TrackerConfig.Cors.HubOrigins) -join '|') -ne ($hubOrigins -join '|')) {
        throw 'Project Tracker dual CORS transform failed or HTTPS is not first.'
    }
    return [pscustomobject]@{ Portal = $PortalConfig; Tracker = $TrackerConfig }
}

function New-SecureDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Force -Path $Path | Out-Null }
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($identity in @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $identity, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow
        )
        $security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $security
}

function Write-SecureState {
    param([Parameter(Mandatory = $true)]$State)
    $directory = Split-Path -Parent $StatePath
    New-SecureDirectory $directory
    $temporary = "$StatePath.tmp"
    $State | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $StatePath -Force
}

function Assert-PoolsPresentAndStarted {
    foreach ($poolName in $poolNames) {
        if (-not (Test-Path -LiteralPath "IIS:\AppPools\$poolName")) { throw "Required IIS application pool '$poolName' is missing." }
        $state = (Get-WebAppPoolState -Name $poolName).Value
        if ($state -ne 'Started') { throw "IIS application pool '$poolName' must be Started before this transaction; current state is '$state'." }
    }
}

function Wait-PoolState {
    param([ValidateSet('Started', 'Stopped')][string]$State)
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        $pending = @($poolNames | Where-Object { (Get-WebAppPoolState -Name $_).Value -ne $State })
        if ($pending.Count -eq 0) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Application pools did not reach '$State': $($pending -join ', ')."
}

function Stop-TargetPools {
    foreach ($poolName in $poolNames) {
        if ((Get-WebAppPoolState -Name $poolName).Value -ne 'Stopped') { Stop-WebAppPool -Name $poolName }
    }
    Wait-PoolState Stopped
}

function Start-TargetPools {
    foreach ($poolName in $poolNames) {
        if ((Get-WebAppPoolState -Name $poolName).Value -ne 'Started') { Start-WebAppPool -Name $poolName }
    }
    Wait-PoolState Started
}

function Wait-UriHealth {
    param([Parameter(Mandatory = $true)][string[]]$Uris)
    $pending = @($Uris)
    $deadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
    do {
        foreach ($uri in @($pending)) {
            try {
                $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec 10
                if ($response.StatusCode -eq 200) { $pending = @($pending | Where-Object { $_ -ne $uri }) }
            }
            catch { }
        }
        if ($pending.Count -gt 0) { Start-Sleep -Milliseconds 750 }
    } while ($pending.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($pending.Count -gt 0) { throw "Health verification timed out for: $($pending -join ', ')." }
}

function Get-DualSchemeHealthUris {
    $uris = @()
    foreach ($application in $applications) {
        $uris += "http://$expectedComputerName`:$($application.HttpPort)/api/health"
        $uris += "https://$expectedComputerName`:$($application.HttpsPort)/api/health"
    }
    $uris += "http://$expectedComputerName`:5140$gatewayPath/api/health"
    $uris += "https://$expectedComputerName`:6140$gatewayPath/api/health"
    return $uris
}

function Get-HttpHealthUris {
    $uris = @($applications | ForEach-Object { "http://$expectedComputerName`:$($_.HttpPort)/api/health" })
    $uris += "http://$expectedComputerName`:5140$gatewayPath/api/health"
    return $uris
}

function Assert-CorsResponse {
    param([Parameter(Mandatory = $true)][string]$Uri, [Parameter(Mandatory = $true)][string]$Origin)
    try {
        $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Method Get -Uri $Uri -TimeoutSec 10 -Headers @{ Origin = $Origin }
    }
    catch { throw "CORS preflight failed for origin '$Origin' at '$Uri': $($_.Exception.Message)" }
    $allowedOrigin = [string]$response.Headers['Access-Control-Allow-Origin']
    $allowedCredentials = [string]$response.Headers['Access-Control-Allow-Credentials']
    if ($allowedOrigin -ne $Origin -or $allowedCredentials -ine 'true') {
        throw "CORS preflight did not allow credentials for exact origin '$Origin' at '$Uri'."
    }
}

function Assert-DualCors {
    Assert-CorsResponse -Origin 'https://SON-IIS2:6140' -Uri 'https://SON-IIS2:6135/api/me'
    Assert-CorsResponse -Origin 'http://SON-IIS2:5140' -Uri 'http://SON-IIS2:5135/api/me'
}

function Restore-Configuration {
    param([Parameter(Mandatory = $true)]$State, [switch]$VerifyDualScheme)
    Stop-TargetPools
    try {
        Copy-Item -LiteralPath $State.PortalBackupPath -Destination $State.PortalConfigPath -Force
        Copy-Item -LiteralPath $State.TrackerBackupPath -Destination $State.TrackerConfigPath -Force
        if ((Get-FileSha256 $State.PortalConfigPath) -ne $State.PortalOriginalSha256 -or
            (Get-FileSha256 $State.TrackerConfigPath) -ne $State.TrackerOriginalSha256) {
            throw 'Restored production configuration hashes do not match the saved originals.'
        }
    }
    finally { Start-TargetPools }
    if ($VerifyDualScheme) { Wait-UriHealth (Get-DualSchemeHealthUris) }
    else { Wait-UriHealth (Get-HttpHealthUris) }
}

if (-not [IO.Path]::IsPathRooted($StatePath)) { throw 'StatePath must be an absolute local path.' }
$StatePath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($StatePath))
Assert-Host
if (-not $WhatIfPreference) { Assert-Administrator }
Import-IisAdministration

if ($Rollback) {
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { throw "Rollback state was not found at '$StatePath'." }
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if ($state.ComputerName -ine $expectedComputerName -or $state.Version -ne 1) { throw 'Rollback state does not match this transaction.' }
    if ($state.Status -ne 'Applied') { throw "Rollback state is '$($state.Status)', not Applied." }
    if ((Get-FileSha256 $state.PortalConfigPath) -ne $state.PortalAppliedSha256 -or
        (Get-FileSha256 $state.TrackerConfigPath) -ne $state.TrackerAppliedSha256) {
        throw 'Active production configuration has drifted since apply; rollback refused.'
    }
    Assert-PoolsPresentAndStarted
    if ($PSCmdlet.ShouldProcess($expectedComputerName, 'Restore original Portal and Project Tracker production configuration')) {
        Restore-Configuration -State $state -VerifyDualScheme
        $state.Status = 'RolledBack'
        $state.RolledBackAtUtc = [DateTime]::UtcNow.ToString('o')
        Write-SecureState $state
        Write-Output 'HTTPS_APPLICATION_CONFIG_ROLLED_BACK_AND_DUAL_SCHEME_HEALTHY'
    }
    elseif ($WhatIfPreference) { Write-Output 'WHATIF_READY_ROLLBACK: production config backup, hashes, pools, and drift checks passed; nothing was changed.' }
    else { Write-Output 'HTTPS_APPLICATION_CONFIG_ROLLBACK_CANCELLED' }
    exit 0
}

Assert-PoolsPresentAndStarted
$portalRoot = Get-ActiveSitePath $portalSiteName
$trackerRoot = Get-ActiveSitePath $trackerSiteName
$portalConfigPath = Join-Path $portalRoot 'appsettings.Production.json'
$trackerConfigPath = Join-Path $trackerRoot 'appsettings.Production.json'
$portalOriginal = Read-JsonFile $portalConfigPath
$trackerOriginal = Read-JsonFile $trackerConfigPath
$transformed = New-TransformedConfig -PortalConfig $portalOriginal -TrackerConfig $trackerOriginal
$portalBytes = Convert-ToUtf8JsonBytes $transformed.Portal
$trackerBytes = Convert-ToUtf8JsonBytes $transformed.Tracker

# HTTPS must already be reachable before any production configuration is changed.
Wait-UriHealth (Get-DualSchemeHealthUris)
if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
    $oldState = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if ($oldState.Status -eq 'Applied') { throw "An applied transaction already exists at '$StatePath'; roll it back before applying another." }
}

if (-not $PSCmdlet.ShouldProcess($expectedComputerName, 'Atomically switch module URLs to HTTPS and configure HTTPS-first dual CORS')) {
    if ($WhatIfPreference) { Write-Output 'WHATIF_READY: active IIS paths, JSON transforms, pools, dual-scheme applications, and gateway passed preflight; nothing was changed.' }
    else { Write-Output 'HTTPS_APPLICATION_CONFIG_CANCELLED' }
    exit 0
}

$backupRoot = Join-Path (Split-Path -Parent $StatePath) ('https-config-backups\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'))
New-SecureDirectory $backupRoot
$portalBackup = Join-Path $backupRoot 'Portal.appsettings.Production.json'
$trackerBackup = Join-Path $backupRoot 'ProjectTracker.appsettings.Production.json'
Copy-Item -LiteralPath $portalConfigPath -Destination $portalBackup
Copy-Item -LiteralPath $trackerConfigPath -Destination $trackerBackup
$state = [pscustomobject]@{
    Version = 1
    ComputerName = $expectedComputerName
    Status = 'Prepared'
    PreparedAtUtc = [DateTime]::UtcNow.ToString('o')
    PortalConfigPath = $portalConfigPath
    TrackerConfigPath = $trackerConfigPath
    PortalBackupPath = $portalBackup
    TrackerBackupPath = $trackerBackup
    PortalOriginalSha256 = Get-FileSha256 $portalConfigPath
    TrackerOriginalSha256 = Get-FileSha256 $trackerConfigPath
    PortalAppliedSha256 = ''
    TrackerAppliedSha256 = ''
}
Write-SecureState $state

$portalTemporary = Join-Path $portalRoot ('.appsettings.Production.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$trackerTemporary = Join-Path $trackerRoot ('.appsettings.Production.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllBytes($portalTemporary, $portalBytes)
    [IO.File]::WriteAllBytes($trackerTemporary, $trackerBytes)
    $null = Read-JsonFile $portalTemporary
    $null = Read-JsonFile $trackerTemporary
    Stop-TargetPools
    try {
        Move-Item -LiteralPath $portalTemporary -Destination $portalConfigPath -Force
        Move-Item -LiteralPath $trackerTemporary -Destination $trackerConfigPath -Force
    }
    finally { Start-TargetPools }
    Wait-UriHealth (Get-DualSchemeHealthUris)
    Assert-DualCors
    $state.PortalAppliedSha256 = Get-FileSha256 $portalConfigPath
    $state.TrackerAppliedSha256 = Get-FileSha256 $trackerConfigPath
    $state.Status = 'Applied'
    $state.AppliedAtUtc = [DateTime]::UtcNow.ToString('o')
    Write-SecureState $state
    Write-Output 'HTTPS_APPLICATION_CONFIG_APPLIED_AND_DUAL_SCHEME_GATEWAY_HEALTHY'
}
catch {
    $failure = $_.Exception.Message
    Remove-Item -LiteralPath $portalTemporary -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $trackerTemporary -Force -ErrorAction SilentlyContinue
    try {
        Restore-Configuration -State $state
        $state.Status = 'AutomaticallyRolledBack'
        $state.RolledBackAtUtc = [DateTime]::UtcNow.ToString('o')
        Write-SecureState $state
    }
    catch { throw "Configuration transaction failed: $failure Automatic rollback also failed: $($_.Exception.Message)" }
    throw "Configuration transaction failed and was rolled back: $failure"
}
finally {
    Remove-Item -LiteralPath $portalTemporary -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $trackerTemporary -Force -ErrorAction SilentlyContinue
}
