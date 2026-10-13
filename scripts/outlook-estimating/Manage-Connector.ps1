#Requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Install', 'Start', 'Stop', 'Status', 'Uninstall')][string]$Action)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'Connector.Core.ps1')
. (Join-Path $PSScriptRoot 'Connector.Lifecycle.ps1')
try {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Stop-ConnectorOperation 'run-normally-without-administrator-elevation' }
    $root = Get-ConnectorLocalRoot
    $installed = Join-Path $root 'Connector'
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runName = 'ArdaOutlookEstimating'
    $stop = { Request-ConnectorStop; Wait-ConnectorStopped }
    switch ($Action) {
        'Install' {
            Write-Host 'Set up Arda correspondence tracking for your Windows account.'
            Write-Host 'After setup completes, the connector starts in the background and at Windows sign-in.'
            $configure = {
                & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoLogo -NoProfile `
                    -File (Join-Path $PSScriptRoot 'Start-OutlookEstimating.ps1') -ConfigureOnly | Out-Host
                return $LASTEXITCODE
            }
            $register = { param($command)
                if (-not (Test-Path -LiteralPath $runKey)) { [void](New-Item -Path $runKey -Force) }
                [void](New-ItemProperty -LiteralPath $runKey -Name $runName -Value $command -PropertyType String -Force)
            }
            Install-ConnectorPackage $PSScriptRoot $root $stop $configure $register { param($folder) Start-ConnectorBackground $folder }
            Write-Host 'Installed for this Windows account. Keep classic Outlook open and signed in.'
            Write-Host 'Use Status.cmd to check progress or Stop.cmd to pause. No message was sent by Arda.'
        }
        'Start' { Start-ConnectorBackground $installed; Write-Host 'Background start requested. Use Status.cmd to check progress.' }
        'Stop' { Request-ConnectorStop; Write-Host 'Stop requested. The connector finishes its current operation, then exits. Outlook remains open.' }
        'Status' {
            $configured = (Test-Path -LiteralPath $runKey) -and $null -ne (Get-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction SilentlyContinue)
            Write-Host ('Start at sign-in: ' + $(if ($configured) { 'enabled' } else { 'disabled' }))
            Write-Host ('Connector: ' + $(if (Test-ConnectorRunning) { 'running' } else { 'stopped' }))
            $record = Read-ConnectorJson (Join-Path $root 'runner.json')
            if ($record) {
                $process = Get-Process -Id ([int]$record.processId) -ErrorAction SilentlyContinue
                $same = $process -and (Test-ConnectorRunnerIdentity $record $process.Id $process.StartTime)
                Write-Host ('Last check: ' + $record.updatedAtUtc + $(if ($same) { '' } else { ' (previous run)' }))
                Write-Host ('Phase: ' + $record.phase + '; pending: ' + $record.pending)
                if ($record.errorCode) { Write-Host ('Attention: ' + $record.errorCode) }
            } else { Write-Host 'No run status recorded yet.' }
            Write-Host 'For network/setup checks: Start.cmd -Diagnose'
        }
        'Uninstall' {
            Uninstall-ConnectorPackage $stop {
                if (Test-Path -LiteralPath $runKey) { Remove-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction SilentlyContinue }
            }
            Write-Host 'Background connector stopped and sign-in start removed. Saved setup, pending queue and backups were preserved.'
        }
    }
} catch { Write-Host ('Connector action failed: ' + (Get-ConnectorErrorCode $_ 'local-connector-action-failed')) -ForegroundColor Red; exit 1 }
exit 0
