# Outlook Object Model reads only. Never save, resolve, send, move, delete or mark mail read.
function Release-OutlookReference {
    param($Value)
    if ($null -ne $Value -and [Runtime.InteropServices.Marshal]::IsComObject($Value)) {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($Value)
    }
}

function Get-OutlookProperty {
    param($Item, [string]$Tag, [switch]$Required)
    $accessor = $null
    try {
        $accessor = $Item.PropertyAccessor
        $value = $accessor.GetProperty('http://schemas.microsoft.com/mapi/proptag/' + $Tag)
        return ,$value
    } catch {
        if ($Required) { Stop-ConnectorOperation 'outlook-attachment-content-unavailable' }
        return $null
    } finally { Release-OutlookReference $accessor }
}

function ConvertTo-SmtpAddress {
    param([AllowNull()][string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    try {
        $parsed = [Net.Mail.MailAddress]::new($Value.Trim())
        if ($parsed.Address -notmatch '^[^\s@<>]+@[^\s@<>]+\.[^\s@<>]+$') { return $null }
        return $parsed.Address.ToLowerInvariant()
    } catch { return $null }
}

function Get-OutlookSmtpAddress {
    param($AddressEntry, [string]$Fallback = '')
    $exchange = $null
    try {
        if ($AddressEntry) {
            if ($AddressEntry.Type -eq 'EX') {
                try { $exchange = $AddressEntry.GetExchangeUser() } catch { $exchange = $null }
                if (-not $exchange) { try { $exchange = $AddressEntry.GetExchangeDistributionList() } catch { $exchange = $null } }
                if ($exchange) {
                    $smtp = ConvertTo-SmtpAddress ([string]$exchange.PrimarySmtpAddress)
                    if ($smtp) { return $smtp }
                }
                $smtp = ConvertTo-SmtpAddress ([string](Get-OutlookProperty $AddressEntry '0x39FE001E'))
                if ($smtp) { return $smtp }
            } else {
                $smtp = ConvertTo-SmtpAddress ([string]$AddressEntry.Address)
                if ($smtp) { return $smtp }
            }
        }
        return ConvertTo-SmtpAddress $Fallback
    } finally { Release-OutlookReference $exchange }
}

function Get-OutlookSourceIdentity {
    param($Mail, [string]$StoreId)
    $internetId = [string](Get-OutlookProperty $Mail '0x1035001F')
    if (-not $internetId) { $internetId = [string](Get-OutlookProperty $Mail '0x1035001E') }
    $conversation = [string]$Mail.ConversationID
    $entryId = [string]$Mail.EntryID
    if (-not $entryId -or -not $StoreId) { Stop-ConnectorOperation 'outlook-item-identity-unavailable' }
    $sourceId = $internetId.Trim()
    if (-not $sourceId) { $sourceId = 'outlook:' + (Get-ConnectorHash ($StoreId + '|' + $entryId + '|' + $conversation)) }
    return @{ entryId = $entryId; storeId = $StoreId; sourceMessageId = $sourceId; conversationId = $conversation }
}

function Get-OutlookAttachmentPayloads {
    param($Mail)
    $collection = $null
    $result = [Collections.Generic.List[object]]::new()
    $totalBytes = 0L
    try {
        $collection = $Mail.Attachments
        if ($collection.Count -gt 500) { Stop-ConnectorOperation 'attachment-count-exceeds-safe-scan-limit' }
        for ($index = 1; $index -le $collection.Count; $index++) {
            $attachment = $null
            try {
                $attachment = $collection.Item($index)
                $name = [IO.Path]::GetFileName(([string]$attachment.FileName).Replace('\', '/'))
                if ([string]::IsNullOrWhiteSpace($name)) { Stop-ConnectorOperation 'attachment-filename-unavailable' }
                $mime = [string](Get-OutlookProperty $attachment '0x370E001F')
                if (-not $mime) { $mime = [string](Get-OutlookProperty $attachment '0x370E001E') }
                $hidden = Get-OutlookProperty $attachment '0x7FFE000B'
                $contentId = [string](Get-OutlookProperty $attachment '0x3712001F')
                if (-not $contentId) { $contentId = [string](Get-OutlookProperty $attachment '0x3712001E') }
                $image = $mime -like 'image/*' -or $name -match '\.(png|jpe?g|gif|bmp|svg|webp|ico|tiff?)$'
                $inline = $image -and $hidden -eq $true
                if ($image -and $contentId -and -not $inline) {
                    try { $inline = ([string]$Mail.HTMLBody).IndexOf(('cid:' + $contentId.Trim('<', '>')), [StringComparison]::OrdinalIgnoreCase) -ge 0 }
                    catch { $inline = $false }
                }
                if ($inline) { continue }
                if ($result.Count -ge 25) { Stop-ConnectorOperation 'attachment-count-over-25' }
                if ([long]$attachment.Size -gt 10MB) { Stop-ConnectorOperation 'attachment-over-10mb' }
                # Binary by-value attachments stay in memory; embedded items are deferred if unavailable.
                [byte[]]$bytes = Get-OutlookProperty $attachment '0x37010102' -Required
                if ($bytes.Length -gt 10MB) { Stop-ConnectorOperation 'attachment-over-10mb' }
                $totalBytes += $bytes.Length
                if ($totalBytes -gt 20MB) { Stop-ConnectorOperation 'message-attachments-over-20mb' }
                $result.Add(@{ fileName = $name; contentType = $(if ($mime) { $mime } else { 'application/octet-stream' }); contentBase64 = [Convert]::ToBase64String($bytes) })
                $bytes = $null
            } finally { Release-OutlookReference $attachment }
        }
        return $result.ToArray()
    } finally { Release-OutlookReference $collection }
}

function Convert-OutlookMail {
    param($Mail, [hashtable]$Record, [string]$Mailbox)
    if ($null -eq $Mail -or $Mail.Class -ne 43) { Stop-ConnectorOperation 'outlook-message-unavailable' }
    $subject = [string]$Mail.Subject
    if (-not (Get-QuoteNumberFromSubject $subject)) { Stop-ConnectorOperation 'subject-no-longer-matches' }
    $sender = $null
    $recipients = $null
    try {
        $sender = $Mail.Sender
        $from = Get-OutlookSmtpAddress $sender ([string]$Mail.SenderEmailAddress)
        if (-not $from) { $from = ConvertTo-SmtpAddress ([string](Get-OutlookProperty $Mail '0x5D01001F')) }
        if (-not $from) { Stop-ConnectorOperation 'sender-smtp-unavailable' }
        $addresses = [Collections.Generic.List[string]]::new()
        $recipients = $Mail.Recipients
        if ($recipients.Count -gt 200) { Stop-ConnectorOperation 'recipient-count-over-200' }
        for ($index = 1; $index -le $recipients.Count; $index++) {
            $recipient = $null; $addressEntry = $null
            try {
                $recipient = $recipients.Item($index)
                $addressEntry = $recipient.AddressEntry
                $address = Get-OutlookSmtpAddress $addressEntry ([string]$recipient.Address)
                if (-not $address) { $address = ConvertTo-SmtpAddress ([string](Get-OutlookProperty $recipient '0x39FE001E')) }
                if (-not $address) { Stop-ConnectorOperation 'recipient-smtp-unavailable' }
                if (-not $addresses.Contains($address)) { $addresses.Add($address) }
            } finally { Release-OutlookReference $addressEntry; Release-OutlookReference $recipient }
        }
        $body = [string]$Mail.Body
        if ($body.Length -gt 200000) { Stop-ConnectorOperation 'message-body-over-200000-characters' }
        $vendors = @($from)
        if ($Record.direction -eq 'outgoing') { $vendors = @($addresses | Where-Object { $_ -ne $Mailbox }) }
        elseif ($from -eq $Mailbox) { $vendors = @() }
        $received = $null
        if ($Record.direction -eq 'incoming') { $received = ([datetime]$Mail.ReceivedTime).ToUniversalTime().ToString('o') }
        return @{
            subject = $subject; fromAddress = $from; fromName = [string]$Mail.SenderName
            toAddresses = $addresses.ToArray(); vendorAddresses = $vendors
            sentAt = ([datetime]$Mail.SentOn).ToUniversalTime().ToString('o'); receivedAt = $received
            bodyText = $body; attachments = @(Get-OutlookAttachmentPayloads $Mail)
        }
    } finally { Release-OutlookReference $sender; Release-OutlookReference $recipients }
}

function Get-OutlookScanBatch {
    param($Folder, [hashtable]$Cursor, [string]$Direction, [int]$Limit)
    $items = $null; $filtered = $null
    $entries = [Collections.Generic.List[object]]::new()
    $nextIndex = [int]$Cursor.nextIndex
    $complete = $false; $errorCode = $null
    try {
        $field = 'ReceivedTime'
        if ($Direction -eq 'outgoing') { $field = 'SentOn' }
        $start = ([datetime]$Cursor.windowStartUtc).ToLocalTime().ToString('g', [Globalization.CultureInfo]::CurrentCulture).Replace("'", "''")
        # Outlook Jet date filters have minute resolution. Round the upper bound outwards.
        $end = ([datetime]$Cursor.windowEndUtc).ToLocalTime().AddMinutes(1).ToString('g', [Globalization.CultureInfo]::CurrentCulture).Replace("'", "''")
        $items = $Folder.Items
        $filtered = $items.Restrict("[$field] >= '$start' AND [$field] <= '$end'")
        [void]$filtered.Sort("[$field]", $false)
        $count = [int]$filtered.Count
        $last = [Math]::Min($count, $nextIndex + $Limit - 1)
        for ($index = $nextIndex; $index -le $last; $index++) {
            $mail = $null
            try {
                $mail = $filtered.Item($index)
                if ($mail.Class -eq 43 -and (Get-QuoteNumberFromSubject ([string]$mail.Subject))) {
                    $identity = Get-OutlookSourceIdentity $mail ([string]$Folder.StoreID)
                    $identity.direction = $Direction
                    $entries.Add($identity)
                }
                $nextIndex = $index + 1
            } catch { $errorCode = Get-ConnectorErrorCode $_ 'outlook-scan-read-failed'; break }
            finally { Release-OutlookReference $mail }
        }
        $complete = -not $errorCode -and $nextIndex -gt $count
    } catch { $errorCode = Get-ConnectorErrorCode $_ 'outlook-folder-unavailable' }
    finally { Release-OutlookReference $filtered; Release-OutlookReference $items }
    return @{ entries = $entries.ToArray(); nextIndex = $nextIndex; complete = $complete; errorCode = $errorCode }
}

function Get-OutlookAccountChoices {
    param($Namespace)
    $accounts = $null
    $choices = [Collections.Generic.List[object]]::new()
    try {
        $accounts = $Namespace.Accounts
        for ($index = 1; $index -le $accounts.Count; $index++) {
            $account = $null
            try {
                $account = $accounts.Item($index)
                $smtp = ConvertTo-SmtpAddress ([string]$account.SmtpAddress)
                if ($smtp) { $choices.Add(@{ index = $index; smtp = $smtp }) }
            } finally { Release-OutlookReference $account }
        }
        return $choices.ToArray()
    } finally { Release-OutlookReference $accounts }
}

function Get-OutlookMailboxFolders {
    param($Namespace, [int]$AccountIndex)
    $accounts = $null; $account = $null; $store = $null
    try {
        $accounts = $Namespace.Accounts
        $account = $accounts.Item($AccountIndex)
        $store = $account.DeliveryStore
        if (-not $store) { Stop-ConnectorOperation 'outlook-own-account-store-unavailable' }
        $inbox = $store.GetDefaultFolder(6)
        $sent = $store.GetDefaultFolder(5)
        if (-not $inbox -or -not $sent) { Stop-ConnectorOperation 'outlook-inbox-or-sent-folder-unavailable' }
        return @{ incoming = $inbox; outgoing = $sent }
    } finally { Release-OutlookReference $store; Release-OutlookReference $account; Release-OutlookReference $accounts }
}
