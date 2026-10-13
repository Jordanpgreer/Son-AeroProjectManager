# One Outlook connection; the background host reconnects when this session becomes unavailable.
function Open-ConnectorOutlook {
    param([switch]$Background)
    if (-not [Type]::GetTypeFromProgID('Outlook.Application')) { Stop-ConnectorOperation 'classic-outlook-is-not-installed' }
    try { return [Runtime.InteropServices.Marshal]::GetActiveObject('Outlook.Application') }
    catch {
        # A hidden worker must never activate a profile/account prompt at sign-in.
        if ($Background) { Stop-ConnectorOperation 'outlook-not-running-open-classic-outlook' }
        return New-Object -ComObject Outlook.Application
    }
}
function Invoke-ConnectorSession {
    param([string]$PackageRoot, [string]$Root, $StopEvent, [switch]$Once, [switch]$DryRun,
        [switch]$Setup, [switch]$ConfigureOnly, [switch]$Background)
    $outlook = $null; $namespace = $null; $folders = $null
    try {
        $packaged = Read-ConnectorJson (Join-Path $PackageRoot 'configuration.json')
        if (-not $packaged) { $packaged = Read-ConnectorJson (Join-Path $PackageRoot 'configuration.json.example') }
        if (-not $packaged) { $packaged = @{} }
        $defaults = Get-ConnectorConfiguration $packaged
        $configurationPath = Join-Path $Root ('configuration-' + (Get-ConnectorHash $defaults.baseUrl).Substring(0, 20) + '.json')
        $stored = Read-ConnectorJson $configurationPath
        $config = $defaults
        if ($stored) { $config = Get-ConnectorConfiguration $stored }
        if ($Background -and (-not $stored -or -not $config.mailbox)) { Stop-ConnectorOperation 'setup-required-run-install-cmd' }
        $outlook = Open-ConnectorOutlook -Background:$Background
        $namespace = $outlook.GetNamespace('MAPI')
        $choices = @(Get-OutlookAccountChoices $namespace)
        if ($choices.Count -eq 0) { Stop-ConnectorOperation 'no-configured-own-outlook-account-with-smtp-address' }
        if (-not $Background -and ($Setup -or $ConfigureOnly -or -not $stored -or -not $config.mailbox)) {
            Write-Host ('Arda address: ' + $config.baseUrl)
            $enteredUrl = Read-Host 'Press Enter to use this HTTPS Arda address, or enter the correct address'
            if ($enteredUrl.Trim()) { $config.baseUrl = $enteredUrl.Trim() }
            $config = Get-ConnectorConfiguration $config
            if ($choices.Count -eq 1) { $config.mailbox = $choices[0].smtp }
            else {
                Write-Host 'Choose your own Outlook mailbox:'
                foreach ($choice in $choices) { Write-Host ('  ' + $choice.smtp) }
                $config.mailbox = (Read-Host 'Enter the mailbox SMTP address').Trim().ToLowerInvariant()
            }
        }
        $selected = @($choices | Where-Object { $_.smtp -eq $config.mailbox })
        if ($selected.Count -ne 1) { Stop-ConnectorOperation 'configured-mailbox-must-match-one-own-outlook-account-run-setup' }
        if (-not $DryRun) { Write-ConnectorJson $configurationPath $config }
        # Configure-only deliberately returns before accessing folders, creating state or making requests.
        if ($ConfigureOnly) { Write-Host 'Setup saved. No correspondence was scanned or imported.'; return 0 }
        if (-not $Background) { Write-Host ('Mailbox: ' + $config.mailbox); Write-Host ('Arda: ' + $config.baseUrl) }
        $folders = Get-OutlookMailboxFolders $namespace $selected[0].index
        $statePath = Join-Path $Root ('state-' + (Get-ConnectorHash ($config.baseUrl + '|' + $config.mailbox)).Substring(0, 24) + '.json')
        $state = Read-ConnectorJson $statePath
        if (-not $state) { $state = New-ConnectorState $config }
        if ($state.schemaVersion -ne 1 -or $state.mailbox -ne $config.mailbox -or $state.baseUrlHash -ne (Get-ConnectorHash $config.baseUrl)) {
            Stop-ConnectorOperation 'state-does-not-match-configuration'
        }
        $reader = { param($record)
            $mail = $null
            try { $mail = $namespace.GetItemFromID($record.entryId, $record.storeId); return Convert-OutlookMail $mail $record $config.mailbox }
            finally { Release-OutlookReference $mail }
        }
        $importer = { param($payload) Invoke-ConnectorRequest $config 'import' $payload }
        $scanner = { param($direction, $cursor, $limit) Get-OutlookScanBatch $folders[$direction] $cursor $direction $limit }
        do {
            if ($StopEvent.WaitOne(0)) { return 0 }
            $now = [datetime]::UtcNow
            $counts = @{ imported = 0; duplicate = 0; deferred = 0; ready = 0 }
            $cycleError = $null
            $scan = Invoke-ConnectorScanCycle $state $config $scanner $now
            $connectionLost = @($scan.errors | Where-Object { $_ -in @('outlook-folder-unavailable', 'outlook-scan-failed', 'outlook-scan-read-failed') }).Count -gt 0
            if ($scan.errors.Count -gt 0) { $cycleError = $scan.errors[0] }
            if (-not $DryRun) { Write-ConnectorJson $statePath $state }
            foreach ($record in @(Get-ConnectorDueRecords $state $config $now)) {
                if ($StopEvent.WaitOne(0)) { break }
                $result = Invoke-ConnectorDelivery $state $record $reader $importer $now -DryRun:$DryRun
                foreach ($key in @('imported', 'duplicate', 'deferred', 'ready')) { $counts[$key] += $result[$key] }
                if ($result.errorCode) { $cycleError = $result.errorCode }
                if (-not $DryRun) { Write-ConnectorJson $statePath $state }
                if (Test-ConnectorTransportPaused $result.errorCode) { break }
            }
            if (-not $DryRun) {
                $retainAfter = $now.AddDays(-[Math]::Max(45, $config.initialLookbackDays + 7))
                foreach ($key in @($state.completed.Keys)) { if ([datetime]$state.completed[$key] -lt $retainAfter) { $state.completed.Remove($key) } }
                Write-ConnectorJson $statePath $state
                if (-not $StopEvent.WaitOne(0)) {
                    try {
                        if (Test-ConnectorTransportPaused $cycleError) { Stop-ConnectorOperation $cycleError }
                        [void](Invoke-ConnectorRequest $config 'sync' @{
                            mailbox = $config.mailbox; clientName = 'Classic Outlook local connector 1.0'
                            importedCount = $counts.imported; duplicateCount = $counts.duplicate
                            deferredCount = $state.pending.Count; error = $cycleError
                        })
                    } catch { $cycleError = Get-ConnectorErrorCode $_ 'heartbeat-failed' }
                }
                Write-ConnectorRunStatus $Root 'polling' $cycleError @{
                    imported = $counts.imported; duplicate = $counts.duplicate; pending = $state.pending.Count }
            }
            if (-not $Background) {
                Write-Host (('Imported {0}, duplicate {1}, pending {2}, dry-run ready {3}' -f $counts.imported, $counts.duplicate, $state.pending.Count, $counts.ready))
                if ($cycleError) { Write-Host ('Attention: ' + $cycleError + '. Pending work is retained.') }
            }
            if ($Once) { if ($cycleError -or $counts.deferred -gt 0) { return 2 }; return 0 }
            if ($Background -and $connectionLost) {
                Stop-ConnectorOperation 'outlook-connection-lost-reconnecting'
            }
            if ($StopEvent.WaitOne([int]$config.pollIntervalSeconds * 1000)) { return 0 }
        } while ($true)
    } finally {
        if ($folders) { foreach ($folder in $folders.Values) { Release-OutlookReference $folder } }
        Release-OutlookReference $namespace
        Release-OutlookReference $outlook
        # Never Quit Outlook: it belongs to the user's desktop session.
    }
}
