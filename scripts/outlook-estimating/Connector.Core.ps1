# Pure configuration, state and delivery functions. No Outlook or network starts on import.
function Stop-ConnectorOperation {
    param([string]$Code)
    $failure = [InvalidOperationException]::new($Code)
    $failure.Data['ConnectorCode'] = $Code
    throw $failure
}

function Get-ConnectorErrorCode {
    param($Failure, [string]$Fallback = 'operation-failed')
    $exception = $Failure.Exception
    while ($exception) {
        if ($exception.Data.Contains('ConnectorCode')) { return [string]$exception.Data['ConnectorCode'] }
        $exception = $exception.InnerException
    }
    return $Fallback
}

function Get-ConnectorHash {
    param([string]$Value)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Test-ConnectorTransportPaused {
    param([AllowNull()][string]$Code)
    return $Code -in @('arda-unreachable-or-server-error', 'windows-authentication-required', 'server-redirect-not-allowed', 'server-rate-limit')
}

function Get-QuoteNumberFromSubject {
    param([AllowNull()][string]$Subject)
    if ([string]::IsNullOrWhiteSpace($Subject)) { return $null }
    $match = [regex]::Match($Subject, '^\s*(?:(?:RE|FW|FWD)\s*:\s*)*Quote\s+([0-9]+)\s*$', 'IgnoreCase,CultureInvariant')
    $number = 0
    if ($match.Success -and [int]::TryParse($match.Groups[1].Value, [ref]$number) -and $number -gt 0) { return $number }
    return $null
}

function ConvertTo-ConnectorMap {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [Collections.IDictionary]) {
        $result = @{}
        foreach ($key in $Value.Keys) { $result[$key] = ConvertTo-ConnectorMap $Value[$key] }
        return $result
    }
    if ($Value -is [pscustomobject]) {
        $result = @{}
        foreach ($property in $Value.PSObject.Properties) { $result[$property.Name] = ConvertTo-ConnectorMap $property.Value }
        return $result
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        $result = @($Value | ForEach-Object { ConvertTo-ConnectorMap $_ })
        return ,$result
    }
    return $Value
}

function Get-ConnectorConfiguration {
    param([hashtable]$Values = @{})
    $config = @{
        baseUrl = 'https://estimating.hub.son4l.local'; mailbox = ''
        pollIntervalSeconds = 120; initialLookbackDays = 30
        maxMessagesPerPoll = 100; maxRetriesPerPoll = 25; requestTimeoutSeconds = 60
    }
    foreach ($key in @($config.Keys)) { if ($Values.ContainsKey($key)) { $config[$key] = $Values[$key] } }
    $uri = $null
    if (-not [Uri]::TryCreate([string]$config.baseUrl, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { Stop-ConnectorOperation 'configuration-requires-https-arda-url' }
    $config.baseUrl = $uri.AbsoluteUri.TrimEnd('/')
    $config.mailbox = ([string]$config.mailbox).Trim().ToLowerInvariant()
    $bounds = @{ pollIntervalSeconds = @(30, 900); initialLookbackDays = @(1, 90); maxMessagesPerPoll = @(4, 500); maxRetriesPerPoll = @(1, 200); requestTimeoutSeconds = @(10, 180) }
    foreach ($key in $bounds.Keys) {
        $number = 0
        if (-not [int]::TryParse([string]$config[$key], [ref]$number) -or $number -lt $bounds[$key][0] -or $number -gt $bounds[$key][1]) { Stop-ConnectorOperation "configuration-invalid-$key" }
        $config[$key] = $number
    }
    return $config
}

function Read-ConnectorJson {
    param([string]$Path)
    if (-not [IO.File]::Exists($Path)) { return $null }
    try {
        if ([IO.FileInfo]::new($Path).Length -gt 20MB) { Stop-ConnectorOperation 'state-file-too-large' }
        return ConvertTo-ConnectorMap ([IO.File]::ReadAllText($Path) | ConvertFrom-Json)
    } catch { Stop-ConnectorOperation 'configuration-or-state-unreadable' }
}

function Write-ConnectorJson {
    param([string]$Path, [hashtable]$Value)
    $temporary = "$Path.new"
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, "$Path.bak") }
    else { [IO.File]::Move($temporary, $Path) }
}

function New-ConnectorState {
    param([hashtable]$Config, [datetime]$Now = [datetime]::UtcNow)
    $folders = @{}
    foreach ($direction in @('incoming', 'outgoing')) {
        $folders[$direction] = @{
            nextIndex = 1; windowStartUtc = $Now.AddDays(-$Config.initialLookbackDays).ToString('o'); windowEndUtc = $Now.ToString('o'); lastFullScanUtc = $null
            live = @{ nextIndex = 1; windowStartUtc = $Now.AddMinutes(-5).ToString('o'); windowEndUtc = $Now.ToString('o'); lastFullScanUtc = $null }
        }
    }
    return @{ schemaVersion = 1; mailbox = $Config.mailbox; baseUrlHash = Get-ConnectorHash $Config.baseUrl; folders = $folders; pending = @{}; completed = @{} }
}

function Add-ConnectorCandidate {
    param([hashtable]$State, [hashtable]$Candidate, [datetime]$Now = [datetime]::UtcNow)
    $key = Get-ConnectorHash ($Candidate.direction + '|' + $Candidate.sourceMessageId)
    if ($State.completed.ContainsKey($key)) { return $false }
    if ($State.pending.ContainsKey($key)) {
        # A later scan can locate a moved item without discarding its delivery acknowledgements.
        $State.pending[$key].entryId = $Candidate.entryId
        $State.pending[$key].storeId = $Candidate.storeId
        if ($Candidate.ContainsKey('priority') -and $Candidate.priority -eq 0) { $State.pending[$key].priority = 0 }
        return $false
    }
    if ($State.pending.Count -ge 25000) { Stop-ConnectorOperation 'pending-queue-full-no-items-discarded' }
    $State.pending[$key] = @{
        key = $key; entryId = $Candidate.entryId; storeId = $Candidate.storeId
        direction = $Candidate.direction; sourceMessageId = $Candidate.sourceMessageId
        conversationId = $Candidate.conversationId; discoveredAtUtc = $Now.ToString('o')
        priority = $(if ($Candidate.ContainsKey('priority')) { $Candidate.priority } else { 10 })
        attempts = 0; nextAttemptUtc = $Now.ToString('o'); lastErrorCode = $null; acknowledgedVendors = @{}
    }
    return $true
}

function Set-ConnectorDeferred {
    param([hashtable]$Record, [string]$Code, [datetime]$Now = [datetime]::UtcNow)
    $Record.attempts = [int]$Record.attempts + 1
    $delay = [Math]::Min(3600, 30 * [Math]::Pow(2, [Math]::Min(7, $Record.attempts - 1)))
    $Record.nextAttemptUtc = $Now.AddSeconds($delay).ToString('o')
    $Record.lastErrorCode = $Code
}

function Get-ConnectorDueRecords {
    param([hashtable]$State, [hashtable]$Config, [datetime]$Now = [datetime]::UtcNow)
    $due = @($State.pending.Values | Where-Object { [datetime]$_.nextAttemptUtc -le $Now })
    return @($due | Where-Object { $_.attempts -eq 0 } | Sort-Object priority, discoveredAtUtc | Select-Object -First $Config.maxMessagesPerPoll) +
        @($due | Where-Object { $_.attempts -gt 0 } | Sort-Object nextAttemptUtc, discoveredAtUtc | Select-Object -First $Config.maxRetriesPerPoll)
}

function Invoke-ConnectorScanCycle {
    param([hashtable]$State, [hashtable]$Config, [scriptblock]$ScanBatch, [datetime]$Now = [datetime]::UtcNow)
    $errors = [Collections.Generic.List[string]]::new()
    $added = 0
    $limit = [Math]::Max(1, [int][Math]::Floor($Config.maxMessagesPerPoll / 4))
    # A busy historical Inbox never takes the budget reserved for new arrivals.
    foreach ($kind in @('live', 'background')) {
        foreach ($direction in @('incoming', 'outgoing')) {
            $folderCursor = $State.folders[$direction]
            if (-not $folderCursor.ContainsKey('live')) {
                $folderCursor.live = @{ nextIndex = 1; windowStartUtc = $Now.AddMinutes(-5).ToString('o'); windowEndUtc = $Now.ToString('o'); lastFullScanUtc = $null }
            }
            $cursor = $folderCursor
            if ($kind -eq 'live') { $cursor = $folderCursor.live }
            try {
                $batch = & $ScanBatch $direction $cursor $limit
                foreach ($candidate in $batch.entries) {
                    $candidate.priority = $(if ($kind -eq 'live') { 0 } else { 10 })
                    if (Add-ConnectorCandidate $State $candidate $Now) { $added++ }
                }
                # Queue every discovered candidate before advancing the scan continuation.
                $cursor.nextIndex = $batch.nextIndex
                if ($batch.complete) {
                    $previousEnd = [datetime]$cursor.windowEndUtc
                    $cursor.nextIndex = 1; $cursor.lastFullScanUtc = $Now.ToString('o')
                    if ($kind -eq 'live') { $cursor.windowStartUtc = $previousEnd.AddMinutes(-2).ToString('o') }
                    else { $cursor.windowStartUtc = $Now.AddDays(-$Config.initialLookbackDays).ToString('o') }
                    $cursor.windowEndUtc = $Now.ToString('o')
                }
                if ($batch.errorCode) { $errors.Add([string]$batch.errorCode) }
            } catch { $errors.Add((Get-ConnectorErrorCode $_ 'outlook-scan-failed')) }
        }
    }
    return @{ added = $added; errors = $errors.ToArray() }
}

function Invoke-ConnectorDelivery {
    param([hashtable]$State, [hashtable]$Record, [scriptblock]$ReadMessage, [scriptblock]$ImportMessage,
        [datetime]$Now = [datetime]::UtcNow, [switch]$DryRun)
    $counts = @{ imported = 0; duplicate = 0; deferred = 0; ready = 0; errorCode = $null }
    try {
        $message = & $ReadMessage $Record
        if ($null -eq $message) { Stop-ConnectorOperation 'outlook-message-unavailable' }
        if (-not (Get-QuoteNumberFromSubject $message.subject)) { Stop-ConnectorOperation 'subject-no-longer-matches' }
        $failure = $null
        foreach ($vendor in $message.vendorAddresses) {
            $vendorKey = Get-ConnectorHash $vendor
            if ($Record.acknowledgedVendors.ContainsKey($vendorKey)) { continue }
            if ($DryRun) { $counts.ready++; continue }
            $payload = @{
                sourceMessageId = $Record.sourceMessageId; mailbox = $State.mailbox; direction = $Record.direction
                subject = $message.subject; fromAddress = $message.fromAddress; fromName = $message.fromName
                toAddresses = @($message.toAddresses); vendorEmail = $vendor; vendorName = $null
                sentAt = $message.sentAt; receivedAt = $message.receivedAt; bodyText = $message.bodyText
                conversationId = $Record.conversationId; attachments = @($message.attachments)
            }
            try {
                $response = & $ImportMessage $payload
                if ($response.outcome -in @('imported', 'unassigned', 'duplicate')) {
                    $Record.acknowledgedVendors[$vendorKey] = $Now.ToString('o')
                    if ($response.outcome -eq 'duplicate') { $counts.duplicate++ } else { $counts.imported++ }
                } elseif ($response.outcome -in @('unmatched', 'ambiguous')) { $failure = 'quote-' + $response.outcome }
                else { $failure = 'import-unrecognized-response' }
            } catch {
                $failure = Get-ConnectorErrorCode $_ 'import-failed'
                if (Test-ConnectorTransportPaused $failure) { break }
            }
        }
        if ($DryRun) { return $counts }
        if ($failure) { Set-ConnectorDeferred $Record $failure $Now; $counts.deferred = 1; $counts.errorCode = $failure }
        else {
            $State.completed[$Record.key] = $Now.ToString('o')
            $State.pending.Remove($Record.key)
        }
    } catch {
        $code = Get-ConnectorErrorCode $_ 'outlook-message-read-failed'
        if (-not $DryRun) { Set-ConnectorDeferred $Record $code $Now }
        $counts.deferred = 1; $counts.errorCode = $code
    }
    return $counts
}

function Invoke-ConnectorRequest {
    param([hashtable]$Config, [ValidateSet('import', 'sync')][string]$Operation, [hashtable]$Payload)
    $json = $Payload | ConvertTo-Json -Depth 12 -Compress
    try {
        # No redirects: Windows credentials and correspondence must stay on the chosen HTTPS endpoint.
        return Invoke-RestMethod -Uri ($Config.baseUrl + '/api/vendor-quotes/' + $Operation) -Method Post -UseDefaultCredentials `
            -Headers @{ 'X-Requested-With' = 'XMLHttpRequest' } -ContentType 'application/json; charset=utf-8' `
            -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec $Config.requestTimeoutSeconds -MaximumRedirection 0 -ErrorAction Stop
    } catch {
        $status = 0
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) { try { $status = [int]$_.Exception.Response.StatusCode } catch { $status = 0 } }
        $code = switch ($status) {
            400 { 'import-payload-rejected' } 401 { 'windows-authentication-required' } 403 { 'quote-access-denied' }
            409 { 'import-conflict-retry' } 413 { 'server-request-limit' } 429 { 'server-rate-limit' }
            default { if ($status -ge 300 -and $status -lt 400) { 'server-redirect-not-allowed' } else { 'arda-unreachable-or-server-error' } }
        }
        Stop-ConnectorOperation $code
    }
}
