# Arda Outlook quote correspondence connector

This connects your classic Outlook Inbox and Sent Items to **Quote Status** in Arda Estimating. It runs in a visible window while you are signed in to Windows. Keep Outlook signed in and synchronized.

## Start

1. Download the connector from Arda and extract the entire ZIP into a folder you can keep.
2. Open **Start.cmd** normally. Do not use **Run as administrator**.
3. Confirm the HTTPS Arda address. If Outlook has multiple accounts, enter the SMTP address of your own mailbox from the displayed list.
4. Leave the window open. Open **Quote Status** in Arda to review imported correspondence. Press **Ctrl+C** in the connector window to stop it.

The package can provide `configuration.json` with the Arda address already filled in. Otherwise the example defaults to `https://estimating.hub.son4l.local`. The connector saves your choice locally and uses it next time. Run `Start.cmd -Setup` to choose again.

Use your existing Windows account. There is no password or API key to enter. Classic Outlook must already have a working profile, and your Windows account must have access to the relevant quotes in Arda. New Outlook, Outlook on the web, services, shared folders, and delegated mailbox discovery are not supported by this connector.

If Windows or Outlook blocks programmatic access, follow your company's approved signing or support process. The connector does not change execution policy, Outlook security settings, certificate validation, or permissions.

## Which messages are imported

The subject must be exactly `Quote` followed by a positive quote number, allowing spaces and repeated reply/forward prefixes. For example:

- `Quote 12345`
- `RE: Quote 12345`
- `FW: RE: FWD: Quote 12345`

`Quote #12345`, `Quote 12345 pricing`, and subjects containing two quote numbers do not match. The quote must exist in Arda, and you must have access to it.

Incoming messages use the sender's SMTP address. Sent messages are associated with each unique To, CC, or BCC recipient except the selected mailbox itself. All resolved recipient addresses accompany the message. The server uses the quote number, correspondent, and conversation to place correspondence. Messages that cannot be assigned to one request can appear on the quote for review. Importing an email does not change the overall quote status.

The first run looks back 30 days. Recent arrivals are scanned before a separate, gradual history scan, every two minutes by default. Actual availability also depends on Outlook synchronization, mailbox volume, and connectivity. Only the selected account's Inbox and Sent Items are scanned; subfolders and archive stores are not included.

## Status and retry

The window reports imported, duplicate, and pending counts. A pending message stays queued when the quote is unavailable, access is denied, Outlook cannot read it, the server is unavailable, or its content exceeds a limit. Retries wait between 30 seconds and one hour. A restart resumes the queue. A failed item is never acknowledged as a complete import.

Attachments are limited to **10 MB each, 20 MB per message, and 25 files**. Inline images are omitted. Plain text bodies are limited to **200,000 characters**. If any required attachment is oversized or unavailable, the complete message is deferred before sending any part of it. Some protected, embedded, or cloud attachments cannot expose their bytes through Outlook and need attention outside the connector.

The connector does not send mail, mark messages read, edit, move, or delete mailbox items. Body text and attachment bytes are held in memory for the HTTPS request; they are not exported to local files or written to its status output. Resume files contain only message identities, timestamps, counters, and safe error codes under `%LOCALAPPDATA%\Arda\OutlookEstimating`.

## Optional commands

Run these from a command window in the extracted folder:

```bat
Start.cmd -Once
Start.cmd -Once -DryRun
Start.cmd -Setup
Start.cmd -SelfTest
```

`-Once` performs one bounded scan and delivery cycle. `-DryRun` still reads matching Outlook messages, but makes no Arda requests and writes no configuration or delivery state. `-SelfTest` uses synthetic objects only: it does not open Outlook or contact a server.

Only one connector window can run for your Windows user in the current session. Keep the metadata files when updating the extracted scripts. If a metadata file cannot be read, the connector stops rather than silently losing pending work; retain that file and its `.bak` copy for support.
