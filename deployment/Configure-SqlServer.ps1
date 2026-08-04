<#
    One-time SON-SQL2 setup for the SON-AERO Hub.
    Run from an elevated PowerShell session on SON-SQL2 only.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$ExpectedComputerName = 'SON-SQL2',
    [ValidateRange(1024, 65535)]
    [int]$SqlPort = 1433,
    [string]$IisComputerAccount = 'SON4L\SON-IIS2$',
    [string]$IisServerAddress = '10.50.10.244',
    [string]$DrawingRoot = 'C:\SonAero\Data\EngineeringDrawings',
    [string]$DrawingShareName = 'EngineeringDrawings$'
)

$ErrorActionPreference = 'Stop'
$sqlServiceName = 'MSSQLSERVER'
$sqlAgentServiceName = 'SQLSERVERAGENT'
$localSqlServer = '(local)'
$bootstrapApplicationName = 'SonAeroSqlBootstrap'
$firewallName = 'SON-AERO Hub SQL from SON-IIS2'

function Test-IsLocalAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-SqlExceptionNumber([System.Management.Automation.ErrorRecord]$errorRecord) {
    $exception = $errorRecord.Exception
    while ($exception) {
        if ($exception -is [System.Data.SqlClient.SqlException]) {
            return $exception.Number
        }
        $exception = $exception.InnerException
    }
    return $null
}

function Get-DefaultSqlService {
    $service = Get-CimInstance Win32_Service -Filter "Name='$sqlServiceName'"
    if (-not $service) {
        throw "The $sqlServiceName service was not found."
    }
    return $service
}

function Stop-DefaultSqlService {
    $service = Get-Service $sqlServiceName
    if ($service.Status -ne 'Stopped') {
        Stop-Service $sqlServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
}

function Start-DefaultSqlServiceNormally {
    Stop-DefaultSqlService
    Start-Service $sqlServiceName
    (Get-Service $sqlServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
}

function Wait-DefaultSqlServiceNormal(
    [int]$expectedPort = 0,
    [int]$timeoutSeconds = 60,
    [bool]$verifySqlState = $true
) {
    for ($attempt = 0; $attempt -lt $timeoutSeconds; $attempt++) {
        $service = Get-DefaultSqlService
        if ($service.State -eq 'Running' -and $service.ProcessId -gt 0) {
            $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($service.ProcessId)"
            $singleUserArgument = $process.CommandLine -match '(?i)(?:^|\s)-(?:m|f)(?:\S*)?'
            if (-not $singleUserArgument) {
                $listenerReady = $expectedPort -eq 0
                if (-not $listenerReady) {
                    $listenerReady = [bool](Get-NetTCPConnection -State Listen -OwningProcess $service.ProcessId `
                            -ErrorAction SilentlyContinue |
                        Where-Object {
                            $_.LocalPort -eq $expectedPort -and
                            $_.LocalAddress -notin @('127.0.0.1', '::1')
                        } |
                        Select-Object -First 1)
                }

                if ($listenerReady) {
                    if (-not $verifySqlState) {
                        return $service
                    }
                    try {
                        $isSingleUser = Invoke-HubSql $localSqlServer 'master' `
                            "SELECT CONVERT(int, SERVERPROPERTY('IsSingleUser'));" `
                            'SonAeroSqlNormalVerification' 2
                        if ($isSingleUser -eq 0) {
                            return $service
                        }
                    }
                    catch {
                        # SQL may still be finishing startup. Retry until the guarded timeout expires.
                    }
                }
            }
        }
        Start-Sleep -Seconds 1
    }

    if ($expectedPort -gt 0) {
        throw "SQL Server did not return to normal multi-user mode with an external TCP $expectedPort listener."
    }
    throw 'SQL Server did not return to normal multi-user mode.'
}

Add-Type -AssemblyName System.Data
function Invoke-HubSql(
    [string]$server,
    [string]$database,
    [string]$commandText,
    [string]$applicationName = 'SonAeroSqlSetup',
    [ValidateRange(1, 60)]
    [int]$connectionTimeout = 10
) {
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder.DataSource = $server
    $builder.InitialCatalog = $database
    $builder.IntegratedSecurity = $true
    $builder.Encrypt = $false
    $builder.TrustServerCertificate = $true
    $builder.ApplicationName = $applicationName
    $builder.Pooling = $false
    $builder.ConnectTimeout = $connectionTimeout

    $connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandTimeout = 120
        $command.CommandText = $commandText
        return $command.ExecuteScalar()
    }
    finally {
        $connection.Dispose()
    }
}

function Configure-HubDatabases([string]$applicationName) {
    $escapedAccount = $IisComputerAccount.Replace(']', ']]').Replace("'", "''")
    $createDatabasesSql = @"
IF DB_ID(N'ProjectTracker') IS NULL EXEC(N'CREATE DATABASE [ProjectTracker]');
IF DB_ID(N'EngineeringHub') IS NULL EXEC(N'CREATE DATABASE [EngineeringHub]');
SELECT 1;
"@
    [void](Invoke-HubSql $localSqlServer 'master' $createDatabasesSql $applicationName)

    $createLoginSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE [name] = N'$escapedAccount')
    CREATE LOGIN [$escapedAccount] FROM WINDOWS;
SELECT 1;
"@
    [void](Invoke-HubSql $localSqlServer 'master' $createLoginSql $applicationName)

    foreach ($databaseName in @('ProjectTracker', 'EngineeringHub')) {
        $grantDatabaseSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'$escapedAccount')
    CREATE USER [$escapedAccount] FOR LOGIN [$escapedAccount];
IF ISNULL(IS_ROLEMEMBER(N'db_datareader', N'$escapedAccount'), 0) <> 1
    ALTER ROLE [db_datareader] ADD MEMBER [$escapedAccount];
IF ISNULL(IS_ROLEMEMBER(N'db_datawriter', N'$escapedAccount'), 0) <> 1
    ALTER ROLE [db_datawriter] ADD MEMBER [$escapedAccount];
IF ISNULL(IS_ROLEMEMBER(N'db_ddladmin', N'$escapedAccount'), 0) <> 1
    ALTER ROLE [db_ddladmin] ADD MEMBER [$escapedAccount];
SELECT 1;
"@
        [void](Invoke-HubSql $localSqlServer $databaseName $grantDatabaseSql $applicationName)
    }
}

function Restore-FirewallRule($snapshot, [bool]$ruleExisted) {
    if (-not $ruleExisted) {
        Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue
        return
    }

    Set-NetFirewallRule -DisplayName $firewallName -Enabled $snapshot.Enabled `
        -Direction $snapshot.Direction -Action $snapshot.Action
    Get-NetFirewallRule -DisplayName $firewallName |
        Get-NetFirewallAddressFilter |
        Set-NetFirewallAddressFilter -RemoteAddress $snapshot.RemoteAddress
    Get-NetFirewallRule -DisplayName $firewallName |
        Get-NetFirewallPortFilter |
        Set-NetFirewallPortFilter -Protocol $snapshot.Protocol -LocalPort $snapshot.LocalPort
}

function Restore-SqlNetworkState(
    [string]$registryBackupPath,
    $firewallSnapshot,
    [bool]$firewallRuleExisted,
    [bool]$verifySqlState
) {
    $recoveryErrors = [System.Collections.Generic.List[string]]::new()
    try {
        Stop-DefaultSqlService
    }
    catch {
        $recoveryErrors.Add("Could not stop SQL Server for rollback: $($_.Exception.Message)")
    }

    try {
        & reg.exe import $registryBackupPath | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "reg.exe exited with code $LASTEXITCODE"
        }
    }
    catch {
        $recoveryErrors.Add("Could not restore SQL network registry: $($_.Exception.Message)")
    }

    try {
        Restore-FirewallRule $firewallSnapshot $firewallRuleExisted
    }
    catch {
        $recoveryErrors.Add("Could not restore the firewall rule: $($_.Exception.Message)")
    }

    try {
        Start-DefaultSqlServiceNormally
        [void](Wait-DefaultSqlServiceNormal 0 60 $verifySqlState)
    }
    catch {
        $recoveryErrors.Add("Could not restore SQL Server to normal service mode: $($_.Exception.Message)")
    }

    if ($recoveryErrors.Count -gt 0) {
        throw ($recoveryErrors -join ' ')
    }
}

function Assert-NoReparsePointInDrawingPath([string]$path) {
    $root = [IO.Path]::GetPathRoot($path)
    $relativePath = $path.Substring($root.Length).TrimStart('\')
    $currentPath = $root
    foreach ($segment in $relativePath.Split('\')) {
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            break
        }
        $item = Get-Item -LiteralPath $currentPath
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "DrawingRoot ancestor $currentPath is a reparse point; refusing recursive permissions or sharing."
        }
    }
}

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
}
if ($IisComputerAccount -notmatch '^[A-Za-z0-9._-]+\\[A-Za-z0-9._-]+\$$') {
    throw 'IisComputerAccount must be a domain computer account such as SON4L\SON-IIS2$.'
}
if ($DrawingShareName -notmatch '^[A-Za-z0-9._-]+\$$') {
    throw 'DrawingShareName must be a hidden SMB share name ending in $.'
}

$approvedDrawingRoot = [IO.Path]::GetFullPath('C:\SonAero\Data').TrimEnd('\')
if (-not [IO.Path]::IsPathRooted($DrawingRoot) -or $DrawingRoot.StartsWith('\\')) {
    throw 'DrawingRoot must be a local absolute path under C:\SonAero\Data.'
}
$resolvedDrawingRoot = [IO.Path]::GetFullPath($DrawingRoot).TrimEnd('\')
$approvedDrawingPrefix = $approvedDrawingRoot + '\'
if (-not $resolvedDrawingRoot.StartsWith($approvedDrawingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DrawingRoot must be a child folder under C:\SonAero\Data; drive roots and traversal are rejected.'
}
$DrawingRoot = $resolvedDrawingRoot
Assert-NoReparsePointInDrawingPath $DrawingRoot

$preflightShare = Get-SmbShare -Name $DrawingShareName -ErrorAction SilentlyContinue
if ($preflightShare) {
    $preflightSharePath = [IO.Path]::GetFullPath($preflightShare.Path).TrimEnd('\')
    if (-not $preflightSharePath.Equals($DrawingRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Share $DrawingShareName already points to $($preflightShare.Path), not $DrawingRoot. No changes were made."
    }
    Assert-NoReparsePointInDrawingPath $preflightSharePath
}

$instanceMapPath = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
$instanceMap = Get-ItemProperty -LiteralPath $instanceMapPath
$instanceId = $instanceMap.MSSQLSERVER
if ([string]::IsNullOrWhiteSpace($instanceId)) {
    throw 'The default MSSQLSERVER instance was not found.'
}
$tcpPath = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instanceId\MSSQLServer\SuperSocketNetLib\Tcp"
$ipAllPath = Join-Path $tcpPath 'IPAll'
if (-not (Test-Path -LiteralPath $tcpPath) -or -not (Test-Path -LiteralPath $ipAllPath)) {
    throw 'The default SQL Server TCP registry configuration was not found.'
}

$sqlService = Get-DefaultSqlService
if ($sqlService.State -ne 'Running' -or $sqlService.ProcessId -le 0) {
    throw 'MSSQLSERVER must be running before this guarded setup begins.'
}
$isLocalAdministrator = Test-IsLocalAdministrator
$currentIdentityName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$needsSingleUserBootstrap = $false
$normalSqlProbeAvailable = $false
try {
    $isSysAdmin = Invoke-HubSql $localSqlServer 'master' "SELECT IS_SRVROLEMEMBER('sysadmin');"
    $normalSqlProbeAvailable = $true
    $needsSingleUserBootstrap = $isSysAdmin -ne 1
}
catch {
    $sqlErrorNumber = Get-SqlExceptionNumber $_
    if ($sqlErrorNumber -eq 18456 -and $isLocalAdministrator) {
        $needsSingleUserBootstrap = $true
    }
    else {
        throw "The local SQL preflight connection failed unexpectedly. No changes were made. $($_.Exception.Message)"
    }
}
if ($needsSingleUserBootstrap -and -not $isLocalAdministrator) {
    throw "The current Windows identity ($currentIdentityName) is neither a SQL sysadmin nor a local Windows administrator. No changes were made."
}

$existingFirewallRules = @(Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue)
if ($existingFirewallRules.Count -gt 1) {
    throw "More than one firewall rule is named '$firewallName'; no changes were made."
}
$firewallRuleExisted = $existingFirewallRules.Count -eq 1
$firewallSnapshot = $null
if ($firewallRuleExisted) {
    $addressFilter = $existingFirewallRules[0] | Get-NetFirewallAddressFilter
    $portFilter = $existingFirewallRules[0] | Get-NetFirewallPortFilter
    $firewallSnapshot = [pscustomobject]@{
        Enabled       = $existingFirewallRules[0].Enabled
        Direction     = $existingFirewallRules[0].Direction
        Action        = $existingFirewallRules[0].Action
        RemoteAddress = $addressFilter.RemoteAddress
        Protocol      = $portFilter.Protocol
        LocalPort     = $portFilter.LocalPort
    }
}

$sqlAction = "Enable external SQL TCP $SqlPort, restart MSSQLSERVER, create Hub databases, and create the drawing share"
if ($needsSingleUserBootstrap) {
    $sqlAction = "Temporarily restart MSSQLSERVER in restricted single-user mode, $sqlAction, then restore normal multi-user mode"
}
if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName SQL Server and Engineering share",
        $sqlAction)) {
    return
}

$backupRoot = 'C:\SonAero\backups'
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
$networkRegistryPath = "HKLM\SOFTWARE\Microsoft\Microsoft SQL Server\$instanceId\MSSQLServer\SuperSocketNetLib"
$backupPath = Join-Path $backupRoot ("sql-network-{0:yyyyMMdd-HHmmss}.reg" -f (Get-Date))
& reg.exe export $networkRegistryPath $backupPath /y | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'SQL network registry backup failed; no network changes were made.'
}

$agentService = Get-Service $sqlAgentServiceName -ErrorAction SilentlyContinue
$agentWasRunning = $agentService -and $agentService.Status -eq 'Running'
$operationError = $null
$rollbackError = $null
$agentRestoreError = $null

try {
    Set-ItemProperty -LiteralPath $tcpPath -Name Enabled -Value 1
    Set-ItemProperty -LiteralPath $tcpPath -Name ListenOnAllIPs -Value 1
    Set-ItemProperty -LiteralPath $ipAllPath -Name TcpDynamicPorts -Value ''
    Set-ItemProperty -LiteralPath $ipAllPath -Name TcpPort -Value $SqlPort.ToString()

    if ($firewallRuleExisted) {
        Set-NetFirewallRule -DisplayName $firewallName -Enabled True -Direction Inbound -Action Allow
        $existingFirewallRules[0] | Get-NetFirewallAddressFilter |
            Set-NetFirewallAddressFilter -RemoteAddress $IisServerAddress
        $existingFirewallRules[0] | Get-NetFirewallPortFilter |
            Set-NetFirewallPortFilter -Protocol TCP -LocalPort $SqlPort
    }
    else {
        New-NetFirewallRule -DisplayName $firewallName -Direction Inbound -Action Allow `
            -Protocol TCP -LocalPort $SqlPort -RemoteAddress $IisServerAddress | Out-Null
    }

    if ($needsSingleUserBootstrap) {
        if ($agentWasRunning) {
            Stop-Service $sqlAgentServiceName -Force
        }

        Stop-DefaultSqlService
        $singleUserOutput = & net.exe start $sqlServiceName '/m"SonAeroSqlBootstrap"' 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "SQL Server could not start in restricted single-user mode: $($singleUserOutput -join ' ')"
        }

        $bootstrapReady = $false
        $lastBootstrapError = $null
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            try {
                [void](Invoke-HubSql $localSqlServer 'master' 'SELECT 1;' $bootstrapApplicationName)
                $bootstrapReady = $true
                break
            }
            catch {
                $lastBootstrapError = $_
                Start-Sleep -Seconds 1
            }
        }
        if (-not $bootstrapReady) {
            throw "SQL Server did not accept the restricted bootstrap connection: $($lastBootstrapError.Exception.Message)"
        }

        Configure-HubDatabases $bootstrapApplicationName
        Start-DefaultSqlServiceNormally
    }
    else {
        Start-DefaultSqlServiceNormally
    }

    [void](Wait-DefaultSqlServiceNormal $SqlPort 60 $normalSqlProbeAvailable)
    if (-not $needsSingleUserBootstrap) {
        Configure-HubDatabases 'SonAeroSqlSetup'
    }
}
catch {
    $operationError = $_
    try {
        Restore-SqlNetworkState $backupPath $firewallSnapshot $firewallRuleExisted $normalSqlProbeAvailable
    }
    catch {
        $rollbackError = $_
    }
}
finally {
    if ($agentWasRunning) {
        try {
            $currentAgentService = Get-Service $sqlAgentServiceName
            if ($currentAgentService.Status -ne 'Running') {
                Start-Service $sqlAgentServiceName
                $currentAgentService.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
            }
        }
        catch {
            $agentRestoreError = $_
        }
    }
}

if ($operationError) {
    $failureMessage = "SQL setup failed and stopped before the drawing share step: $($operationError.Exception.Message)"
    if ($rollbackError) {
        $failureMessage += " Automatic rollback also reported: $($rollbackError.Exception.Message)"
    }
    else {
        $failureMessage += " The original SQL network configuration and normal service mode were restored from $backupPath."
    }
    $failureMessage += ' Database-side objects created before the failure may remain; the setup is idempotent and safe to rerun after review.'
    if ($agentRestoreError) {
        $failureMessage += " SQL Server Agent restoration also reported: $($agentRestoreError.Exception.Message)"
    }
    throw $failureMessage
}
if ($agentRestoreError) {
    throw "SQL configuration succeeded, but SQL Server Agent could not be restarted: $($agentRestoreError.Exception.Message)"
}

New-Item -ItemType Directory -Force -Path $DrawingRoot | Out-Null
Assert-NoReparsePointInDrawingPath $DrawingRoot
& icacls.exe $DrawingRoot /grant "${IisComputerAccount}:(OI)(CI)M" /t /c | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "NTFS permission assignment failed for $DrawingRoot."
}

$share = Get-SmbShare -Name $DrawingShareName -ErrorAction SilentlyContinue
if ($share) {
    $existingSharePath = [IO.Path]::GetFullPath($share.Path).TrimEnd('\')
    if (-not $existingSharePath.Equals($DrawingRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Share $DrawingShareName already points to $($share.Path), not $DrawingRoot."
    }
}
if (-not $share) {
    New-SmbShare -Name $DrawingShareName -Path $DrawingRoot `
        -ChangeAccess $IisComputerAccount -FullAccess 'BUILTIN\Administrators' | Out-Null
}
elseif (-not (Get-SmbShareAccess -Name $DrawingShareName |
        Where-Object AccountName -IEQ $IisComputerAccount)) {
    Grant-SmbShareAccess -Name $DrawingShareName -AccountName $IisComputerAccount `
        -AccessRight Change -Force | Out-Null
}

Write-Host "SQL Server is in normal multi-user service mode and listening externally on TCP $SqlPort."
Write-Host 'ProjectTracker and EngineeringHub databases are ready.'
Write-Host "Drawing share is ready at \\$ExpectedComputerName\$DrawingShareName."
Write-Host "SQL network backup: $backupPath"
