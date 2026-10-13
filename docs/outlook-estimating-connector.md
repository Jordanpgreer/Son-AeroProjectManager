# Outlook quote correspondence connector

The portable connector in `scripts/outlook-estimating/` reads the selected classic Outlook account's Inbox and Sent Items and imports correspondence into Arda Estimating's **Quote Status** page (`#/quote-status`). It does not change the quote's overall status. Server authorization and matching remain authoritative.

## Deployment and setup

Package the canonical files in `scripts/outlook-estimating/` at the ZIP root and inject a `configuration.json` containing the deployment's HTTPS Estimating base URL. Ship `README.md` with the scripts. The example file contains no mailbox address or credentials. The backend links these canonical assets into its published connector download; do not maintain a second script copy.

The user extracts the ZIP and runs `Install.cmd` in their normal Windows session, with classic Outlook open and configured. Setup confirms the HTTPS Arda address and selects their own Outlook account. It saves the package locally, registers a per-user sign-in launcher, and starts background syncing after setup succeeds. Installation does not require administrator access. Multiple Outlook accounts require an explicit SMTP address selection; a single account is selected automatically. Only that account's Inbox and Sent Items are read, without recursion into subfolders, shared folders, or archives.

Local choices are stored separately for each package base URL under `%LOCALAPPDATA%\Arda\OutlookEstimating\configuration-<hash>.json`. Rerunning `Install.cmd` updates the installed scripts while preserving saved configuration and delivery checkpoints. `Status.cmd` shows local health, `Stop.cmd` stops the connector, and `Start-Background.cmd` resumes it. `Uninstall.cmd` removes its sign-in launch without deleting saved settings or pending work. `Start.cmd` remains available for a visible diagnostic run; Ctrl+C stops that run. A Windows-user/session mutex prevents overlapping scans. The connector never quits Outlook.

Background syncing runs in the signed-in user's Windows session, with bounded retries when Outlook or Arda is temporarily unavailable. It does not run as a Windows service. The computer must remain on and signed in, with the company network available. The **Outlook** button on Quote Status shows heartbeat health and installation instructions; it remains available after setup. The page refreshes connection status and confirmed imported activity automatically while visible, without replacing unsaved edits.

The connector uses `Invoke-RestMethod -UseDefaultCredentials` with a timeout and redirects disabled. It stores no credentials and does not disable TLS certificate validation. Existing Windows execution policy, Outlook programmatic-access protections, and company policy apply. If blocked, use the company's approved signing/support process; there is no policy override or security-setting change in the launcher.

| Configuration property | Default | Accepted range or format |
| --- | --- | --- |
| `baseUrl` | `https://estimating.hub.son4l.local` | Absolute HTTPS URL without user information, query, or fragment |
| `mailbox` | Empty, chosen during setup | SMTP address of exactly one configured Outlook account |
| `pollIntervalSeconds` | `120` | 30-900 |
| `initialLookbackDays` | `30` | 1-90 |
| `maxMessagesPerPoll` | `100` | 4-500 scanned Outlook items total, divided across four scan budgets |
| `maxRetriesPerPoll` | `25` | 1-200 previously attempted messages |
| `requestTimeoutSeconds` | `60` | 10-180 per request |

## Scanning, delivery, and recovery

Each folder has an independent recent-arrival window and history cursor. The default scan budgets are 25 recent Inbox items, 25 recent Sent items, 25 historical Inbox items, and 25 historical Sent items per cycle. Recent windows start at five minutes before first use, overlap by two minutes after completion, and retain their pagination while busy. Historical windows start at the configured lookback and repeat for reconciliation after reaching their end. Thus a large historical mailbox does not consume the recent-arrival budget. Polling pauses after each cycle, so throughput also depends on Outlook synchronization and request duration.

Outlook's date restriction narrows the candidate set, then the connector applies the exact subject parser locally. It accepts case-insensitive `Quote <positive Int32 digits>` optionally followed by ` - <item number>`, with leading/trailing whitespace and repeated `RE:`, `FW:`, or `FWD:` prefixes. The item number may include punctuation and spaces. It rejects unrelated suffix text, blank item suffixes, multiline subjects, subjects over 998 characters, `#` before the quote number, zero, overflow, or multiple quote-number tokens. Unsupported subjects are excluded, not uploaded. The server repeats the same validation. Older connector packages that accept only the bare quote number must be updated before material RFQ subjects can sync.

Discovered candidates enter a persistent queue before a cursor advances. A failed scan retains the failed item's continuation and does not advance its time boundary. New messages receive delivery priority over history; retries have a separate budget ordered by next retry time. Failures, unmatched quotes, and ambiguous responses remain queued with exponential backoff from 30 seconds to one hour, even after they fall outside the rolling lookback. Pending records are never aged out. Outlook entry/store references are refreshed when a later scan locates the same message identity.

Connection, Windows-authentication, redirect, or rate-limit failures pause transport for the rest of the cycle so an outage cannot incur a request timeout for every queued recipient. The remaining queue is preserved for later cycles. A quote-specific permission or matching failure still permits independent correspondence to be processed.

Internet Message ID is the preferred identity. If absent, the connector uses a SHA-256 hash of Store ID, Entry ID, and Conversation ID. State is separated by normalized mailbox and Arda base URL. Successful identities are retained locally for at least 45 days or lookback plus seven days; the server independently deduplicates imports. Moving a message can invalidate its Entry ID; a subsequent scan may recover it when the Internet Message ID remains available. A deleted, moved outside the scanned folders, or protected pending item can remain deferred until its read problem is resolved.

State writes use replacement files and retain a `.bak` copy. Unreadable or mismatched state stops processing instead of resetting checkpoints. Configuration and resume JSON contain mailbox identity, source/entry/store/conversation identifiers, hashes, cursors, timestamps, counters, and safe error codes. They contain no subjects, bodies, attachment names, attachment bytes, or server response bodies. File parsing is capped at 20 MB and the pending queue at 25,000 messages; reaching a cap stops progress without discarding queued items. Retain the original state and backup when investigating recovery.

## Message content and API contract

Exchange senders and recipients resolve through Exchange user/distribution-list SMTP properties, with MAPI SMTP fallback. The connector does not resolve or edit recipients in Outlook. Incoming correspondence uses the sender; outgoing correspondence fans out to each unique To/CC/BCC address except the selected mailbox. It does not infer internal domains or expand distribution lists. All resolved recipients remain in `toAddresses` for server validation.

The connector reads the full plain text body and non-inline attachment bytes into memory before starting any imports for that message. It does not write attachments to disk. The limits are 200,000 body characters, 25 non-inline files, 10 MB per file, and 20 MB total attachment bytes. Hidden images and images referenced by an HTML content ID are excluded. Unavailable binary content or any exceeded limit defers the whole message before transport; bodies and files are never silently truncated. Outlook may not expose embedded, protected, or cloud attachments as by-value binary content.

`POST /api/vendor-quotes/import` sends one correspondent/message pair using `ImportVendorQuoteMessageDto` in `Dtos/VendorQuoteDtos.cs`: `sourceMessageId`, `mailbox`, `direction` (`incoming`/`outgoing`), `subject`, `fromAddress`, `fromName`, `toAddresses`, `vendorEmail`, `vendorName`, `sentAt`, `receivedAt`, `bodyText`, `attachments` (`fileName`, `contentType`, `contentBase64`), `conversationId`, and optional `materialDraftToken`. Only the specific `X-Arda-Material-Draft` header is extracted for that token; the connector does not upload all transport headers.

`imported`, `duplicate`, and `unassigned` are durable success outcomes. `unassigned` means the server stored the correspondence on the quote for review; it must not be retried. Acknowledgements are recorded per correspondent, allowing partially successful outgoing fanout to resume without reposting successful recipients. `unmatched`, `ambiguous`, unexpected responses, and HTTP errors remain retryable. A crash before saving an acknowledgement is safe because backend deduplication is authoritative. Message IDs and mailbox addresses do not grant access to a quote.

Removing or moving an email inside Arda retains its source identity. A repeated automatic import is acknowledged as `duplicate` without restoring the email, undoing its placement, or disclosing the destination quote. This also applies if local connector acknowledgements have expired or are being rebuilt. Restoring removed correspondence is an explicit Arda action; the connector never changes or deletes the original Outlook item.

`POST /api/vendor-quotes/sync` sends a heartbeat with `mailbox`, `clientName`, `importedCount`, `duplicateCount`, `deferredCount`, and an optional safe `error` code. Deferred count is the current pending queue size. The connector does not call administrative endpoints, send email, save items, change read state, move messages, or delete anything in Outlook.

## Verification

```powershell
powershell.exe -NoLogo -NoProfile -File .\scripts\outlook-estimating\Start-OutlookEstimating.ps1 -SelfTest
```

The self-test parses every connector PowerShell file and uses synthetic COM-shaped objects plus fake transport. It covers strict subjects, Exchange SMTP fallback, attachment limits and inline filtering, full-message failure before transport, outgoing fanout, deduplication, pending retry, durable unassigned outcomes, dry-run behavior, HTTPS/default-credential request options, metadata-only restart, bounded pagination, failed-item continuation, and recent mail arriving amid 3,000 older items. It does not activate Outlook, read a real mailbox, or contact Arda.

`-Once` performs one bounded operational cycle. `-Once -DryRun` reads Outlook and validates matching messages without network requests or configuration/state writes. Dry run is operational mailbox access and is separate from synthetic self-test. Exit code 0 means that cycle had no reported error, 1 is fatal setup/local failure, and 2 reports a cycle error or deferred delivery. A bounded successful cycle can still leave history to scan or retries waiting for their next attempt.

Live end-to-end validation requires the intended Windows user, a working classic Outlook profile, and deployed Arda with Windows authentication. Repository self-tests do not establish those environmental prerequisites.

For the company-workstation check after deploying the updated Arda API and client:

1. Download the package from **Quote Status > Connect Outlook**, extract it, and run **Install.cmd**. Confirm that the Outlook status becomes connected.
2. Open a material draft from an appropriate quote. Confirm its recipients, full description, quantities and formatting in classic Outlook before sending it as part of the normal quoting workflow.
3. Once Outlook synchronizes Sent Items and the connector scans, verify one sent email appears on the material RFQ and its status is **Waiting on vendor**.
4. Verify a reply from either listed contact and its supported attachments appear on the same RFQ. Review pricing before explicitly marking **Quote received**.
5. Close and reopen Outlook, then check **Status.cmd** and Arda to confirm the background connector reconnects. No quote number or company mailbox is needed for the repository's synthetic tests.

Run `Start.cmd -Diagnose` to inspect configuration, classic Outlook registration, DNS, and a read-only `GET /api/vendor-quotes/sync` request using the existing Windows identity. Diagnostics never activate Outlook, read mailbox items, persist setup/state, or import correspondence. Output excludes mailbox addresses and server response bodies. Redirects remain disabled and TLS validation remains enabled. The built-in default `https://estimating.hub.son4l.local` requires the company's DNS/network; a local development dashboard does not make that host reachable. Diagnose distinguishes DNS, authentication, authorization, incorrect endpoint and connectivity issues. A passed check does not prove a usable Outlook profile or mail import permissions.

## Material draft handoff

The material RFQ action can download an HTML `.eml` file with `X-Unsent: 1` without the correspondence connector running. Windows must open this downloaded file in classic Outlook; the browser may require the user to click the download or choose **Open with > Outlook (classic)**. The draft keeps all intended vendor contacts in one message and remains editable. Creating/downloading/opening it is not evidence that the user sent it, and does not authorize this connector to send mail. Do not represent a download as a confirmed Outlook launch. Classic Outlook profile and file association behavior require validation on the intended Windows workstation.

Arda stores the draft generation and primary vendor contacts when preparing a material RFQ. Once the connector finds the actual outgoing message in Sent Items, the server can label it **Rates requested** and move that RFQ to **Waiting on vendor**. It requires the current draft token, or a match against the expected generated text, recipients, and preparation time when Outlook omits the header. The subject alone never triggers that status change. Editing the generated text when Outlook also strips the header may require marking the attached sent email **Rates requested** manually.

The outgoing email addressed to both primary-vendor contacts is attached once. Replies from those contacts attach to the same material RFQ when linked to the verified sent request. Their supported attachments are imported with the email. A reply never automatically confirms that pricing was supplied: **Quote received** remains an explicit estimator action. Stale drafts and replayed emails must not reset a newer RFQ decision.

Microsoft references: [Items.Restrict date filtering](https://learn.microsoft.com/en-us/office/vba/api/outlook.items.restrict), [account store default folders](https://learn.microsoft.com/en-us/office/vba/api/outlook.store.getdefaultfolder), [SMTP sender resolution](https://learn.microsoft.com/en-us/office/client-developer/outlook/pia/how-to-get-the-smtp-address-of-the-sender-of-a-mail-item), and [attachment binary property](https://learn.microsoft.com/en-us/office/client-developer/outlook/mapi/pidtagattachdatabinary-canonical-property).
