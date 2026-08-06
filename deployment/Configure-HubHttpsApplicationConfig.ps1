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
$stateRoot = 'C:\ProgramData\SonAero\deployment-state'
$backupBaseRoot = Join-Path $stateRoot 'https-config-backups'
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

function Get-BytesSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hasher.ComputeHash($Bytes))).Replace('-', '') }
    finally { $hasher.Dispose() }
}

function Set-StateProperty {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$Name,
        $Value
    )
    if ($State.PSObject.Properties.Name -contains $Name) { $State.$Name = $Value }
    else { $State | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}

function Assert-RequiredStateProperties {
    param([Parameter(Mandatory = $true)]$State, [Parameter(Mandatory = $true)][string[]]$Names)
    foreach ($name in $Names) {
        if ($State.PSObject.Properties.Name -notcontains $name -or $null -eq $State.$name) {
            throw "Transaction state is missing required property '$name'."
        }
        if ($State.$name -is [string] -and [string]::IsNullOrWhiteSpace([string]$State.$name)) {
            throw "Transaction state property '$name' is empty."
        }
    }
}

function Assert-SafeStatePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullRoot = Get-FullPath $stateRoot
    $fullPath = Get-FullPath $Path
    if ((Split-Path -Parent $fullPath) -ine $fullRoot -or [IO.Path]::GetExtension($fullPath) -ine '.json') {
        throw "StatePath must be a JSON file directly under '$fullRoot'."
    }
    return $fullPath
}

function Assert-PathUnderRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $fullPath = Get-FullPath $Path
    $fullRoot = Get-FullPath $Root
    if (-not $fullPath.StartsWith($fullRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must remain under '$fullRoot'."
    }
    return $fullPath
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

function Try-WriteSecureState {
    param([Parameter(Mandatory = $true)]$State)
    try {
        Write-SecureState $State
        return $null
    }
    catch { return $_.Exception.Message }
}

function Assert-TransactionState {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$ExpectedPortalConfigPath,
        [Parameter(Mandatory = $true)][string]$ExpectedTrackerConfigPath
    )
    $required = @(
        'Version', 'ComputerName', 'Status', 'PortalConfigPath', 'TrackerConfigPath',
        'PortalBackupPath', 'TrackerBackupPath', 'PortalOriginalSha256', 'TrackerOriginalSha256'
    )
    Assert-RequiredStateProperties -State $State -Names $required
    foreach ($name in @('PortalAppliedSha256', 'TrackerAppliedSha256')) {
        if ($State.PSObject.Properties.Name -notcontains $name -or $null -eq $State.$name) {
            throw "Transaction state is missing required property '$name'."
        }
    }
    $version = [int]$State.Version
    if ($version -notin @(1, 2) -or $State.ComputerName -ine $expectedComputerName) {
        throw 'Transaction state identity or version does not match this script.'
    }
    if ((Get-FullPath $State.PortalConfigPath) -ine (Get-FullPath $ExpectedPortalConfigPath) -or
        (Get-FullPath $State.TrackerConfigPath) -ine (Get-FullPath $ExpectedTrackerConfigPath)) {
        throw 'Transaction state active configuration paths do not match the current IIS sites.'
    }
    $portalBackupPath = Assert-PathUnderRoot -Path $State.PortalBackupPath -Root $backupBaseRoot -Label 'Portal backup path'
    $trackerBackupPath = Assert-PathUnderRoot -Path $State.TrackerBackupPath -Root $backupBaseRoot -Label 'Project Tracker backup path'
    if ((Split-Path -Parent $portalBackupPath) -ine (Split-Path -Parent $trackerBackupPath)) {
        throw 'Transaction backup files do not share the same secured transaction directory.'
    }
    foreach ($name in @('PortalOriginalSha256', 'TrackerOriginalSha256')) {
        if ([string]$State.$name -notmatch '^[A-Fa-f0-9]{64}$') { throw "Transaction state has an invalid $name value." }
    }
    Assert-TransactionBackups $State
    if ($version -eq 1) {
        $portalOriginal = Read-JsonFile $State.PortalBackupPath
        $trackerOriginal = Read-JsonFile $State.TrackerBackupPath
        $transformed = New-TransformedConfig -PortalConfig $portalOriginal -TrackerConfig $trackerOriginal
        $portalPlannedSha256 = Get-BytesSha256 (Convert-ToUtf8JsonBytes $transformed.Portal)
        $trackerPlannedSha256 = Get-BytesSha256 (Convert-ToUtf8JsonBytes $transformed.Tracker)
        foreach ($pair in @(
            [pscustomobject]@{ Name = 'PortalAppliedSha256'; Planned = $portalPlannedSha256 },
            [pscustomobject]@{ Name = 'TrackerAppliedSha256'; Planned = $trackerPlannedSha256 }
        )) {
            $applied = [string]$State.($pair.Name)
            if (-not [string]::IsNullOrWhiteSpace($applied) -and $applied -ne $pair.Planned) {
                throw "Legacy transaction state $($pair.Name) does not match the safely reconstructed transform."
            }
        }
        Set-StateProperty -State $State -Name 'PortalPlannedSha256' -Value $portalPlannedSha256
        Set-StateProperty -State $State -Name 'TrackerPlannedSha256' -Value $trackerPlannedSha256
        Set-StateProperty -State $State -Name 'Version' -Value 2
    }
    else {
        Assert-RequiredStateProperties -State $State -Names @('PortalPlannedSha256', 'TrackerPlannedSha256')
    }
    foreach ($name in @('PortalPlannedSha256', 'TrackerPlannedSha256')) {
        if ([string]$State.$name -notmatch '^[A-Fa-f0-9]{64}$') { throw "Transaction state has an invalid $name value." }
    }
    if ($State.Status -eq 'Applied' -and
        ($State.PortalAppliedSha256 -ne $State.PortalPlannedSha256 -or
         $State.TrackerAppliedSha256 -ne $State.TrackerPlannedSha256)) {
        throw 'Applied transaction state hashes do not match the planned transform.'
    }
    foreach ($name in @(
        'AppliedAtUtc', 'RolledBackAtUtc', 'ApplyFailure', 'ApplyFailedAtUtc',
        'RollbackStartedAtUtc', 'RollbackFailure', 'RollbackFailedAtUtc'
    )) {
        if ($State.PSObject.Properties.Name -notcontains $name) { Set-StateProperty -State $State -Name $name -Value $null }
    }
}

function Assert-TransactionBackups {
    param([Parameter(Mandatory = $true)]$State)
    if (-not (Test-Path -LiteralPath $State.PortalBackupPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $State.TrackerBackupPath -PathType Leaf)) {
        throw 'One or more secured transaction backups are missing.'
    }
    $null = Read-JsonFile $State.PortalBackupPath
    $null = Read-JsonFile $State.TrackerBackupPath
    if ((Get-FileSha256 $State.PortalBackupPath) -ne $State.PortalOriginalSha256 -or
        (Get-FileSha256 $State.TrackerBackupPath) -ne $State.TrackerOriginalSha256) {
        throw 'Secured transaction backup hashes do not match the saved originals.'
    }
}

function New-VerifiedTransactionSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$PortalConfigPath,
        [Parameter(Mandatory = $true)][string]$TrackerConfigPath,
        [Parameter(Mandatory = $true)][string]$PortalBackupPath,
        [Parameter(Mandatory = $true)][string]$TrackerBackupPath
    )
    $portalBeforeSha256 = Get-FileSha256 $PortalConfigPath
    $trackerBeforeSha256 = Get-FileSha256 $TrackerConfigPath
    Copy-Item -LiteralPath $PortalConfigPath -Destination $PortalBackupPath
    Copy-Item -LiteralPath $TrackerConfigPath -Destination $TrackerBackupPath
    $portalBackupSha256 = Get-FileSha256 $PortalBackupPath
    $trackerBackupSha256 = Get-FileSha256 $TrackerBackupPath
    $portalAfterSha256 = Get-FileSha256 $PortalConfigPath
    $trackerAfterSha256 = Get-FileSha256 $TrackerConfigPath
    if ($portalBeforeSha256 -ne $portalBackupSha256 -or $portalAfterSha256 -ne $portalBackupSha256 -or
        $trackerBeforeSha256 -ne $trackerBackupSha256 -or $trackerAfterSha256 -ne $trackerBackupSha256) {
        throw 'Production configuration changed while the secured backup snapshot was captured; transaction refused.'
    }

    $portalBackupConfig = Read-JsonFile $PortalBackupPath
    $trackerBackupConfig = Read-JsonFile $TrackerBackupPath
    $transformed = New-TransformedConfig -PortalConfig $portalBackupConfig -TrackerConfig $trackerBackupConfig
    $portalPlannedBytes = Convert-ToUtf8JsonBytes $transformed.Portal
    $trackerPlannedBytes = Convert-ToUtf8JsonBytes $transformed.Tracker
    return [pscustomobject]@{
        PortalOriginalSha256 = $portalBackupSha256
        TrackerOriginalSha256 = $trackerBackupSha256
        PortalPlannedBytes = $portalPlannedBytes
        TrackerPlannedBytes = $trackerPlannedBytes
        PortalPlannedSha256 = Get-BytesSha256 $portalPlannedBytes
        TrackerPlannedSha256 = Get-BytesSha256 $trackerPlannedBytes
    }
}

function Assert-RecoverableActiveConfiguration {
    param([Parameter(Mandatory = $true)]$State)
    $portalHash = Get-FileSha256 $State.PortalConfigPath
    $trackerHash = Get-FileSha256 $State.TrackerConfigPath
    if ($portalHash -notin @($State.PortalOriginalSha256, $State.PortalPlannedSha256) -or
        $trackerHash -notin @($State.TrackerOriginalSha256, $State.TrackerPlannedSha256)) {
        throw 'Active production configuration contains unrelated drift; recovery refused.'
    }
}

function Assert-OriginalConfiguration {
    param([Parameter(Mandatory = $true)]$State)
    if ((Get-FileSha256 $State.PortalConfigPath) -ne $State.PortalOriginalSha256 -or
        (Get-FileSha256 $State.TrackerConfigPath) -ne $State.TrackerOriginalSha256) {
        throw 'Active production configuration does not match the saved originals.'
    }
}

function Assert-PoolsPresentAndStarted {
    foreach ($poolName in $poolNames) {
        if (-not (Test-Path -LiteralPath "IIS:\AppPools\$poolName")) { throw "Required IIS application pool '$poolName' is missing." }
        $state = (Get-WebAppPoolState -Name $poolName).Value
        if ($state -ne 'Started') { throw "IIS application pool '$poolName' must be Started before this transaction; current state is '$state'." }
    }
}

function Assert-PoolsPresent {
    foreach ($poolName in $poolNames) {
        if (-not (Test-Path -LiteralPath "IIS:\AppPools\$poolName")) { throw "Required IIS application pool '$poolName' is missing." }
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

function Invoke-WithPoolsStopped {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Precondition,
        [Parameter(Mandatory = $true)][scriptblock]$Operation,
        [Parameter(Mandatory = $true)][scriptblock]$RecoveryOperation,
        [switch]$RecoveryCompletesOperation
    )
    $stopError = $null
    try { Stop-TargetPools }
    catch { $stopError = $_ }
    if ($stopError) {
        $restartError = $null
        try { Start-TargetPools }
        catch { $restartError = $_ }
        if ($restartError) {
            throw "Pool stop failed: $($stopError.Exception.Message) Pool recovery also failed: $($restartError.Exception.Message)"
        }
        throw $stopError
    }

    $preconditionError = $null
    try { & $Precondition }
    catch { $preconditionError = $_ }
    if ($preconditionError) {
        $restartError = $null
        try { Start-TargetPools }
        catch { $restartError = $_ }
        if ($restartError) {
            throw "Stopped-pool precondition failed without changing files: $($preconditionError.Exception.Message) Pool recovery also failed: $($restartError.Exception.Message)"
        }
        throw $preconditionError
    }

    $operationError = $null
    try {
        & $Operation
    }
    catch {
        $operationError = $_
        try { & $RecoveryOperation }
        catch {
            throw "The stopped-pool operation failed: $($operationError.Exception.Message) Consistency recovery also failed: $($_.Exception.Message) The affected pools remain stopped."
        }
        if ($RecoveryCompletesOperation) { $operationError = $null }
    }
    $restartError = $null
    try { Start-TargetPools }
    catch { $restartError = $_ }
    if ($operationError -and $restartError) {
        throw "The stopped-pool operation failed but original configuration consistency was restored: $($operationError.Exception.Message) Pool recovery then failed: $($restartError.Exception.Message)"
    }
    if ($operationError) { throw $operationError }
    if ($restartError) { throw $restartError }
}

function Wait-UriHealth {
    param([Parameter(Mandatory = $true)][string[]]$Uris)
    $pending = @($Uris)
    $lastErrors = @{}
    $deadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
    do {
        foreach ($uri in @($pending)) {
            try {
                $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec 10
                if ($response.StatusCode -eq 200) {
                    $pending = @($pending | Where-Object { $_ -ne $uri })
                    $null = $lastErrors.Remove($uri)
                }
                else { $lastErrors[$uri] = "HTTP $($response.StatusCode)" }
            }
            catch { $lastErrors[$uri] = $_.Exception.Message }
        }
        if ($pending.Count -gt 0) { Start-Sleep -Milliseconds 750 }
    } while ($pending.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($pending.Count -gt 0) {
        $details = @($pending | ForEach-Object { "$_ => $($lastErrors[$_])" }) -join '; '
        throw "Health verification timed out. Last results: $details"
    }
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

function Restore-FilesWhilePoolsStopped {
    param([Parameter(Mandatory = $true)]$State)
    Assert-TransactionBackups $State
    $portalRoot = Split-Path -Parent $State.PortalConfigPath
    $trackerRoot = Split-Path -Parent $State.TrackerConfigPath
    $portalTemporary = Join-Path $portalRoot ('.appsettings.Production.restore.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $trackerTemporary = Join-Path $trackerRoot ('.appsettings.Production.restore.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Copy-Item -LiteralPath $State.PortalBackupPath -Destination $portalTemporary
        Copy-Item -LiteralPath $State.TrackerBackupPath -Destination $trackerTemporary
        $null = Read-JsonFile $portalTemporary
        $null = Read-JsonFile $trackerTemporary
        if ((Get-FileSha256 $portalTemporary) -ne $State.PortalOriginalSha256 -or
            (Get-FileSha256 $trackerTemporary) -ne $State.TrackerOriginalSha256) {
            throw 'Prepared restore files do not match the saved original hashes.'
        }
        Move-Item -LiteralPath $portalTemporary -Destination $State.PortalConfigPath -Force
        Move-Item -LiteralPath $trackerTemporary -Destination $State.TrackerConfigPath -Force
        Assert-OriginalConfiguration $State
    }
    finally {
        Remove-Item -LiteralPath $portalTemporary -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $trackerTemporary -Force -ErrorAction SilentlyContinue
    }
}

function Restore-Configuration {
    param([Parameter(Mandatory = $true)]$State, [switch]$VerifyDualScheme)
    Assert-TransactionBackups $State
    Invoke-WithPoolsStopped `
        -Precondition { Assert-RecoverableActiveConfiguration $State } `
        -Operation { Restore-FilesWhilePoolsStopped $State } `
        -RecoveryOperation { Restore-FilesWhilePoolsStopped $State } `
        -RecoveryCompletesOperation
    if ($VerifyDualScheme) { Wait-UriHealth (Get-DualSchemeHealthUris) }
    else { Wait-UriHealth (Get-HttpHealthUris) }
}

if (-not [IO.Path]::IsPathRooted($StatePath)) { throw 'StatePath must be an absolute local path.' }
$StatePath = Assert-SafeStatePath $StatePath
Assert-Host
if (-not $WhatIfPreference) { Assert-Administrator }
Import-IisAdministration
$portalRoot = Get-ActiveSitePath $portalSiteName
$trackerRoot = Get-ActiveSitePath $trackerSiteName
$portalConfigPath = Join-Path $portalRoot 'appsettings.Production.json'
$trackerConfigPath = Join-Path $trackerRoot 'appsettings.Production.json'

if ($Rollback) {
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { throw "Rollback state was not found at '$StatePath'." }
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    Assert-TransactionState -State $state -ExpectedPortalConfigPath $portalConfigPath -ExpectedTrackerConfigPath $trackerConfigPath
    Assert-TransactionBackups $state
    $terminalStatuses = @('RolledBack', 'AutomaticallyRolledBack')
    $recoverableStatuses = @('Applied', 'Prepared', 'ApplyInProgress', 'ApplyFailedRollbackPending', 'ManualRollbackPending', 'RollbackFailed')
    if ($state.Status -in $terminalStatuses) {
        Assert-OriginalConfiguration $state
        Assert-PoolsPresentAndStarted
        Wait-UriHealth (Get-DualSchemeHealthUris)
        Write-Output 'HTTPS_APPLICATION_CONFIG_ALREADY_ROLLED_BACK_AND_DUAL_SCHEME_HEALTHY'
        exit 0
    }
    if ($state.Status -notin $recoverableStatuses) { throw "Rollback state '$($state.Status)' is not recognized as recoverable." }
    Assert-RecoverableActiveConfiguration $state
    Assert-PoolsPresent
    if ($PSCmdlet.ShouldProcess($expectedComputerName, 'Restore original Portal and Project Tracker production configuration')) {
        Set-StateProperty -State $state -Name 'Status' -Value 'ManualRollbackPending'
        Set-StateProperty -State $state -Name 'RollbackStartedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
        Write-SecureState $state
        $restored = $false
        try {
            Restore-Configuration -State $state -VerifyDualScheme
            $restored = $true
            Set-StateProperty -State $state -Name 'Status' -Value 'RolledBack'
            Set-StateProperty -State $state -Name 'RolledBackAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
            Set-StateProperty -State $state -Name 'RollbackFailure' -Value $null
            Write-SecureState $state
        }
        catch {
            $rollbackFailure = $_.Exception.Message
            if ($restored) {
                throw "Original configuration was restored and is healthy, but rollback state persistence failed: $rollbackFailure Re-run -Rollback to verify and finish the state record."
            }
            Set-StateProperty -State $state -Name 'Status' -Value 'RollbackFailed'
            Set-StateProperty -State $state -Name 'RollbackFailure' -Value $rollbackFailure
            Set-StateProperty -State $state -Name 'RollbackFailedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
            $stateWriteFailure = Try-WriteSecureState $state
            if ($stateWriteFailure) { throw "Rollback failed: $rollbackFailure State persistence also failed: $stateWriteFailure" }
            throw "Rollback failed and remains recoverable: $rollbackFailure"
        }
        Write-Output 'HTTPS_APPLICATION_CONFIG_ROLLED_BACK_AND_DUAL_SCHEME_HEALTHY'
    }
    elseif ($WhatIfPreference) { Write-Output 'WHATIF_READY_ROLLBACK: production config backup, hashes, pools, and drift checks passed; nothing was changed.' }
    else { Write-Output 'HTTPS_APPLICATION_CONFIG_ROLLBACK_CANCELLED' }
    exit 0
}

Assert-PoolsPresentAndStarted
$portalOriginal = Read-JsonFile $portalConfigPath
$trackerOriginal = Read-JsonFile $trackerConfigPath
$null = New-TransformedConfig -PortalConfig $portalOriginal -TrackerConfig $trackerOriginal

# HTTPS must already be reachable before any production configuration is changed.
Wait-UriHealth (Get-DualSchemeHealthUris)
if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
    $oldState = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    Assert-TransactionState -State $oldState -ExpectedPortalConfigPath $portalConfigPath -ExpectedTrackerConfigPath $trackerConfigPath
    if ($oldState.Status -notin @('RolledBack', 'AutomaticallyRolledBack')) {
        throw "Transaction state '$($oldState.Status)' already exists at '$StatePath'; run -Rollback before applying another transaction."
    }
    Assert-OriginalConfiguration $oldState
}

if (-not $PSCmdlet.ShouldProcess($expectedComputerName, 'Atomically switch module URLs to HTTPS and configure HTTPS-first dual CORS')) {
    if ($WhatIfPreference) { Write-Output 'WHATIF_READY: active IIS paths, JSON transforms, pools, dual-scheme applications, and gateway passed preflight; nothing was changed.' }
    else { Write-Output 'HTTPS_APPLICATION_CONFIG_CANCELLED' }
    exit 0
}

$backupRoot = Join-Path $backupBaseRoot ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'))
New-SecureDirectory $backupRoot
$portalBackup = Join-Path $backupRoot 'Portal.appsettings.Production.json'
$trackerBackup = Join-Path $backupRoot 'ProjectTracker.appsettings.Production.json'
$snapshot = New-VerifiedTransactionSnapshot `
    -PortalConfigPath $portalConfigPath `
    -TrackerConfigPath $trackerConfigPath `
    -PortalBackupPath $portalBackup `
    -TrackerBackupPath $trackerBackup
$portalOriginalSha256 = $snapshot.PortalOriginalSha256
$trackerOriginalSha256 = $snapshot.TrackerOriginalSha256
$portalBytes = [byte[]]$snapshot.PortalPlannedBytes
$trackerBytes = [byte[]]$snapshot.TrackerPlannedBytes
$portalPlannedSha256 = $snapshot.PortalPlannedSha256
$trackerPlannedSha256 = $snapshot.TrackerPlannedSha256
$state = [pscustomobject]@{
    Version = 2
    ComputerName = $expectedComputerName
    Status = 'Prepared'
    PreparedAtUtc = [DateTime]::UtcNow.ToString('o')
    PortalConfigPath = $portalConfigPath
    TrackerConfigPath = $trackerConfigPath
    PortalBackupPath = $portalBackup
    TrackerBackupPath = $trackerBackup
    PortalOriginalSha256 = $portalOriginalSha256
    TrackerOriginalSha256 = $trackerOriginalSha256
    PortalPlannedSha256 = $portalPlannedSha256
    TrackerPlannedSha256 = $trackerPlannedSha256
    PortalAppliedSha256 = ''
    TrackerAppliedSha256 = ''
    AppliedAtUtc = $null
    RolledBackAtUtc = $null
    ApplyFailure = $null
    ApplyFailedAtUtc = $null
    RollbackStartedAtUtc = $null
    RollbackFailure = $null
    RollbackFailedAtUtc = $null
}
Write-SecureState $state

$portalTemporary = Join-Path $portalRoot ('.appsettings.Production.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$trackerTemporary = Join-Path $trackerRoot ('.appsettings.Production.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllBytes($portalTemporary, $portalBytes)
    [IO.File]::WriteAllBytes($trackerTemporary, $trackerBytes)
    $null = Read-JsonFile $portalTemporary
    $null = Read-JsonFile $trackerTemporary
    if ((Get-FileSha256 $portalTemporary) -ne $state.PortalPlannedSha256 -or
        (Get-FileSha256 $trackerTemporary) -ne $state.TrackerPlannedSha256) {
        throw 'Prepared production configuration hashes do not match the planned transaction.'
    }
    $state.Status = 'ApplyInProgress'
    Write-SecureState $state
    Invoke-WithPoolsStopped `
        -Precondition {
            Assert-OriginalConfiguration $state
            if ((Get-FileSha256 $portalTemporary) -ne $state.PortalPlannedSha256 -or
                (Get-FileSha256 $trackerTemporary) -ne $state.TrackerPlannedSha256) {
                throw 'Prepared configuration drifted before the stopped-pool replacement.'
            }
        } `
        -Operation {
            Move-Item -LiteralPath $portalTemporary -Destination $portalConfigPath -Force
            Move-Item -LiteralPath $trackerTemporary -Destination $trackerConfigPath -Force
            if ((Get-FileSha256 $portalConfigPath) -ne $state.PortalPlannedSha256 -or
                (Get-FileSha256 $trackerConfigPath) -ne $state.TrackerPlannedSha256) {
                throw 'Active production configuration hashes do not match the planned transaction.'
            }
        } `
        -RecoveryOperation { Restore-FilesWhilePoolsStopped $state }
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
    Set-StateProperty -State $state -Name 'Status' -Value 'ApplyFailedRollbackPending'
    Set-StateProperty -State $state -Name 'ApplyFailure' -Value $failure
    Set-StateProperty -State $state -Name 'ApplyFailedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
    $pendingStateFailure = Try-WriteSecureState $state
    $restored = $false
    try {
        Assert-RecoverableActiveConfiguration $state
        Restore-Configuration -State $state
        $restored = $true
        Set-StateProperty -State $state -Name 'Status' -Value 'AutomaticallyRolledBack'
        Set-StateProperty -State $state -Name 'RolledBackAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
        Set-StateProperty -State $state -Name 'RollbackFailure' -Value $null
        Write-SecureState $state
    }
    catch {
        $rollbackFailure = $_.Exception.Message
        if ($restored) {
            throw "Configuration transaction failed: $failure Original configuration was restored and is HTTP healthy, but final state persistence failed: $rollbackFailure Re-run -Rollback to verify and finish the state record."
        }
        Set-StateProperty -State $state -Name 'Status' -Value 'RollbackFailed'
        Set-StateProperty -State $state -Name 'RollbackFailure' -Value $rollbackFailure
        Set-StateProperty -State $state -Name 'RollbackFailedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
        $rollbackStateFailure = Try-WriteSecureState $state
        $stateFailures = @($pendingStateFailure, $rollbackStateFailure) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $stateFailureText = if ($stateFailures.Count -gt 0) { " State persistence failures: $($stateFailures -join ' | ')" } else { '' }
        throw "Configuration transaction failed: $failure Automatic rollback also failed: $rollbackFailure$stateFailureText"
    }
    $pendingStateText = if ($pendingStateFailure) { " The intermediate failure state could not be persisted: $pendingStateFailure" } else { '' }
    throw "Configuration transaction failed and original HTTP configuration was restored healthy: $failure$pendingStateText"
}
finally {
    Remove-Item -LiteralPath $portalTemporary -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $trackerTemporary -Force -ErrorAction SilentlyContinue
}
