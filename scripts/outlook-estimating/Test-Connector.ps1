#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'Connector.Core.ps1')
. (Join-Path $PSScriptRoot 'Connector.Outlook.ps1')
$script:ConnectorAssertions = 0

function Assert-Connector {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Synthetic test failed: $Message" }
    $script:ConnectorAssertions++
}
function Assert-ConnectorFailure {
    param([scriptblock]$Action, [string]$Expected)
    $code = ''
    try { & $Action | Out-Null } catch { $code = Get-ConnectorErrorCode $_ }
    Assert-Connector ($code -eq $Expected) "Expected $Expected; received $code"
}
function New-FakeAccessor {
    param([hashtable]$Values = @{})
    $value = [pscustomobject]@{ Values = $Values }
    $value | Add-Member ScriptMethod GetProperty {
        param($Tag)
        $key = $Tag.Split('/')[-1]
        if (-not $this.Values.ContainsKey($key)) { throw 'Synthetic missing MAPI property.' }
        return ,$this.Values[$key]
    }
    return $value
}
function New-FakeCollection {
    param([object[]]$Values = @())
    $value = [pscustomobject]@{ Values = $Values; Count = $Values.Count }
    $value | Add-Member ScriptMethod Item { param($Index) return $this.Values[$Index - 1] }
    $value | Add-Member ScriptMethod Restrict {
        param($Filter)
        $match = [regex]::Match($Filter, "\[(ReceivedTime|SentOn)\] >= '([^']+)' AND \[\1\] <= '([^']+)'")
        if (-not $match.Success) { throw 'Synthetic scanner did not use its expected date restriction.' }
        $field = $match.Groups[1].Value
        $start = [datetime]::Parse($match.Groups[2].Value, [Globalization.CultureInfo]::CurrentCulture)
        $end = [datetime]::Parse($match.Groups[3].Value, [Globalization.CultureInfo]::CurrentCulture)
        return New-FakeCollection @($this.Values | Where-Object { $_.$field -ge $start -and $_.$field -le $end })
    }
    $value | Add-Member ScriptMethod Sort {
        param($Field, $Descending)
        $name = $Field.Trim('[', ']')
        $this.Values = @($this.Values | Sort-Object $name -Descending:$Descending)
    }
    return $value
}
function New-FakeAddress {
    param([string]$Address)
    return [pscustomobject]@{ Type = 'SMTP'; Address = $Address; PropertyAccessor = New-FakeAccessor }
}
function New-FakeRecipient {
    param([string]$Address)
    return [pscustomobject]@{ Address = $Address; AddressEntry = New-FakeAddress $Address; PropertyAccessor = New-FakeAccessor }
}
function New-FakeAttachment {
    param([string]$Name = 'quote.pdf', [byte[]]$Bytes = @(1, 2, 3), [long]$Size = -1, [bool]$Inline = $false)
    if ($Size -lt 0) { $Size = $Bytes.Length }
    return [pscustomobject]@{
        FileName = $Name; Size = $Size
        PropertyAccessor = New-FakeAccessor @{
            '0x37010102' = $Bytes; '0x370E001F' = $(if ($Inline) { 'image/png' } else { 'application/pdf' })
            '0x7FFE000B' = $Inline; '0x3712001F' = $(if ($Inline) { 'inline-image' } else { '' })
        }
    }
}
function New-FakeMail {
    param([string]$Subject = 'RE: FW: Quote 42', [object[]]$Attachments = @(), [string[]]$To = @('owner@example.test'))
    return [pscustomobject]@{
        Class = 43; Subject = $Subject; EntryID = 'synthetic-entry'; ConversationID = 'synthetic-conversation'
        Sender = New-FakeAddress 'vendor@example.test'; SenderEmailAddress = 'vendor@example.test'; SenderName = 'Synthetic Vendor'
        Recipients = New-FakeCollection @($To | ForEach-Object { New-FakeRecipient $_ })
        Attachments = New-FakeCollection $Attachments
        Body = 'SYNTHETIC-BODY-NEVER-PERSIST'; HTMLBody = '<img src="cid:inline-image">'
        SentOn = [datetime]'2026-01-01T12:00:00Z'; ReceivedTime = [datetime]'2026-01-01T12:01:00Z'
        PropertyAccessor = New-FakeAccessor @{ '0x1035001F' = '<synthetic-message@example.test>' }
    }
}
function New-FakeCandidate {
    param([string]$Source = '<synthetic-message@example.test>', [string]$Direction = 'incoming')
    return @{ entryId = 'synthetic-entry'; storeId = 'synthetic-store'; sourceMessageId = $Source; conversationId = 'synthetic-conversation'; direction = $Direction }
}

foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
    $tokens = $null; $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    Assert-Connector (@($parseErrors).Count -eq 0) ('PowerShell parses ' + $file.Name)
}
foreach ($subject in @('Quote 42', 'RE: Quote 42', 'fw: FWD: RE: Quote 42', '  re :  Quote 42  ')) {
    Assert-Connector ((Get-QuoteNumberFromSubject $subject) -eq 42) 'Repeated reply/forward subject prefixes are accepted.'
}
foreach ($subject in @('', 'Quote #42', 'Quote 42 pricing', 'Pricing Quote 42', 'Quote 0', 'Quote 2147483648', 'Quote 42 Quote 43')) {
    Assert-Connector ($null -eq (Get-QuoteNumberFromSubject $subject)) 'Ambiguous or unsupported subject is excluded.'
}
$config = Get-ConnectorConfiguration @{ mailbox = 'owner@example.test' }
Assert-Connector ($config.initialLookbackDays -eq 30 -and $config.pollIntervalSeconds -eq 120) 'Safe defaults are applied.'
Assert-ConnectorFailure { Get-ConnectorConfiguration @{ baseUrl = 'http://example.test' } } 'configuration-requires-https-arda-url'
Assert-ConnectorFailure { Get-ConnectorConfiguration @{ baseUrl = 'https://user:secret@example.test' } } 'configuration-requires-https-arda-url'
Assert-ConnectorFailure { Get-ConnectorConfiguration @{ maxMessagesPerPoll = 10000 } } 'configuration-invalid-maxMessagesPerPoll'

# Override the transport command in this test scope; no test can contact Arda.
function Invoke-RestMethod {
    param($Uri, $Method, [switch]$UseDefaultCredentials, $Headers, $ContentType, $Body, $TimeoutSec, $MaximumRedirection, $ErrorAction)
    $script:FakeRequest = @{ uri = $Uri; method = $Method; defaultCredentials = $UseDefaultCredentials.IsPresent; headers = $Headers; contentType = $ContentType; body = $Body; timeout = $TimeoutSec; redirects = $MaximumRedirection }
    if ($script:FakeRequestFailure) { throw [InvalidOperationException]::new('SYNTHETIC-SERVER-BODY-MUST-NOT-BE-LOGGED') }
    return @{ outcome = 'duplicate' }
}
$script:FakeRequestFailure = $false
[void](Invoke-ConnectorRequest $config 'import' @{ subject = 'Quote 42'; bodyText = 'Synthetic text' })
Assert-Connector ($script:FakeRequest.defaultCredentials -and $script:FakeRequest.redirects -eq 0 -and $script:FakeRequest.uri -eq ($config.baseUrl + '/api/vendor-quotes/import')) 'Transport uses Windows identity at the selected HTTPS endpoint without redirects.'
Assert-Connector ($script:FakeRequest.body -is [byte[]] -and ([Text.Encoding]::UTF8.GetString($script:FakeRequest.body) | ConvertFrom-Json).bodyText -eq 'Synthetic text') 'Transport sends UTF8 JSON bytes.'
$script:FakeRequestFailure = $true
Assert-ConnectorFailure { Invoke-ConnectorRequest $config 'sync' @{} } 'arda-unreachable-or-server-error'
$script:FakeRequestFailure = $false

$exchange = [pscustomobject]@{ Type = 'EX'; Address = '/O=EXAMPLE/CN=RECIPIENT'; PropertyAccessor = New-FakeAccessor }
$exchange | Add-Member ScriptMethod GetExchangeUser { return [pscustomobject]@{ PrimarySmtpAddress = 'Exchange.User@Example.Test' } }
Assert-Connector ((Get-OutlookSmtpAddress $exchange) -eq 'exchange.user@example.test') 'Exchange sender/recipient resolves to SMTP.'
$exchangeFallback = [pscustomobject]@{ Type = 'EX'; Address = '/O=EXAMPLE'; PropertyAccessor = New-FakeAccessor @{ '0x39FE001E' = 'fallback@example.test' } }
$exchangeFallback | Add-Member ScriptMethod GetExchangeUser { return $null }
$exchangeFallback | Add-Member ScriptMethod GetExchangeDistributionList { return $null }
Assert-Connector ((Get-OutlookSmtpAddress $exchangeFallback) -eq 'fallback@example.test') 'Exchange SMTP property fallback works.'

$mail = New-FakeMail -Attachments @((New-FakeAttachment), (New-FakeAttachment -Name 'logo.png' -Inline $true))
$record = New-FakeCandidate
$message = Convert-OutlookMail $mail $record $config.mailbox
Assert-Connector ($message.attachments.Count -eq 1 -and $message.attachments[0].fileName -eq 'quote.pdf') 'Inline images are excluded and real attachments remain flat.'
Assert-Connector ($message.attachments[0].contentBase64 -eq 'AQID') 'Attachment bytes are encoded without a disk export.'
Assert-Connector ($message.vendorAddresses.Count -eq 1 -and $message.vendorAddresses[0] -eq 'vendor@example.test') 'Incoming mail targets its SMTP sender.'
Assert-Connector ($message.bodyText -eq $mail.Body) 'Full plain text body is preserved in memory.'
$identity = Get-OutlookSourceIdentity $mail 'synthetic-store'
Assert-Connector ($identity.sourceMessageId -eq '<synthetic-message@example.test>') 'Internet message ID is the preferred deduplication identity.'
$mail.PropertyAccessor = New-FakeAccessor
$fallback = Get-OutlookSourceIdentity $mail 'synthetic-store'
Assert-Connector ($fallback.sourceMessageId -like 'outlook:*' -and $fallback.sourceMessageId -eq (Get-OutlookSourceIdentity $mail 'synthetic-store').sourceMessageId) 'Entry/store/conversation fallback is stable.'
Assert-Connector ($fallback.sourceMessageId -ne (Get-OutlookSourceIdentity $mail 'other-store').sourceMessageId) 'Fallback separates Outlook stores.'

$outgoing = New-FakeMail -To @('owner@example.test', 'vendor-a@example.test', 'vendor-b@example.test', 'vendor-a@example.test')
$outRecord = New-FakeCandidate '<sent@example.test>' 'outgoing'
$outMessage = Convert-OutlookMail $outgoing $outRecord $config.mailbox
Assert-Connector ($outMessage.vendorAddresses.Count -eq 2) 'Sent mail fans out to unique recipients except the selected mailbox.'

$now = [datetime]'2026-01-02T12:00:00Z'
$state = New-ConnectorState $config $now
Assert-Connector (Add-ConnectorCandidate $state $outRecord $now) 'New candidate is queued before import.'
$pending = @($state.pending.Values)[0]
$script:FakeTransportCalls = [Collections.Generic.List[string]]::new()
$script:FailVendorB = $true
$reader = { param($candidate) return $outMessage }
$importer = {
    param($payload)
    $script:FakeTransportCalls.Add($payload.vendorEmail)
    if ($payload.vendorEmail -eq 'vendor-b@example.test' -and $script:FailVendorB) { return @{ outcome = 'unmatched' } }
    return @{ outcome = 'imported' }
}
$first = Invoke-ConnectorDelivery $state $pending $reader $importer $now
Assert-Connector ($first.imported -eq 1 -and $first.deferred -eq 1 -and $state.pending.Count -eq 1) 'Unmatched fanout remains pending after another vendor succeeds.'
Assert-Connector (@(Get-ConnectorDueRecords $state $config $now.AddSeconds(10)).Count -eq 0) 'Backoff prevents immediate retry.'
Assert-Connector (@(Get-ConnectorDueRecords $state $config $now.AddMinutes(1)).Count -eq 1) 'Unmatched mail becomes retryable independent of scan cursor.'
$script:FailVendorB = $false
$second = Invoke-ConnectorDelivery $state $pending $reader $importer $now.AddMinutes(1)
Assert-Connector ($second.imported -eq 1 -and $state.pending.Count -eq 0 -and $state.completed.Count -eq 1) 'Later quote availability completes pending fanout.'
Assert-Connector (@($script:FakeTransportCalls | Where-Object { $_ -eq 'vendor-a@example.test' }).Count -eq 1) 'Previously acknowledged vendors are not posted again.'
Assert-Connector (-not (Add-ConnectorCandidate $state $outRecord $now)) 'Repeated scan does not requeue completed mail.'

$offlineState = New-ConnectorState $config $now
[void](Add-ConnectorCandidate $offlineState (New-FakeCandidate '<offline@example.test>' 'outgoing') $now)
$script:OfflineCalls = 0
$offline = Invoke-ConnectorDelivery $offlineState @($offlineState.pending.Values)[0] $reader {
    param($payload)
    $script:OfflineCalls++
    Stop-ConnectorOperation 'arda-unreachable-or-server-error'
} $now
Assert-Connector ($script:OfflineCalls -eq 1 -and $offlineState.pending.Count -eq 1 -and $offline.deferred -eq 1) 'An unavailable server stops fanout after one failure and retains the complete remaining message.'

foreach ($outcome in @('duplicate', 'unassigned')) {
    $candidate = New-FakeCandidate "<$outcome@example.test>"
    [void](Add-ConnectorCandidate $state $candidate $now)
    $pending = @($state.pending.Values)[0]
    $response = @{ outcome = $outcome }
    $result = Invoke-ConnectorDelivery $state $pending { param($item) return $message } { param($payload) return $response } $now
    Assert-Connector ($state.pending.Count -eq 0 -and $result.deferred -eq 0) "$outcome is a durable backend success."
}
$failedCandidate = New-FakeCandidate '<failed@example.test>'
[void](Add-ConnectorCandidate $state $failedCandidate $now)
$pending = @($state.pending.Values)[0]
$result = Invoke-ConnectorDelivery $state $pending { param($item) return $message } { param($payload) Stop-ConnectorOperation 'quote-access-denied' } $now
Assert-Connector ($state.pending.Count -eq 1 -and $pending.lastErrorCode -eq 'quote-access-denied') 'Permission failures remain retryable and retain their identity.'
$beforeDryRun = $state | ConvertTo-Json -Depth 12 -Compress
$result = Invoke-ConnectorDelivery $state $pending { param($item) return $message } { throw 'Dry run must not call transport.' } $now.AddHours(1) -DryRun
Assert-Connector ($result.ready -eq 1 -and ($state | ConvertTo-Json -Depth 12 -Compress) -eq $beforeDryRun) 'Dry run makes no network call and changes no delivery metadata.'

$tooLarge = New-FakeMail -Attachments @((New-FakeAttachment -Size (10MB + 1)))
Assert-ConnectorFailure { Convert-OutlookMail $tooLarge (New-FakeCandidate) $config.mailbox } 'attachment-over-10mb'
$many = New-FakeMail -Attachments @(1..26 | ForEach-Object { New-FakeAttachment })
Assert-ConnectorFailure { Convert-OutlookMail $many (New-FakeCandidate) $config.mailbox } 'attachment-count-over-25'
$eightMb = [byte[]]::new(8MB)
$totalTooLarge = New-FakeMail -Attachments @((New-FakeAttachment -Bytes $eightMb), (New-FakeAttachment -Bytes $eightMb), (New-FakeAttachment -Bytes $eightMb))
Assert-ConnectorFailure { Convert-OutlookMail $totalTooLarge (New-FakeCandidate) $config.mailbox } 'message-attachments-over-20mb'
$longBody = New-FakeMail
$longBody.Body = 'x' * 200001
Assert-ConnectorFailure { Convert-OutlookMail $longBody (New-FakeCandidate) $config.mailbox } 'message-body-over-200000-characters'
$payloadCalls = 0
$result = Invoke-ConnectorDelivery $state $pending { param($item) Convert-OutlookMail $tooLarge $item $config.mailbox } { $payloadCalls++; return @{ outcome = 'imported' } } $now.AddHours(1)
Assert-Connector ($result.deferred -eq 1 -and $payloadCalls -eq 0 -and $state.pending.Count -eq 1) 'Oversized attachment fails the complete message before any import.'

$orderedMail = @((New-FakeMail 'Other subject'), (New-FakeMail 'Quote 42'), (New-FakeMail 'Quote 43'))
for ($index = 0; $index -lt $orderedMail.Count; $index++) { $orderedMail[$index].ReceivedTime = $now.AddHours(-1).AddMinutes($index) }
$folder = [pscustomobject]@{ StoreID = 'synthetic-store'; Items = New-FakeCollection $orderedMail }
$cursor = (New-ConnectorState $config $now).folders.incoming
$scan = Get-OutlookScanBatch $folder $cursor 'incoming' 2
Assert-Connector ($scan.entries.Count -eq 1 -and $scan.nextIndex -eq 3 -and -not $scan.complete) 'Mailbox scanning is bounded and records a continuation.'
$cursor.nextIndex = $scan.nextIndex
$scan = Get-OutlookScanBatch $folder $cursor 'incoming' 2
Assert-Connector ($scan.entries.Count -eq 1 -and $scan.complete) 'A later scan resumes the bounded batch.'

$oldMessages = @(1..3000 | ForEach-Object { [pscustomobject]@{ Class = 43; Subject = 'Other subject'; ReceivedTime = $now.AddDays(-20); SentOn = $now.AddDays(-20) } })
$newArrival = New-FakeMail 'Quote 999'
$newArrival.ReceivedTime = $now.AddMinutes(-1); $newArrival.SentOn = $now.AddMinutes(-2)
$liveFolder = [pscustomobject]@{ StoreID = 'synthetic-store'; Items = New-FakeCollection @($oldMessages + $newArrival) }
$emptyFolder = [pscustomobject]@{ StoreID = 'synthetic-store'; Items = New-FakeCollection }
$scanState = New-ConnectorState $config $now
$liveScanner = { param($direction, $cursor, $limit)
    $selectedFolder = $liveFolder
    if ($direction -eq 'outgoing') { $selectedFolder = $emptyFolder }
    Get-OutlookScanBatch $selectedFolder $cursor $direction $limit
}
$scanResult = Invoke-ConnectorScanCycle $scanState $config $liveScanner $now
Assert-Connector ($scanState.pending.Count -eq 1 -and @($scanState.pending.Values)[0].priority -eq 0) 'New arrivals are queued in the first poll despite 3000 older messages.'
Assert-Connector ($scanState.folders.incoming.nextIndex -eq 26 -and $scanState.folders.incoming.live.nextIndex -eq 1) 'Historical and live scanning have independent bounded cursors.'
$failedScan = { param($direction, $cursor, $limit) return @{ entries = @(); nextIndex = $cursor.nextIndex; complete = $false; errorCode = 'synthetic-read-failure' } }
$beforeLiveEnd = $scanState.folders.incoming.live.windowEndUtc
[void](Invoke-ConnectorScanCycle $scanState $config $failedScan $now.AddMinutes(2))
Assert-Connector ($scanState.folders.incoming.live.windowEndUtc -eq $beforeLiveEnd) 'A failed live scan never advances its time boundary.'

$pagedState = New-ConnectorState $config $now
$recentMail = @(1..40 | ForEach-Object {
    $item = New-FakeMail ("Quote " + (1000 + $_))
    $item.ReceivedTime = $now.AddMinutes(-2).AddSeconds($_)
    $item.EntryID = "synthetic-recent-$_"
    $item.PropertyAccessor = New-FakeAccessor @{ '0x1035001F' = "<recent-$_@example.test>" }
    $item
})
$liveFolder.Items = New-FakeCollection $recentMail
[void](Invoke-ConnectorScanCycle $pagedState $config $liveScanner $now)
Assert-Connector ($pagedState.folders.incoming.live.nextIndex -eq 26 -and $pagedState.pending.Count -eq 25) 'A busy live window retains its bounded continuation.'
[void](Invoke-ConnectorScanCycle $pagedState $config $liveScanner $now.AddMinutes(2))
Assert-Connector ($pagedState.pending.Count -eq 40 -and $pagedState.folders.incoming.live.nextIndex -eq 1) 'The next live page imports every candidate without resetting behind the first page.'

$brokenMail = New-FakeMail 'Quote 77'
$brokenMail.EntryID = ''
$brokenMail.ReceivedTime = $now.AddMinutes(-2)
$laterMail = New-FakeMail 'Quote 78'
$laterMail.ReceivedTime = $now.AddMinutes(-1)
$brokenFolder = [pscustomobject]@{ StoreID = 'synthetic-store'; Items = New-FakeCollection @($brokenMail, $laterMail) }
$scan = Get-OutlookScanBatch $brokenFolder (New-ConnectorState $config $now).folders.incoming 'incoming' 25
Assert-Connector (-not $scan.complete -and $scan.nextIndex -eq 1 -and $scan.errorCode -eq 'outlook-item-identity-unavailable') 'A failed Outlook item remains at the scan continuation rather than being skipped.'

$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ArdaOutlookConnectorTests-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDirectory)
try {
    $stateFile = Join-Path $testDirectory 'metadata.json'
    Write-ConnectorJson $stateFile $state
    $resumed = Read-ConnectorJson $stateFile
    Assert-Connector ($resumed.pending.Count -eq 1 -and $resumed.completed.Count -eq $state.completed.Count) 'Retry and deduplication state survive a restart.'
    Write-ConnectorJson $stateFile $resumed
    Assert-Connector ([IO.File]::Exists("$stateFile.bak")) 'Atomic replacement retains a previous metadata version.'
    $savedText = [IO.File]::ReadAllText($stateFile)
    Assert-Connector ($savedText -notmatch 'SYNTHETIC-BODY|contentBase64|quote\.pdf|subject') 'Persisted state contains no message content or attachments.'
} finally {
    foreach ($name in @('metadata.json', 'metadata.json.bak', 'metadata.json.new')) {
        $testFile = Join-Path $testDirectory $name
        if ([IO.File]::Exists($testFile)) { Remove-Item -LiteralPath $testFile -Force }
    }
    Remove-Item -LiteralPath $testDirectory -Force
}
Write-Host ("PASS: $script:ConnectorAssertions synthetic assertions; no Outlook session or network used.")
