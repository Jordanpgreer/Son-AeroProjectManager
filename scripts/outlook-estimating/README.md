# Arda Outlook quote correspondence connector

This connects your classic Outlook Inbox and Sent Items to **Quote Status** in Arda Estimating. Install it once for your Windows account; it then runs in the background and starts when you sign in. Keep classic Outlook open, signed in and synchronized, and stay connected to the company network/VPN.

## Install once

1. Download the connector from Arda and extract the entire ZIP.
2. Open **Install.cmd** normally. Do not use **Run as administrator**.
3. Confirm the HTTPS Arda address. If Outlook has multiple accounts, enter the SMTP address of your own mailbox from the displayed list.
4. After setup succeeds, close the installer window. The connector starts in the background. Open **Quote Status** in Arda to see its heartbeat and imported correspondence.

Setup reads account choices but does not scan folders or import messages. Only after setup finishes does installation register startup and launch the connector. Program files are copied to `%LOCALAPPDATA%\Arda\OutlookEstimating\Connector`; the extracted ZIP folder can then be removed. Installing an updated package preserves your saved setup, pending queue and backups, and reconfirms the address. No administrator rights, service, scheduled task or stored password is required.

The background connector waits quietly when Outlook is closed, and reconnects when it is opened. It never opens Outlook or an account/profile prompt in the background. If the server/network is unavailable, pending messages remain queued for retry. Existing company execution policy and Outlook programmatic-access protections still apply.

The package provides `configuration.json` with the Arda address already filled in. Otherwise the example defaults to `https://estimating.hub.son4l.local`. Re-run **Install.cmd** to repair or change the saved address/account. The installer requests a graceful stop before updating. If an in-flight request has not finished yet, wait and run it again; it never force-kills Outlook or unrelated processes.

Use your existing Windows account. There is no password or API key to enter. Classic Outlook must already have a working profile, and your Windows account must have access to the relevant quotes in Arda. New Outlook, Outlook on the web, services, shared folders, and delegated mailbox discovery are not supported by this connector.

If Windows or Outlook blocks programmatic access, follow your company's approved signing or support process. The connector does not change execution policy, Outlook security settings, certificate validation, or permissions.

## Which messages are imported

The subject must be `Quote` followed by a positive quote number, optionally followed by ` - item number`, allowing spaces and repeated reply/forward prefixes. For example:

- `Quote 12345`
- `RE: Quote 12345`
- `FW: RE: FWD: Quote 12345`
- `Quote 12345 - RM004420`
- `RE: Quote 12345 - RM004420`

`Quote #12345`, `Quote 12345 pricing`, blank item suffixes, multiline subjects, and subjects containing two quote numbers do not match. The quote must exist in Arda, and you must have access to it.

Incoming messages use the sender's SMTP address. Sent messages include all resolved To, CC, and BCC recipients except the selected mailbox itself as correspondents. The server uses the quote number, item, correspondent and conversation to place correspondence. A preserved `X-Arda-Material-Draft` header also supplies an opaque draft token for server verification. Messages that cannot be assigned confidently appear on the quote for review. Only a verified message found in Sent Items can confirm a material RFQ was sent; downloading/opening a draft cannot. Importing an email does not change the overall quote status.

The first run looks back 30 days. Recent arrivals are scanned before a separate, gradual history scan, every two minutes by default. Actual availability also depends on Outlook synchronization, mailbox volume, and connectivity. Only the selected account's Inbox and Sent Items are scanned; subfolders and archive stores are not included.

## Status and retry

**Status.cmd** reports whether sign-in start is enabled, whether the connector is running, and its last phase/error and pending count. **Stop.cmd** requests a graceful pause. **Start-Background.cmd** resumes it. **Uninstall.cmd** stops the runner and removes sign-in startup while preserving setup, scripts, pending queue and backups. Stop pauses until you start it again or next sign-in; use Uninstall to disable future sign-in runs.

A pending message stays queued when the quote is unavailable, access is denied, Outlook cannot read it, the server is unavailable, or its content exceeds a limit. Delivery retries wait between 30 seconds and one hour. Outlook reconnection retries wait between 30 seconds and five minutes. A restart resumes the queue. A failed item is never acknowledged as a complete import.

Attachments are limited to **10 MB each, 20 MB per message, and 25 files**. Inline images are omitted. Plain text bodies are limited to **200,000 characters**. If any required attachment is oversized or unavailable, the complete message is deferred before sending any part of it. Some protected, embedded, or cloud attachments cannot expose their bytes through Outlook and need attention outside the connector.

The connector does not send mail, save drafts, mark messages read, edit, move, or delete mailbox items. Body text, headers and attachment bytes are held in memory for the HTTPS request; they are not exported to local files or written to its status output. Resume files contain only message identities, timestamps, counters, and safe error codes under `%LOCALAPPDATA%\Arda\OutlookEstimating`. `runner.json` and the bounded `connector.log` contain process/timing metadata, counts and safe error codes. Logs rotate at 512 KB with one previous file.

## Optional commands

Run these from a command window in the extracted folder:

```bat
Start.cmd -Once
Start.cmd -Once -DryRun
Start.cmd -Setup
Start.cmd -SelfTest
Start.cmd -Diagnose
```

`-Once` performs one bounded scan and delivery cycle. `-DryRun` still reads matching Outlook messages, but makes no Arda requests and writes no configuration or delivery state. `-SelfTest` uses synthetic objects only: it does not open Outlook or contact a server.

**Start.cmd** without arguments remains available for a visible manual run, stopped with Ctrl+C. Stop the installed background runner first: only one runner can operate for your Windows user/session. `-SelfTest` also exercises installation and lifecycle with temporary files and stubbed startup/launch callbacks; it does not modify your real sign-in settings.

`-Diagnose` checks saved setup, classic Outlook registration, DNS, and a read-only Windows-authenticated Arda endpoint. It never opens Outlook, reads mail, imports correspondence, or changes configuration. It prints no mailbox addresses or server response bodies. If the Arda address cannot resolve, connect to your company network/VPN or run `Start.cmd -Setup` with the correct HTTPS Estimating address. A successful diagnostic does not verify your Outlook profile; open classic Outlook and confirm the intended account yourself.

## Material quote drafts

Arda's material quote action downloads an unsent `.eml` draft independently of this correspondence connector. Open the downloaded file with **Outlook (classic)** to edit the recipients, subject and formatted body, then send it yourself. If Windows opens another mail app, use **Open with** to select classic Outlook. Downloading or opening the draft does not send it or confirm that it was sent. The connector must be running to import the sent message and vendor replies. If Outlook removes the tracking header, Arda verifies the generated message's subject, template body, recipients and preparation time; edits that prevent a confident match remain available for manual review instead of guessing that the request was sent.

Only one connector window can run for your Windows user in the current session. Keep the metadata files when updating the extracted scripts. If a metadata file cannot be read, the connector stops rather than silently losing pending work; retain that file and its `.bak` copy for support.
