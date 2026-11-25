# Create a native Outlook Reply All only for a claimed request from Arda.
function Get-ConnectorFollowUpBody {
    return "Hi team,`n`nJust following up on this request.`n`nDo you have this quote available?`n`nThank you!"
}

function Confirm-ConnectorFollowUpJob {
    param([hashtable]$Job)
    $id = [Guid]::Empty
    $source = [string]$Job['sourceMessageId']
    $conversation = [string]$Job['conversationId']
    $sentAt = [DateTimeOffset]::MinValue
    $sentValue = $Job['originalSentAt']
    if ($sentValue -is [datetime] -or $sentValue -is [DateTimeOffset]) {
        $sentAt = [DateTimeOffset]$sentValue
        $hasSentAt = $true
    } else {
        $hasSentAt = [DateTimeOffset]::TryParse([string]$sentValue, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$sentAt)
    }
    $recipients = @($Job['recipientEmails'])
    $body = ([string]$Job['bodyText']).Replace("`r`n", "`n")
    if (-not [Guid]::TryParse([string]$Job['id'], [ref]$id) -or $id -eq [Guid]::Empty -or
        -not $source -or $source.Length -gt 1024 -or $source -match '[\p{Cc}]' -or
        $conversation.Length -gt 512 -or $conversation -match '[\p{Cc}]' -or
        -not (Get-QuoteNumberFromSubject ([string]$Job['subject'])) -or
        -not $hasSentAt -or $sentAt.Year -lt 2000 -or $sentAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5) -or
        $recipients.Count -lt 1 -or $recipients.Count -gt 200 -or
        $body -cne (Get-ConnectorFollowUpBody)) {
        Stop-ConnectorOperation 'follow-up-invalid-job'
    }
    foreach ($email in $recipients) {
        if (-not (ConvertTo-SmtpAddress ([string]$email))) { Stop-ConnectorOperation 'follow-up-invalid-job' }
    }
    return $sentAt
}

function Find-OutlookFollowUpSource {
    param($SentFolder, [hashtable]$Job, [DateTimeOffset]$SentAt)
    $items = $null; $filtered = $null
    $source = $null
    # Use the previously synchronized timestamp to bound the Sent Items lookup.
    # The exact stored identity still decides which Outlook item may be used.
    $sentLocal = $SentAt.LocalDateTime
    $culture = [Globalization.CultureInfo]::CurrentCulture
    $start = $sentLocal.AddMinutes(-2).ToString('g', $culture).Replace("'", "''")
    $end = $sentLocal.AddMinutes(2).ToString('g', $culture).Replace("'", "''")
    try {
        $items = $SentFolder.Items
        $filtered = $items.Restrict("[SentOn] >= '$start' AND [SentOn] <= '$end'")
        $count = [int]$filtered.Count
        if ($count -gt 500) { Stop-ConnectorOperation 'follow-up-original-unavailable' }
        for ($index = 1; $index -le $count; $index++) {
            $original = $null
            try {
                $original = $filtered.Item($index)
                if ($original.Class -ne 43 -or [string]$original.Subject -cne $Job.subject) { continue }
                $identity = Get-OutlookSourceIdentity $original ([string]$SentFolder.StoreID)
                if ($identity.sourceMessageId -cne $Job.sourceMessageId -or
                    ($Job['conversationId'] -and $identity.conversationId -cne $Job.conversationId)) { continue }
                if ($source) { Stop-ConnectorOperation 'follow-up-original-unavailable' }
                $source = $identity
            } finally { Release-OutlookReference $original }
        }
    } finally { Release-OutlookReference $filtered; Release-OutlookReference $items }
    if (-not $source) { Stop-ConnectorOperation 'follow-up-original-unavailable' }
    return $source
}

function Get-OutlookFollowUpRecipients {
    param($Original)
    $recipients = $null
    $audience = [Collections.Generic.List[object]]::new()
    try {
        $recipients = $Original.Recipients
        if ($recipients.Count -gt 200) { Stop-ConnectorOperation 'follow-up-recipient-mismatch' }
        for ($index = 1; $index -le $recipients.Count; $index++) {
            $recipient = $null; $entry = $null
            try {
                $recipient = $recipients.Item($index)
                $type = [int]$recipient.Type
                if ($type -notin @(1, 2)) { continue }
                $entry = $recipient.AddressEntry
                $address = Get-OutlookSmtpAddress $entry ([string]$recipient.Address)
                if (-not $address) { $address = ConvertTo-SmtpAddress ([string](Get-OutlookProperty $recipient '0x39FE001E')) }
                if (-not $address) { Stop-ConnectorOperation 'follow-up-recipient-mismatch' }
                $audience.Add(@{ address = $address; type = $type })
            } finally { Release-OutlookReference $entry; Release-OutlookReference $recipient }
        }
    } finally { Release-OutlookReference $recipients }
    return $audience.ToArray()
}

function Set-OutlookFollowUpRecipients {
    param($Reply, [object[]]$Audience)
    $recipients = $null
    try {
        $recipients = $Reply.Recipients
        # Outlook can include the sender when replying to a Sent Item. Use the
        # original To and Cc recipients; do not copy hidden recipients.
        for ($index = $recipients.Count; $index -gt 0; $index--) { [void]$recipients.Remove($index) }
        foreach ($target in $Audience) {
            $recipient = $null; $entry = $null
            try {
                $recipient = $recipients.Add($target.address)
                $recipient.Type = $target.type
                if (-not $recipient.Resolve()) { Stop-ConnectorOperation 'follow-up-recipient-mismatch' }
                $entry = $recipient.AddressEntry
                if ((Get-OutlookSmtpAddress $entry ([string]$recipient.Address)) -cne $target.address) {
                    Stop-ConnectorOperation 'follow-up-recipient-mismatch'
                }
            } finally { Release-OutlookReference $entry; Release-OutlookReference $recipient }
        }
    } finally { Release-OutlookReference $recipients }
}

function Set-OutlookFollowUpText {
    param($Reply)
    $text = Get-ConnectorFollowUpBody
    if ([int]$Reply.BodyFormat -eq 2) {
        $html = [string]$Reply.HTMLBody
        $bodyTag = [regex]::Match($html, '<body\b[^>]*>', [Text.RegularExpressions.RegexOptions]::IgnoreCase,
            [TimeSpan]::FromSeconds(1))
        if ($bodyTag.Success) {
            $paragraphs = ($text -split "`n`n" | ForEach-Object { '<p>' + [Net.WebUtility]::HtmlEncode($_) + '</p>' }) -join ''
            $Reply.HTMLBody = $html.Insert($bodyTag.Index + $bodyTag.Length, $paragraphs)
            return
        }
    }
    $Reply.Body = $text.Replace("`n", "`r`n") + "`r`n`r`n" + [string]$Reply.Body
}

function Open-OutlookFollowUpDraft {
    param($Namespace, $SentFolder, [int]$AccountIndex, [string]$Mailbox, [hashtable]$Job)
    $sentAt = Confirm-ConnectorFollowUpJob $Job
    $source = Find-OutlookFollowUpSource $SentFolder $Job $sentAt
    $original = $null; $sender = $null; $reply = $null; $accounts = $null; $account = $null
    try {
        $original = $Namespace.GetItemFromID($source.entryId, $source.storeId)
        if ($null -eq $original -or $original.Class -ne 43 -or [string]$original.Subject -cne $Job.subject -or
            (Get-OutlookSourceIdentity $original $source.storeId).sourceMessageId -cne $source.sourceMessageId) {
            Stop-ConnectorOperation 'follow-up-original-unavailable'
        }
        $sender = $original.Sender
        $from = Get-OutlookSmtpAddress $sender ([string]$original.SenderEmailAddress)
        if (-not $from) { $from = ConvertTo-SmtpAddress ([string](Get-OutlookProperty $original '0x5D01001F')) }
        $accounts = $Namespace.Accounts
        $account = $accounts.Item($AccountIndex)
        if ($from -ne $Mailbox -or (ConvertTo-SmtpAddress ([string]$account.SmtpAddress)) -ne $Mailbox) {
            Stop-ConnectorOperation 'follow-up-original-unavailable'
        }
        $audience = @(Get-OutlookFollowUpRecipients $original)
        if ($audience.Count -eq 0) { Stop-ConnectorOperation 'follow-up-recipient-mismatch' }
        $addresses = @($audience | ForEach-Object { $_.address })
        foreach ($email in $Job.recipientEmails) {
            if ((ConvertTo-SmtpAddress ([string]$email)) -notin $addresses) {
                Stop-ConnectorOperation 'follow-up-recipient-mismatch'
            }
        }
        $reply = $original.ReplyAll()
        $reply.SendUsingAccount = $account
        Set-OutlookFollowUpRecipients $reply $audience
        Set-OutlookFollowUpText $reply
        $reply.Display($false)
    } finally {
        Release-OutlookReference $reply; Release-OutlookReference $account; Release-OutlookReference $accounts
        Release-OutlookReference $sender; Release-OutlookReference $original
    }
}

function Invoke-ConnectorFollowUpPoll {
    param([hashtable]$Config, [scriptblock]$OpenDraft, $StopEvent)
    try {
        $response = ConvertTo-ConnectorMap (Invoke-ConnectorRequest $Config 'follow-up/claim' @{ mailbox = $Config.mailbox })
        if (-not $response -or -not $response['job']) { return $null }
    } catch { return Get-ConnectorErrorCode $_ 'follow-up-claim-failed' }

    $job = $response['job']
    $result = @{ mailbox = $Config.mailbox; jobId = $job['id']; opened = $false }
    try {
        if ($StopEvent.WaitOne(0)) { Stop-ConnectorOperation 'follow-up-connector-stopping' }
        & $OpenDraft $job
        $result.opened = $true
    } catch { $result.message = Get-ConnectorErrorCode $_ 'follow-up-outlook-could-not-open' }
    try { [void](Invoke-ConnectorRequest $Config 'follow-up/result' $result) }
    catch { return Get-ConnectorErrorCode $_ 'follow-up-result-failed' }
    return $null
}
