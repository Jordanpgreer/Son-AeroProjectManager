#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'Connector.Core.ps1')
. (Join-Path $PSScriptRoot 'Connector.Lifecycle.ps1')
. (Join-Path $PSScriptRoot 'Connector.Session.ps1')
$script:LifecycleAssertions = 0
function Assert-Lifecycle { param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw ('Synthetic lifecycle test failed: ' + $Message) }
    $script:LifecycleAssertions++
}
function Assert-LifecycleFailure { param([scriptblock]$Action, [string]$Code)
    $actual = ''
    try { & $Action | Out-Null } catch { $actual = Get-ConnectorErrorCode $_ }
    Assert-Lifecycle ($actual -eq $Code) ('Expected ' + $Code + '; received ' + $actual)
}
# All startup/launch/stop effects are explicit test stubs; no registry or actual process is touched.
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ArdaConnectorLifecycle-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
try {
    $root = Join-Path $testRoot 'state'
    [void][IO.Directory]::CreateDirectory($root)
    $queue = Join-Path $root 'state-synthetic.json'
    [IO.File]::WriteAllText($queue, 'SYNTHETIC-PENDING-QUEUE')
    $script:Calls = [Collections.Generic.List[string]]::new()
    $script:Startup = @{}
    $stop = { $script:Calls.Add('stop') }
    $configure = { $script:Calls.Add('configure'); return 0 }
    $register = { param($command) $script:Calls.Add('register'); $script:Startup.command = $command }
    $start = { param($folder) $script:Calls.Add('start'); $script:LaunchFolder = $folder }
    Install-ConnectorPackage $PSScriptRoot $root $stop $configure $register $start
    Assert-Lifecycle (($script:Calls -join ',') -eq 'stop,configure,register,start') 'Setup completes before registration or launch.'
    Assert-Lifecycle ([IO.File]::Exists((Join-Path $script:LaunchFolder 'Connector.Session.ps1'))) 'Canonical package is copied to stable user location.'
    Assert-Lifecycle ($script:Startup.command -match '-NonInteractive -WindowStyle Hidden' -and $script:Startup.command -match '-Background$') 'Sign-in startup is hidden and non-interactive.'
    Assert-Lifecycle ($script:Startup.command -notmatch '(?i)Bypass|ExecutionPolicy|RunAs') 'Installer never weakens policy or elevates.'
    Install-ConnectorPackage $PSScriptRoot $root $stop $configure $register $start
    Assert-Lifecycle ($script:Startup.Count -eq 1 -and [IO.File]::ReadAllText($queue) -eq 'SYNTHETIC-PENDING-QUEUE') 'Reinstall is idempotent and preserves pending queue.'
    $script:Calls.Clear()
    Assert-LifecycleFailure { Install-ConnectorPackage $PSScriptRoot $root $stop { $script:Calls.Add('configure'); return 1 } $register $start } 'setup-not-completed-no-autostart-created'
    Assert-Lifecycle (($script:Calls -join ',') -eq 'stop,configure') 'Failed setup never registers or launches.'
    $script:Calls.Clear()
    Assert-LifecycleFailure { Install-ConnectorPackage $testRoot $root $stop $configure $register $start } 'connector-package-incomplete'
    Assert-Lifecycle ($script:Calls.Count -eq 0) 'Incomplete update does not interrupt a working package.'
    Uninstall-ConnectorPackage $stop { $script:Startup.Clear() }
    Assert-Lifecycle ($script:Startup.Count -eq 0 -and [IO.File]::ReadAllText($queue) -eq 'SYNTHETIC-PENDING-QUEUE') 'Uninstall removes startup while preserving queued work.'
    Assert-Lifecycle ((Get-ConnectorBackoffSeconds 1) -eq 30 -and (Get-ConnectorBackoffSeconds 20) -eq 300) 'Reconnect retry uses bounded backoff.'
    $identity = @{ processId = 123; startedAtUtc = '2026-10-12T12:00:00Z' }
    Assert-Lifecycle (Test-ConnectorRunnerIdentity $identity 123 ([datetime]'2026-10-12T12:00:00Z')) 'Status recognizes exact process identity.'
    Assert-Lifecycle (-not (Test-ConnectorRunnerIdentity $identity 123 ([datetime]'2026-10-12T13:00:00Z'))) 'Reused process ID is not mistaken for the runner.'
    Write-ConnectorRunStatus $root 'polling' 'example-safe-code' @{ imported = 1; duplicate = 2; pending = 3 }
    $log = [IO.File]::ReadAllText((Join-Path $root 'connector.log'))
    Assert-Lifecycle ($log -notmatch 'subject|body|mailbox|example\.test' -and $log -match 'example-safe-code') 'Logs contain metadata and safe codes only.'
    [IO.File]::WriteAllText((Join-Path $root 'connector.log'), ('x' * 512KB))
    Write-ConnectorRunStatus $root 'reconnecting' 'SYNTHETIC secret@example.test body'
    Assert-Lifecycle ((Read-ConnectorJson (Join-Path $root 'runner.json')).pending -eq 3) 'Reconnect/stopped metadata retains the last known queue count.'
    Assert-Lifecycle ([IO.File]::Exists((Join-Path $root 'connector.log.1')) -and [IO.FileInfo]::new((Join-Path $root 'connector.log')).Length -lt 10KB) 'Logs rotate at the bound.'
    Assert-Lifecycle ([IO.File]::ReadAllText((Join-Path $root 'connector.log')) -notmatch 'secret@example.test|SYNTHETIC') 'Unexpected error text is never logged.'

    # COM boundary, account discovery, prompts and transport are all replaced below.
    function Open-ConnectorOutlook {
        param([switch]$Background)
        $script:Opened++
        $fake = [pscustomobject]@{}
        $fake | Add-Member ScriptMethod GetNamespace { param($name) return [pscustomobject]@{} }
        return $fake
    }
    function Get-OutlookAccountChoices { param($namespace) return @{ index = 1; smtp = 'owner@example.test' } }
    function Get-OutlookMailboxFolders { throw 'Configure-only must not read folders.' }
    function Invoke-ConnectorRequest { throw 'Configure-only must not contact Arda.' }
    function Release-OutlookReference { param($value) }
    function Read-Host { param($Prompt) $script:Prompts++; return '' }
    $script:Opened = 0; $script:Prompts = 0
    $event = [Threading.ManualResetEvent]::new($false)
    try {
        $sessionRoot = Join-Path $testRoot 'session'
        [void][IO.Directory]::CreateDirectory($sessionRoot)
        Assert-LifecycleFailure { Invoke-ConnectorSession $PSScriptRoot $sessionRoot $event -Background } 'setup-required-run-install-cmd'
        Assert-Lifecycle ($script:Opened -eq 0 -and $script:Prompts -eq 0) 'Unconfigured background run never opens Outlook or prompts.'
        $result = Invoke-ConnectorSession $PSScriptRoot $sessionRoot $event -ConfigureOnly
        Assert-Lifecycle ($result -eq 0 -and $script:Prompts -eq 1) 'Configure-only chooses account and saves after confirmation.'
        Assert-Lifecycle (@(Get-ChildItem -LiteralPath $sessionRoot -Filter 'state-*').Count -eq 0) 'Configure-only never creates delivery state.'
        $result = Invoke-ConnectorSession $PSScriptRoot $sessionRoot $event -ConfigureOnly
        Assert-Lifecycle ($result -eq 0 -and $script:Prompts -eq 2) 'Reinstall reconfirms even an existing saved address.'
    } finally { $event.Dispose() }
} finally {
    # This is the unique directory created above, resolved and checked before recursive cleanup.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'ArdaConnectorLifecycle-*') {
        throw 'Unsafe synthetic test cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Host ("PASS: $script:LifecycleAssertions synthetic lifecycle assertions; no registry, actual startup, Outlook or network used.")
