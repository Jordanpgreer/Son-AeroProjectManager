<#
    Read-only backup prerequisite audit for SON-SQL2.

    This script does not create, overwrite, delete, grant access to, or verify a backup file. It
    deliberately requires direct NTFS and SMB-share write grants for the SQL Server network
    principal because reachability by the interactive caller does not prove SQL Server can write.
#>
[CmdletBinding()]
param(
    [string]$ExpectedComputerName = 'SON-SQL2',
    [Parameter(Mandatory)]
    [string]$OffServerBackupRoot,
    [Parameter(Mandatory)]
    [string]$ExpectedBackupHost,
    [Parameter(Mandatory)]
    [ValidateSet('Daily', 'FourHours', 'PointInTime15Minutes')]
    [string]$RecoveryPointObjective,
    [string]$DrawingRoot = 'C:\SonAero\Data\EngineeringDrawings'
)

$ErrorActionPreference = 'Stop'

function Get-IpAddressKeys {
    param([Parameter(Mandatory)][string]$HostName)

    try {
        return @([Net.Dns]::GetHostAddresses($HostName) | ForEach-Object {
            [Convert]::ToBase64String($_.GetAddressBytes())
        } | Select-Object -Unique)
    }
    catch {
        throw "Could not resolve '$HostName' to prove the backup host is off-server: $($_.Exception.Message)"
    }
}

function Test-NtfsWriteGrant {
    param(
        [Parameter(Mandatory)]$AccessRule,
        [Parameter(Mandatory)][Security.AccessControl.AccessControlType]$ControlType
    )

    $writeRights = [Security.AccessControl.FileSystemRights]::Write
    if ($AccessRule.AccessControlType -ne $ControlType -or
        ($AccessRule.PropagationFlags -band
            [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) {
        return $false
    }
    if ($ControlType -eq [Security.AccessControl.AccessControlType]::Deny) {
        return ($AccessRule.FileSystemRights -band $writeRights) -ne 0
    }
    return ($AccessRule.FileSystemRights -band $writeRights) -eq $writeRights
}

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This audit is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
}
if ([string]::IsNullOrWhiteSpace($OffServerBackupRoot) -or
    -not $OffServerBackupRoot.StartsWith('\\')) {
    throw 'OffServerBackupRoot must be an approved UNC path such as \\BACKUP01\SonAeroHub.'
}
if ([string]::IsNullOrWhiteSpace($ExpectedBackupHost)) {
    throw 'ExpectedBackupHost cannot be empty.'
}

$uncParts = @($OffServerBackupRoot.TrimStart('\').Split('\'))
if ($uncParts.Count -lt 2 -or [string]::IsNullOrWhiteSpace($uncParts[0]) -or
    [string]::IsNullOrWhiteSpace($uncParts[1])) {
    throw 'OffServerBackupRoot must include a server and share name.'
}
$destinationHost = $uncParts[0]
$destinationShare = $uncParts[1]
if ($destinationHost -ine $ExpectedBackupHost) {
    throw "The UNC host '$destinationHost' does not match ExpectedBackupHost '$ExpectedBackupHost'."
}
if ($destinationHost -in @('localhost', '127.0.0.1', '::1') -or
    $destinationHost -ieq $ExpectedComputerName -or
    $destinationHost -ieq $env:COMPUTERNAME) {
    throw 'The backup destination must be on another server, not SON-SQL2.'
}
if ($OffServerBackupRoot -match '(^|[\\/])\.\.([\\/]|$)') {
    throw 'The backup destination cannot contain a parent-directory segment.'
}

# Reject local IP literals and DNS aliases that point back to this server. A destination that
# cannot be resolved is not treated as off-server merely because the interactive caller can open it.
$backupAddressKeys = @(Get-IpAddressKeys -HostName $destinationHost)
$localAddressKeys = [System.Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
foreach ($localName in @($env:COMPUTERNAME, $ExpectedComputerName, 'localhost')) {
    foreach ($addressKey in @(Get-IpAddressKeys -HostName $localName)) {
        [void]$localAddressKeys.Add($addressKey)
    }
}
if (Get-Command Get-NetIPAddress -ErrorAction SilentlyContinue) {
    foreach ($address in @(Get-NetIPAddress -AddressFamily IPv4, IPv6 -ErrorAction Stop)) {
        try {
            $parsedAddress = [Net.IPAddress]::Parse(($address.IPAddress -split '%')[0])
            [void]$localAddressKeys.Add([Convert]::ToBase64String($parsedAddress.GetAddressBytes()))
        }
        catch {
            throw "Could not normalize local address '$($address.IPAddress)': $($_.Exception.Message)"
        }
    }
}
if (@($backupAddressKeys | Where-Object { $localAddressKeys.Contains($_) }).Count -gt 0) {
    throw "The backup destination '$destinationHost' resolves to this SQL server. Use a genuinely off-server host."
}

if (-not (Test-Path -LiteralPath $OffServerBackupRoot -PathType Container)) {
    throw "The backup destination is not reachable as the current identity: $OffServerBackupRoot"
}
$destination = Get-Item -LiteralPath $OffServerBackupRoot
if ($destination.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The backup root cannot be a reparse point.'
}
if (-not (Test-Path -LiteralPath $DrawingRoot -PathType Container)) {
    throw "The Engineering drawing root does not exist: $DrawingRoot"
}
$drawingItem = Get-Item -LiteralPath $DrawingRoot
if ($drawingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The Engineering drawing root cannot be a reparse point.'
}

$sqlService = Get-CimInstance Win32_Service -Filter "Name='MSSQLSERVER'"
$agentService = Get-CimInstance Win32_Service -Filter "Name='SQLSERVERAGENT'"
if (-not $sqlService) {
    throw 'The default SQL Server service MSSQLSERVER was not found.'
}

$computerSystem = Get-CimInstance Win32_ComputerSystem
$computerAccount = '{0}\{1}$' -f $computerSystem.Domain, $env:COMPUTERNAME
$sqlNetworkPrincipal = switch -Regex ($sqlService.StartName) {
    '^(LocalSystem|NT AUTHORITY\\SYSTEM|NT AUTHORITY\\NETWORK ?SERVICE|NT SERVICE\\MSSQLSERVER)$' {
        $computerAccount
        break
    }
    '^NT AUTHORITY\\LOCAL ?SERVICE$' {
        'UNSUITABLE: Local Service has no domain network identity'
        break
    }
    default { $sqlService.StartName }
}

$connectionBuilder = [Data.SqlClient.SqlConnectionStringBuilder]::new()
$connectionBuilder.DataSource = '(local)'
$connectionBuilder.InitialCatalog = 'master'
$connectionBuilder.IntegratedSecurity = $true
$connectionBuilder.Encrypt = $true
$connectionBuilder.TrustServerCertificate = $true
$connectionBuilder.ApplicationName = 'SonAeroBackupReadiness'

$connection = [Data.SqlClient.SqlConnection]::new($connectionBuilder.ConnectionString)
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = @"
SELECT d.[name], d.[state_desc], d.[recovery_model_desc],
       SUM(CONVERT(bigint, m.[size])) * 8192 AS [allocated_bytes]
FROM sys.databases AS d
INNER JOIN sys.master_files AS m ON m.[database_id] = d.[database_id]
WHERE d.[name] IN (N'ProjectTracker', N'EngineeringHub', N'QualityAssurance')
GROUP BY d.[name], d.[state_desc], d.[recovery_model_desc]
ORDER BY d.[name];
"@
    $reader = $command.ExecuteReader()
    $databases = [System.Collections.Generic.List[object]]::new()
    while ($reader.Read()) {
        $databases.Add([pscustomobject]@{
            Name = $reader.GetString(0)
            State = $reader.GetString(1)
            RecoveryModel = $reader.GetString(2)
            AllocatedBytes = $reader.GetInt64(3)
        })
    }
    $reader.Dispose()
}
finally {
    $connection.Dispose()
}

$drawingFiles = @(Get-ChildItem -LiteralPath $DrawingRoot -File -Recurse)
$drawingBytes = [long](($drawingFiles | Measure-Object -Property Length -Sum).Sum)
$databaseBytes = [long](($databases | Measure-Object -Property AllocatedBytes -Sum).Sum)
$estimatedRecoverySetBytes = [long]($drawingBytes + $databaseBytes)

# Three uncompressed source-size equivalents conservatively cover two complete restore points plus
# 50% operational headroom. Compression is intentionally not assumed.
$capacityMultiplier = 3.0
$minimumFreeBytes = [long][Math]::Ceiling([double]$estimatedRecoverySetBytes * $capacityMultiplier)

$remoteCimSession = $null
try {
    $remoteCimSession = New-CimSession -ComputerName $destinationHost
    $remoteShare = Get-SmbShare -CimSession $remoteCimSession -Name $destinationShare
    if (-not $remoteShare -or [string]::IsNullOrWhiteSpace($remoteShare.Path)) {
        throw "SMB share '$destinationShare' was not found on $destinationHost."
    }
    $shareAccess = @(Get-SmbShareAccess -CimSession $remoteCimSession -Name $destinationShare)

    $shareRoot = [IO.Path]::GetPathRoot($remoteShare.Path)
    if ([string]::IsNullOrWhiteSpace($shareRoot)) {
        throw "Could not determine the destination volume from remote share path '$($remoteShare.Path)'."
    }
    $driveId = $shareRoot.TrimEnd('\')
    $escapedDriveId = $driveId.Replace("'", "''")
    $destinationDisk = Get-CimInstance -CimSession $remoteCimSession -ClassName Win32_LogicalDisk `
        -Filter "DeviceID='$escapedDriveId'"
    if (-not $destinationDisk -or $null -eq $destinationDisk.FreeSpace) {
        throw "Could not prove free space for destination volume '$driveId' on $destinationHost."
    }
}
finally {
    if ($remoteCimSession) {
        Remove-CimSession -CimSession $remoteCimSession
    }
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($databaseName in @('ProjectTracker', 'EngineeringHub', 'QualityAssurance')) {
    $database = $databases | Where-Object Name -EQ $databaseName | Select-Object -First 1
    if (-not $database) {
        $failures.Add("Database $databaseName was not found.")
        continue
    }
    if ($database.State -ne 'ONLINE') {
        $failures.Add("Database $databaseName is $($database.State), not ONLINE.")
    }
    if ($RecoveryPointObjective -eq 'PointInTime15Minutes' -and
        $database.RecoveryModel -ne 'FULL') {
        $failures.Add("Database $databaseName must use FULL recovery for the PointInTime15Minutes RPO; it is $($database.RecoveryModel).")
    }
}
if ($sqlService.State -ne 'Running') {
    $failures.Add('MSSQLSERVER is not running.')
}
if (-not $agentService -or $agentService.State -ne 'Running') {
    $failures.Add('SQLSERVERAGENT is not installed or running; scheduled jobs cannot run yet.')
}
if ($sqlNetworkPrincipal -like 'UNSUITABLE:*') {
    $failures.Add($sqlNetworkPrincipal)
}
if ([uint64]$destinationDisk.FreeSpace -lt [uint64]$minimumFreeBytes) {
    $failures.Add("The backup volume has $($destinationDisk.FreeSpace) free bytes; at least $minimumFreeBytes are required for two restore points plus conservative headroom.")
}

$ntfsAcl = Get-Acl -LiteralPath $OffServerBackupRoot
$matchingNtfsEntries = @($ntfsAcl.Access | Where-Object {
    $_.IdentityReference.Value -ieq $sqlNetworkPrincipal
})
$ntfsWriteAllows = @($matchingNtfsEntries | Where-Object {
    Test-NtfsWriteGrant -AccessRule $_ -ControlType Allow
})
$ntfsWriteDenies = @($ntfsAcl.Access | Where-Object {
    Test-NtfsWriteGrant -AccessRule $_ -ControlType Deny
})

$matchingShareEntries = @($shareAccess | Where-Object {
    $_.AccountName -ieq $sqlNetworkPrincipal
})
$shareWriteAllows = @($matchingShareEntries | Where-Object {
    $_.AccessControlType -eq 'Allow' -and $_.AccessRight -in @('Change', 'Full')
})
$shareWriteDenies = @($shareAccess | Where-Object {
    $_.AccessControlType -eq 'Deny'
})

if ($ntfsWriteAllows.Count -eq 0 -or $ntfsWriteDenies.Count -gt 0) {
    $failures.Add("Direct NTFS write access for SQL network principal '$sqlNetworkPrincipal' was not proven, or a write-deny ACE exists on the destination root.")
}
if ($shareWriteAllows.Count -eq 0 -or $shareWriteDenies.Count -gt 0) {
    $failures.Add("Direct SMB share Change/Full access for SQL network principal '$sqlNetworkPrincipal' was not proven, or a deny ACE exists on the share.")
}
$sqlServiceAclWriteAccessProven = $ntfsWriteAllows.Count -gt 0 -and
    $ntfsWriteDenies.Count -eq 0 -and
    $shareWriteAllows.Count -gt 0 -and
    $shareWriteDenies.Count -eq 0

[pscustomobject]@{
    Status = if ($failures.Count -eq 0) { 'BACKUP_PREREQUISITES_READY' } else { 'BACKUP_NOT_READY' }
    ComputerName = $env:COMPUTERNAME
    RecoveryPointObjective = $RecoveryPointObjective
    Destination = $OffServerBackupRoot
    DestinationHost = $destinationHost
    DestinationResolvedAddresses = @([Net.Dns]::GetHostAddresses($destinationHost) | ForEach-Object ToString)
    DestinationReachableByCaller = $true
    DestinationSharePath = $remoteShare.Path
    DestinationVolume = $driveId
    DestinationFreeBytes = [uint64]$destinationDisk.FreeSpace
    EstimatedRecoverySetBytes = $estimatedRecoverySetBytes
    MinimumFreeBytes = $minimumFreeBytes
    CapacityBasis = '3x uncompressed database allocation plus drawing bytes (two sets + 50% headroom)'
    SqlServiceState = $sqlService.State
    SqlServiceLogon = $sqlService.StartName
    SqlNetworkPrincipal = $sqlNetworkPrincipal
    SqlServiceAclWriteAccessProven = $sqlServiceAclWriteAccessProven
    ControlledWriteTestPerformed = $false
    BackupOperationsStatus = 'NOT_OPERATIONAL_UNTIL_CHECKSUM_BACKUPS_VERIFY_AND_RESTORE_DRILL_SUCCEED'
    SqlAgentState = if ($agentService) { $agentService.State } else { 'Missing' }
    Databases = $databases.ToArray()
    DrawingRoot = $DrawingRoot
    DrawingFileCount = $drawingFiles.Count
    DrawingBytes = $drawingBytes
    NtfsDirectEntries = @($matchingNtfsEntries | ForEach-Object {
        '{0} {1} {2}' -f $_.IdentityReference.Value, $_.FileSystemRights, $_.AccessControlType
    })
    ShareDirectEntries = @($matchingShareEntries | ForEach-Object {
        '{0} {1} {2}' -f $_.AccountName, $_.AccessRight, $_.AccessControlType
    })
    RequiredNextActions = @(
        "Retain at least two restore points outside $ExpectedComputerName.",
        "Keep direct NTFS Write and SMB Change/Full grants for '$sqlNetworkPrincipal' on the approved destination.",
        'Create CHECKSUM backups for ProjectTracker, EngineeringHub, and QualityAssurance and run RESTORE VERIFYONLY WITH CHECKSUM.',
        'Back up EngineeringHub and EngineeringDrawings$ as one quiesced recovery set.',
        'Perform a restore drill on a non-production SQL instance before calling backups operational.'
    )
    Failures = $failures.ToArray()
}

if ($failures.Count -gt 0) {
    throw "Backup readiness failed: $($failures -join ' ') No backup data or permissions were changed."
}
