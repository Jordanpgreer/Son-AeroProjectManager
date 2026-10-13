#Requires -Version 5.1
[CmdletBinding()]
param([switch]$Once, [switch]$DryRun, [switch]$SelfTest, [switch]$Setup, [switch]$Diagnose,
    [switch]$ConfigureOnly, [switch]$Background)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'Connector.Core.ps1')
. (Join-Path $PSScriptRoot 'Connector.Outlook.ps1')
. (Join-Path $PSScriptRoot 'Connector.Lifecycle.ps1')
. (Join-Path $PSScriptRoot 'Connector.Session.ps1')
if ($SelfTest) { & (Join-Path $PSScriptRoot 'Test-Connector.ps1'); & (Join-Path $PSScriptRoot 'Test-Lifecycle.ps1'); exit 0 }
if ($Diagnose) { & (Join-Path $PSScriptRoot 'Test-Environment.ps1'); exit $LASTEXITCODE }
$mutex = $null; $ownsMutex = $false; $stopEvent = $null; $root = $null; $exitCode = 0
try {
    if (-not [Environment]::UserInteractive -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) {
        Stop-ConnectorOperation 'run-in-your-normal-interactive-windows-session'
    }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Stop-ConnectorOperation 'run-normally-without-administrator-elevation' }
    if ($Background -and ($Setup -or $ConfigureOnly -or $DryRun -or $Once)) { Stop-ConnectorOperation 'background-mode-cannot-prompt-or-run-once' }
    $mutex = [Threading.Mutex]::new($false, (Get-ConnectorObjectName 'Mutex'))
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { Stop-ConnectorOperation 'another-connector-is-already-running' }
    $stopEvent = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, (Get-ConnectorObjectName 'Stop'))
    [void]$stopEvent.Reset()
    $root = Get-ConnectorLocalRoot
    if (-not $DryRun) { [void][IO.Directory]::CreateDirectory($root) }
    if (-not $Background) {
        Write-Host 'Arda Outlook quote correspondence connector'
        if ($DryRun) { Write-Host 'DRY RUN: no Arda requests or saved state.' }
        elseif (-not $ConfigureOnly) { Write-Host 'Reads Inbox and Sent Items. Ctrl+C or Stop.cmd stops this session.' }
    }
    $attempt = 0
    do {
        try {
            if (-not $DryRun -and -not $ConfigureOnly) { Write-ConnectorRunStatus $root 'connecting' $null }
            $exitCode = Invoke-ConnectorSession $PSScriptRoot $root $stopEvent -Once:$Once -DryRun:$DryRun `
                -Setup:$Setup -ConfigureOnly:$ConfigureOnly -Background:$Background
            break
        } catch {
            if (-not $Background) { throw }
            $attempt++
            $code = Get-ConnectorErrorCode $_ 'outlook-or-local-configuration-unavailable'
            Write-ConnectorRunStatus $root 'reconnecting' $code
            if ($stopEvent.WaitOne((Get-ConnectorBackoffSeconds $attempt) * 1000)) { break }
        }
    } while ($Background)
} catch {
    Write-Host ('Connector stopped: ' + (Get-ConnectorErrorCode $_ 'outlook-or-local-configuration-unavailable')) -ForegroundColor Red
    Write-Host 'Mailbox contents were not changed. Correct the issue and restart; pending work is retained.'
    $exitCode = 1
} finally {
    if ($ownsMutex -and $root -and -not $DryRun -and -not $ConfigureOnly) {
        try { Write-ConnectorRunStatus $root 'stopped' $null } catch { }
    }
    if ($stopEvent) { $stopEvent.Dispose() }
    if ($ownsMutex -and $mutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
exit $exitCode
