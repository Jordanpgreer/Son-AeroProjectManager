# Estimating quote status and correspondence

The Quote Status page is the shared history of an estimating quote. It uses persisted `EstimatingQuoteHistory` records, including quotes with no vendor correspondence. Browser-local calculator drafts are not the source of these records.

## Organization

- The quote has an overall Arda status, follow-up date, and dated internal notes.
- Threads organize work by part number and contact. Multiple vendors can quote the same part independently. Threads without a part number appear under General.
- Each thread has its own status, follow-up date, notes, and email conversation.
- The combined quote history includes overall updates and thread updates with their part/contact context. An RFQ sent to Silicone Prime can therefore appear in both that thread's history and the overall quote history.
- Status changes and notes record the authenticated author and server timestamp. Updates require the version originally loaded so one estimator cannot silently overwrite another's work.
- The overall status uses the existing Arda status fields shared with the Quotes Dashboard. Thread statuses do not change the overall status. No workflow automation runs here.

Assigned quote rows on the Quotes Dashboard open the corresponding Quote Status record. The former dashboard dropdown has been removed. Arda status, current status notes, and estimating due-date override are editable directly in the quote overview, including **Use automatic**. **Save changes** and **Discard** appear when details change; no Edit action or modal is required. Existing summary notes remain independent of the append-only activity notes. The automatic estimating date continues to use the existing business-day calculation from the RFQ due date. RFQ due dates remain read-only Fulcrum data.

Direct quote links open a focused record with an **All quotes** return action. The general Quote Status entry retains quote search and selection. Quote Status appears between Quotes Dashboard and Estimate Calculator in both desktop and mobile navigation.

## Email connection

The downloadable Outlook connector uses classic Outlook in the signed-in Windows user's session. It reads matching messages from the configured mailbox's Inbox and Sent Items, preserves mailbox contents and read flags, and sends correspondence to the authenticated Arda API. It requires neither Graph credentials nor a mailbox password. Existing workstation and Outlook policies still apply.

`Quote 4445`, with supported reply/forward prefixes, matches quote 4445 exactly. Matching a quote number never grants access to it. Conversation identity and the contact help choose a thread. Ambiguous correspondence must not be guessed into one of several part threads; it remains available at quote level for explicit placement.

An incoming reply can mark a waiting thread Reply received. It does not prove that pricing was supplied and does not mark a quote accepted, reviewed, or complete. Internal notes remain internal. Follow-up links open Outlook; Arda does not automatically send emails.

Historical replies do not overwrite a newer manual thread status. Imported messages retain their original sent/received time separately from the time they were added to Arda.

Attachments are bounded and downloaded through authenticated endpoints. Email bodies render as text; incoming HTML is not executed. Duplicate messages and retry attempts do not create duplicate activity. Quote and attachment access is checked on direct API entry as well as in the interface.

See [Outlook connector operation](outlook-estimating-connector.md) for setup, limits, troubleshooting, and synthetic checks.

## Installation and release

The Estimating schema initializer adds the quote tracking tables for both SQLite and SQL Server. The Outlook connector files are linked from `scripts/outlook-estimating` into the Estimating build and publish output under `Assets/OutlookConnector`.

The ZIP includes a destination from `OutlookConnector:BaseUrl`, defaulting to `https://estimating.hub.son4l.local`. Set this to the actual HTTPS Estimating address when it differs. The destination is not taken from a request's Host header. The connector is started explicitly by the user; downloading it does not start mailbox access.

Use the normal Hub publish/deploy process. Local previews and builds do not activate a production connection or demonstrate production schema installation.

## Local preview

Run the Estimating API on port 5282 with an explicit disposable SQLite `ConnectionStrings__ModuleAccessStore`, Development authentication against a synthetic user, and `EnterpriseQuoteSync__Enabled=false`. From the Estimating ClientApp, run `npm run dev -- --config tests/quote-status-preview.config.ts` to preview on port 5242. The preview must not point at the original development database.

## Verification on September 10, 2026

- 167 backend tests passed, including permissions, concurrency, shared quote workflow fields, canonical status filters, activity recency, exact subject matching, duplicate imports, ambiguous assignment, attachments, and SQLite schema initialization.
- 122 frontend tests passed, including independent overview/activity drafts and stale-version protection; one native Excel test was skipped. TypeScript, Vite production build, and lint passed.
- The downloaded ZIP passed 60 synthetic assertions in Windows PowerShell 5.1 without Outlook or network access during the self-test.
- Browser checks used a separate SQLite database with synthetic quotes. They covered multiple vendors on one part, combined and scoped history, general quote notes, manual email assignment, vendor search, unsaved-note retention/discard, light/dark themes, and a 390-pixel mobile layout without horizontal document overflow.
- The redesigned page was checked for dashboard row navigation, focused quote links, All quotes navigation, desktop/mobile navigation order, saving status and summary notes, custom estimating due dates, restoring the automatic date, and retaining/discarding unsaved quote edits.
- Inline editing was verified for status, date, and summary saves; preserving an activity draft during a details save and a summary draft during an activity save; retaining edits on a version conflict; and protecting unsaved changes during navigation.
- The Release API was published to a temporary directory with the built frontend in `wwwroot`; the page and authenticated connector ZIP were verified from that API. The sample attachment downloaded with forced-download headers.

No real mailbox was connected or production deployment performed. SQL Server DDL was structurally tested but was not executed against a live SQL Server. Live Outlook validation still depends on the intended user's profile, Windows authentication, and company policy.
