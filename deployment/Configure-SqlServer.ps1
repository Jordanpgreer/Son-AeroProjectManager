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
    [string]$DrawingRoot = 'D:\SonAero\EngineeringDrawings',
    [string]$DrawingShareName = 'EngineeringDrawings$'
)

$ErrorActionPreference = 'Stop'

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
}
if ($IisComputerAccount -notmatch '^[A-Za-z0-9._-]+\\[A-Za-z0-9._-]+\$$') {
    throw 'IisComputerAccount must be a domain computer account such as SON4L\SON-IIS2$.'
}
if ($DrawingShareName -notmatch '^[A-Za-z0-9._-]+\$$') {
    throw 'DrawingShareName must be a hidden SMB share name ending in $.'
}

$instanceMapPath = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
$instanceMap = Get-ItemProperty -LiteralPath $instanceMapPath
$instanceId = $instanceMap.MSSQLSERVER
if ([string]::IsNullOrWhiteSpace($instanceId)) {
    throw 'The default MSSQLSERVER instance was not found.'
}

$sqlProcess = Get-Process sqlservr -ErrorAction Stop | Select-Object -First 1
$currentListeners = @(Get-NetTCPConnection -State Listen -OwningProcess $sqlProcess.Id -ErrorAction SilentlyContinue)
$currentPort = $currentListeners | Select-Object -ExpandProperty LocalPort -Unique | Select-Object -First 1
if (-not $currentPort) {
    throw 'SQL Server has no current TCP listener; refusing to change it without a working local admin connection.'
}

Add-Type -AssemblyName System.Data
function Invoke-HubSql([string]$server, [string]$commandText) {
    $connection = [System.Data.SqlClient.SqlConnection]::new(
        "Server=$server;Database=master;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Connection Timeout=10")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandTimeout = 60
        $command.CommandText = $commandText
        return $command.ExecuteScalar()
    }
    finally {
        $connection.Dispose()
    }
}

$isSysAdmin = Invoke-HubSql "tcp:127.0.0.1,$currentPort" "SELECT IS_SRVROLEMEMBER('sysadmin');"
if ($isSysAdmin -ne 1) {
    throw 'The current Windows identity is not a SQL Server sysadmin. No changes were made.'
}

if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName SQL Server and Engineering share",
        "Enable SQL TCP $SqlPort, restart MSSQLSERVER, create Hub databases, and create the drawing share")) {
    return
}

$backupRoot = 'C:\SonAero\backups'
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
$networkRegistryPath = "HKLM\SOFTWARE\Microsoft\Microsoft SQL Server\$instanceId\MSSQLServer\SuperSocketNetLib"
$backupPath = Join-Path $backupRoot ("sql-network-{0:yyyyMMdd-HHmmss}.reg" -f (Get-Date))
& reg.exe export $networkRegistryPath $backupPath /y | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'SQL network registry backup failed; no network changes were made.' }

$tcpPath = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instanceId\MSSQLServer\SuperSocketNetLib\Tcp"
$ipAllPath = Join-Path $tcpPath 'IPAll'
Set-ItemProperty -LiteralPath $tcpPath -Name Enabled -Value 1
Set-ItemProperty -LiteralPath $tcpPath -Name ListenOnAllIPs -Value 1
Set-ItemProperty -LiteralPath $ipAllPath -Name TcpDynamicPorts -Value ''
Set-ItemProperty -LiteralPath $ipAllPath -Name TcpPort -Value $SqlPort.ToString()

$firewallName = 'SON-AERO Hub SQL from SON-IIS2'
$existingFirewallRule = Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue
if ($existingFirewallRule) {
    Set-NetFirewallRule -DisplayName $firewallName -Enabled True -Direction Inbound -Action Allow
    $existingFirewallRule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress $IisServerAddress
    $existingFirewallRule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort $SqlPort
}
else {
    New-NetFirewallRule -DisplayName $firewallName -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $SqlPort -RemoteAddress $IisServerAddress | Out-Null
}

Restart-Service MSSQLSERVER -Force
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    Start-Sleep -Seconds 1
    $service = Get-Service MSSQLSERVER
    if ($service.Status -eq 'Running') {
        $sqlProcess = Get-Process sqlservr -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($sqlProcess -and (Get-NetTCPConnection -State Listen -OwningProcess $sqlProcess.Id -ErrorAction SilentlyContinue |
                Where-Object LocalPort -eq $SqlPort)) {
            break
        }
    }
}
if (-not $sqlProcess -or -not (Get-NetTCPConnection -State Listen -OwningProcess $sqlProcess.Id -ErrorAction SilentlyContinue |
        Where-Object LocalPort -eq $SqlPort)) {
    throw "SQL Server did not begin listening on TCP $SqlPort. Registry backup: $backupPath"
}

$escapedAccount = $IisComputerAccount.Replace("]", "]]").Replace("'", "''")
$databaseSql = @"
IF DB_ID(N'ProjectTracker') IS NULL CREATE DATABASE [ProjectTracker];
IF DB_ID(N'EngineeringHub') IS NULL CREATE DATABASE [EngineeringHub];
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE [name] = N'$escapedAccount')
    CREATE LOGIN [$escapedAccount] FROM WINDOWS;

USE [ProjectTracker];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'$escapedAccount')
    CREATE USER [$escapedAccount] FOR LOGIN [$escapedAccount];
IF IS_ROLEMEMBER(N'db_datareader', N'$escapedAccount') <> 1 ALTER ROLE [db_datareader] ADD MEMBER [$escapedAccount];
IF IS_ROLEMEMBER(N'db_datawriter', N'$escapedAccount') <> 1 ALTER ROLE [db_datawriter] ADD MEMBER [$escapedAccount];
IF IS_ROLEMEMBER(N'db_ddladmin', N'$escapedAccount') <> 1 ALTER ROLE [db_ddladmin] ADD MEMBER [$escapedAccount];

USE [EngineeringHub];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'$escapedAccount')
    CREATE USER [$escapedAccount] FOR LOGIN [$escapedAccount];
IF IS_ROLEMEMBER(N'db_datareader', N'$escapedAccount') <> 1 ALTER ROLE [db_datareader] ADD MEMBER [$escapedAccount];
IF IS_ROLEMEMBER(N'db_datawriter', N'$escapedAccount') <> 1 ALTER ROLE [db_datawriter] ADD MEMBER [$escapedAccount];
IF IS_ROLEMEMBER(N'db_ddladmin', N'$escapedAccount') <> 1 ALTER ROLE [db_ddladmin] ADD MEMBER [$escapedAccount];
SELECT 1;
"@
[void](Invoke-HubSql "tcp:127.0.0.1,$SqlPort" $databaseSql)

New-Item -ItemType Directory -Force -Path $DrawingRoot | Out-Null
& icacls.exe $DrawingRoot /grant "${IisComputerAccount}:(OI)(CI)M" /t /c | Out-Null
if ($LASTEXITCODE -ne 0) { throw "NTFS permission assignment failed for $DrawingRoot." }

$share = Get-SmbShare -Name $DrawingShareName -ErrorAction SilentlyContinue
if ($share -and $share.Path -ine $DrawingRoot) {
    throw "Share $DrawingShareName already points to $($share.Path), not $DrawingRoot."
}
if (-not $share) {
    New-SmbShare -Name $DrawingShareName -Path $DrawingRoot `
        -ChangeAccess $IisComputerAccount -FullAccess 'BUILTIN\Administrators' | Out-Null
}
elseif (-not (Get-SmbShareAccess -Name $DrawingShareName | Where-Object AccountName -IEQ $IisComputerAccount)) {
    Grant-SmbShareAccess -Name $DrawingShareName -AccountName $IisComputerAccount `
        -AccessRight Change -Force | Out-Null
}

Write-Host "SQL Server is listening on TCP $SqlPort."
Write-Host 'ProjectTracker and EngineeringHub databases are ready.'
Write-Host "Drawing share is ready at \\$ExpectedComputerName\$DrawingShareName."
Write-Host "SQL network backup: $backupPath"
