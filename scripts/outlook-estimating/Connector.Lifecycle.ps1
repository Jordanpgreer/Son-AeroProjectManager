# Local lifecycle helpers. Importing this file does not install, launch or stop anything.
function Get-ConnectorLocalRoot {
    return Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Arda\OutlookEstimating'
}
function Get-ConnectorObjectName {
    param([ValidateSet('Mutex', 'Stop')][string]$Kind)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $suffix = (Get-ConnectorHash $sid).Substring(0, 24)
    if ($Kind -eq 'Mutex') { return 'Local\Arda-OutlookQuoteConnector-' + $suffix }
    return 'Local\Arda-OutlookQuoteConnector-Stop-' + $suffix
}
function Test-ConnectorRunning {
    $mutex = [Threading.Mutex]::new($false, (Get-ConnectorObjectName 'Mutex'))
    $owned = $false
    try {
        try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
        return -not $owned
    } finally { if ($owned) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
}
function Request-ConnectorStop {
    $event = $null
    try {
        $event = [Threading.EventWaitHandle]::OpenExisting((Get-ConnectorObjectName 'Stop'))
        [void]$event.Set()
    } catch [Threading.WaitHandleCannotBeOpenedException] { }
    finally { if ($event) { $event.Dispose() } }
}
function Wait-ConnectorStopped {
    param([int]$TimeoutSeconds = 20)
    for ($elapsed = 0; $elapsed -lt $TimeoutSeconds; $elapsed++) {
        if (-not (Test-ConnectorRunning)) { return }
        Start-Sleep -Seconds 1
    }
    if (Test-ConnectorRunning) { Stop-ConnectorOperation 'stop-requested-wait-for-current-request-then-retry' }
}
function Get-ConnectorBackoffSeconds {
    param([int]$Attempt)
    return [int][Math]::Min(300, 30 * [Math]::Pow(2, [Math]::Min(4, [Math]::Max(0, $Attempt - 1))))
}
function Get-ConnectorPackageNames {
    return @('Start-OutlookEstimating.ps1', 'Connector.Core.ps1', 'Connector.Outlook.ps1',
        'Connector.Lifecycle.ps1', 'Connector.Session.ps1', 'Manage-Connector.ps1',
        'Test-Connector.ps1', 'Test-Lifecycle.ps1', 'Test-Environment.ps1',
        'Start.cmd', 'Install.cmd', 'Start-Background.cmd', 'Stop.cmd', 'Status.cmd', 'Uninstall.cmd',
        'README.md', 'configuration.json', 'configuration.json.example')
}
function Get-ConnectorStartupCommand {
    param([string]$InstallDirectory)
    $path = [IO.Path]::GetFullPath((Join-Path $InstallDirectory 'Start-OutlookEstimating.ps1'))
    if ($path.Contains('"') -or $path.Contains("`r") -or $path.Contains("`n")) { Stop-ConnectorOperation 'invalid-install-path' }
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    return '"' + $powershell + '" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File "' + $path + '" -Background'
}
function Install-ConnectorPackage {
    param([string]$SourceDirectory, [string]$Root, [scriptblock]$StopRunner,
        [scriptblock]$Configure, [scriptblock]$RegisterStartup, [scriptblock]$StartRunner)
    # Reject an incomplete update before stopping or replacing a working installation.
    foreach ($name in Get-ConnectorPackageNames) {
        if ($name -in @('configuration.json', 'configuration.json.example')) { continue }
        if (-not [IO.File]::Exists((Join-Path $SourceDirectory $name))) { Stop-ConnectorOperation 'connector-package-incomplete' }
    }
    # Stop gracefully before updating scripts. Configure must finish before any auto-run registration.
    & $StopRunner
    $configured = & $Configure
    if ($configured -ne 0) { Stop-ConnectorOperation 'setup-not-completed-no-autostart-created' }
    $install = [IO.Path]::GetFullPath((Join-Path $Root 'Connector'))
    [void][IO.Directory]::CreateDirectory($install)
    foreach ($name in Get-ConnectorPackageNames) {
        $source = [IO.Path]::GetFullPath((Join-Path $SourceDirectory $name))
        $destination = Join-Path $install $name
        if ([IO.File]::Exists($source) -and -not $source.Equals($destination, [StringComparison]::OrdinalIgnoreCase)) {
            [IO.File]::Copy($source, $destination, $true)
        }
    }
    if (-not [IO.File]::Exists((Join-Path $install 'Start-OutlookEstimating.ps1'))) { Stop-ConnectorOperation 'connector-package-incomplete' }
    & $RegisterStartup (Get-ConnectorStartupCommand $install)
    & $StartRunner $install
}
function Uninstall-ConnectorPackage {
    param([scriptblock]$StopRunner, [scriptblock]$RemoveStartup)
    & $StopRunner
    & $RemoveStartup
    # Keep configuration, pending queue, backups and scripts for recovery/reinstallation.
}
function Test-ConnectorRunnerIdentity {
    param([hashtable]$Record, [int]$ProcessId, [datetime]$StartedAtUtc)
    return $Record -and $Record.processId -eq $ProcessId -and
        ([datetime]$Record.startedAtUtc).ToUniversalTime() -eq $StartedAtUtc.ToUniversalTime()
}
function Write-ConnectorRunStatus {
    param([string]$Root, [string]$Phase, [AllowNull()][string]$ErrorCode,
        [AllowNull()][hashtable]$Counts = $null)
    if ($Phase -notmatch '^[a-z-]{1,40}$') { Stop-ConnectorOperation 'invalid-status-phase' }
    if ($ErrorCode -and $ErrorCode -notmatch '^[a-z0-9-]{1,100}$') { $ErrorCode = 'operation-failed' }
    if ($null -eq $Counts) {
        $Counts = @{ imported = 0; duplicate = 0; pending = 0 }
        $previous = Read-ConnectorJson (Join-Path $Root 'runner.json')
        if ($previous) {
            foreach ($name in @('imported', 'duplicate', 'pending')) {
                if ($previous.ContainsKey($name)) { $Counts[$name] = [int]$previous[$name] }
            }
        }
    }
    $process = [Diagnostics.Process]::GetCurrentProcess()
    $record = @{ processId = $process.Id; startedAtUtc = $process.StartTime.ToUniversalTime().ToString('o')
        updatedAtUtc = [datetime]::UtcNow.ToString('o'); phase = $Phase; errorCode = $ErrorCode
        imported = [int]$Counts.imported; duplicate = [int]$Counts.duplicate; pending = [int]$Counts.pending }
    Write-ConnectorJson (Join-Path $Root 'runner.json') $record
    $log = Join-Path $Root 'connector.log'
    if ([IO.File]::Exists($log) -and [IO.FileInfo]::new($log).Length -ge 512KB) {
        [IO.File]::Copy($log, "$log.1", $true)
        [IO.File]::WriteAllText($log, '', [Text.UTF8Encoding]::new($false))
    }
    [IO.File]::AppendAllText($log, ($record | ConvertTo-Json -Compress) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
function Start-ConnectorBackground {
    param([string]$InstallDirectory)
    if (Test-ConnectorRunning) { return }
    $script = Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) 'Start-OutlookEstimating.ps1'
    if (-not [IO.File]::Exists($script)) { Stop-ConnectorOperation 'install-connector-first' }
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    Start-Process -FilePath $powershell -WindowStyle Hidden -ArgumentList @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-File', ('"' + $script + '"'), '-Background') | Out-Null
}
