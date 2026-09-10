#Requires -Version 5.1
[CmdletBinding()]
param([switch]$Once, [switch]$DryRun, [switch]$SelfTest, [switch]$Setup)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'Connector.Core.ps1')
. (Join-Path $PSScriptRoot 'Connector.Outlook.ps1')
if ($SelfTest) { & (Join-Path $PSScriptRoot 'Test-Connector.ps1'); exit 0 }

$mutex = $null; $ownsMutex = $false
$outlook = $null; $namespace = $null; $folders = $null; $state = $null; $statePath = $null
try {
    if (-not [Environment]::UserInteractive -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) {
        Stop-ConnectorOperation 'run-in-your-normal-interactive-windows-session'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Stop-ConnectorOperation 'run-normally-without-administrator-elevation' }
    $mutexName = 'Local\Arda-OutlookQuoteConnector-' + (Get-ConnectorHash $identity.User.Value).Substring(0, 24)
    $mutex = [Threading.Mutex]::new($false, $mutexName)
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { Stop-ConnectorOperation 'another-connector-is-already-running' }

    Write-Host 'Arda Outlook quote correspondence connector'
    Write-Host 'Reads your Inbox and Sent Items. Press Ctrl+C to stop.'
    if ($DryRun) { Write-Host 'DRY RUN: no Arda requests and no delivery-state changes.' -ForegroundColor Yellow }
    $localRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Arda\OutlookEstimating'
    [void][IO.Directory]::CreateDirectory($localRoot)
    $packaged = Read-ConnectorJson (Join-Path $PSScriptRoot 'configuration.json')
    if (-not $packaged) { $packaged = Read-ConnectorJson (Join-Path $PSScriptRoot 'configuration.json.example') }
    if (-not $packaged) { $packaged = @{} }
    $defaults = Get-ConnectorConfiguration $packaged
    $configurationPath = Join-Path $localRoot ('configuration-' + (Get-ConnectorHash $defaults.baseUrl).Substring(0, 20) + '.json')
    $stored = Read-ConnectorJson $configurationPath
    $config = $defaults
    if ($stored) { $config = Get-ConnectorConfiguration $stored }

    if (-not [Type]::GetTypeFromProgID('Outlook.Application')) { Stop-ConnectorOperation 'classic-outlook-is-not-installed' }
    try { $outlook = [Runtime.InteropServices.Marshal]::GetActiveObject('Outlook.Application') }
    catch { $outlook = New-Object -ComObject Outlook.Application }
    $namespace = $outlook.GetNamespace('MAPI')
    $choices = @(Get-OutlookAccountChoices $namespace)
    if ($choices.Count -eq 0) { Stop-ConnectorOperation 'no-configured-own-outlook-account-with-smtp-address' }
    if ($Setup -or -not $stored -or -not $config.mailbox) {
        Write-Host ''
        Write-Host ('Arda address: ' + $config.baseUrl)
        $enteredUrl = Read-Host 'Press Enter to use this HTTPS Arda address, or enter the correct address'
        if ($enteredUrl.Trim()) { $config.baseUrl = $enteredUrl.Trim() }
        $config = Get-ConnectorConfiguration $config
        if ($choices.Count -eq 1) { $config.mailbox = $choices[0].smtp }
        else {
            Write-Host 'Outlook has multiple accounts. Choose your own mailbox explicitly:'
            foreach ($choice in $choices) { Write-Host ('  ' + $choice.smtp) }
            $config.mailbox = (Read-Host 'Enter the mailbox SMTP address').Trim().ToLowerInvariant()
        }
    }
    $selected = @($choices | Where-Object { $_.smtp -eq $config.mailbox })
    if ($selected.Count -ne 1) { Stop-ConnectorOperation 'configured-mailbox-must-match-one-own-outlook-account-run-setup' }
    if (-not $DryRun) { Write-ConnectorJson $configurationPath $config }
    Write-Host ('Mailbox: ' + $config.mailbox)
    Write-Host ('Arda: ' + $config.baseUrl)
    Write-Host ('Lookback: ' + $config.initialLookbackDays + ' days; poll interval: ' + $config.pollIntervalSeconds + ' seconds.')
    $folders = Get-OutlookMailboxFolders $namespace $selected[0].index
    $statePath = Join-Path $localRoot ('state-' + (Get-ConnectorHash ($config.baseUrl + '|' + $config.mailbox)).Substring(0, 24) + '.json')
    $state = Read-ConnectorJson $statePath
    if (-not $state) { $state = New-ConnectorState $config }
    if ($state.schemaVersion -ne 1 -or $state.mailbox -ne $config.mailbox -or $state.baseUrlHash -ne (Get-ConnectorHash $config.baseUrl)) {
        Stop-ConnectorOperation 'state-does-not-match-configuration'
    }
    $reader = {
        param($record)
        $mail = $null
        try {
            $mail = $namespace.GetItemFromID($record.entryId, $record.storeId)
            return Convert-OutlookMail $mail $record $config.mailbox
        } finally { Release-OutlookReference $mail }
    }
    $importer = { param($payload) Invoke-ConnectorRequest $config 'import' $payload }
    $scanner = { param($direction, $cursor, $limit) Get-OutlookScanBatch $folders[$direction] $cursor $direction $limit }

    do {
        $now = [datetime]::UtcNow
        $counts = @{ imported = 0; duplicate = 0; deferred = 0; ready = 0 }
        $cycleError = $null
        $scan = Invoke-ConnectorScanCycle $state $config $scanner $now
        if ($scan.errors.Count -gt 0) { $cycleError = $scan.errors[0] }
        if (-not $DryRun) { Write-ConnectorJson $statePath $state }
        foreach ($record in @(Get-ConnectorDueRecords $state $config $now)) {
            $result = Invoke-ConnectorDelivery $state $record $reader $importer $now -DryRun:$DryRun
            foreach ($key in @('imported', 'duplicate', 'deferred', 'ready')) { $counts[$key] += $result[$key] }
            if ($result.errorCode) {
                $cycleError = $result.errorCode
                Write-Host ('Deferred ' + $record.key.Substring(0, 10) + ': ' + $result.errorCode + ' (retained for retry).') -ForegroundColor Yellow
            }
            if (-not $DryRun) { Write-ConnectorJson $statePath $state }
            # One unavailable server must not cause a timeout for every queued message.
            if (Test-ConnectorTransportPaused $result.errorCode) { break }
        }
        if (-not $DryRun) {
            $retainAfter = $now.AddDays(-[Math]::Max(45, $config.initialLookbackDays + 7))
            foreach ($key in @($state.completed.Keys)) { if ([datetime]$state.completed[$key] -lt $retainAfter) { $state.completed.Remove($key) } }
            Write-ConnectorJson $statePath $state
            try {
                if (Test-ConnectorTransportPaused $cycleError) { Stop-ConnectorOperation $cycleError }
                [void](Invoke-ConnectorRequest $config 'sync' @{
                    mailbox = $config.mailbox; clientName = 'Classic Outlook local connector 1.0'
                    importedCount = $counts.imported; duplicateCount = $counts.duplicate
                    deferredCount = $state.pending.Count; error = $cycleError
                })
            } catch { $cycleError = Get-ConnectorErrorCode $_ 'heartbeat-failed' }
        }
        Write-Host (('{0} | imported {1}, duplicate {2}, pending {3}, dry-run ready {4}' -f (Get-Date -Format 'HH:mm:ss'), $counts.imported, $counts.duplicate, $state.pending.Count, $counts.ready))
        if ($cycleError) { Write-Host ('Attention: ' + $cycleError + '. No failed message was marked complete.') -ForegroundColor Yellow }
        if ($Once) { break }
        for ($second = 0; $second -lt $config.pollIntervalSeconds; $second++) { Start-Sleep -Seconds 1 }
    } while ($true)
    if ($cycleError -or $counts.deferred -gt 0) { exit 2 }
} catch {
    Write-Host ('Connector stopped: ' + (Get-ConnectorErrorCode $_ 'outlook-or-local-configuration-unavailable')) -ForegroundColor Red
    Write-Host 'Mailbox contents were not changed. Correct the issue and restart; pending work is retained.'
    exit 1
} finally {
    if ($folders) { foreach ($folder in $folders.Values) { Release-OutlookReference $folder } }
    Release-OutlookReference $namespace
    Release-OutlookReference $outlook
    # Do not Quit Outlook: this is the user's existing desktop session.
    if ($ownsMutex -and $mutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
