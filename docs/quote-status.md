# Estimating quote status and correspondence

The Quote Status page is the shared history of an estimating quote. It uses persisted `EstimatingQuoteHistory` records, including quotes with no vendor correspondence. Browser-local calculator drafts are not the source of these records.

## Organization

- The quote has an overall Arda status, follow-up date, and dated internal notes.
- Threads organize work by part number and contact. Multiple vendors can quote the same part independently. Threads without a part number appear under General.
- Each thread has its own status, follow-up date, notes, and email conversation.
- The combined quote history includes overall updates and thread updates with their part/contact context. An RFQ sent to Silicone Prime can therefore appear in both that thread's history and the overall quote history.
- Status changes and notes record the authenticated author and server timestamp. Updates require the version originally loaded so one estimator cannot silently overwrite another's work.
- The overall status uses the existing Arda status fields shared with the Quotes Dashboard. Thread statuses do not change the overall status. Explicitly marking a sent email **Rates requested** changes only its assigned RFQ to **Waiting on vendor**.

Assigned quote rows on the Quotes Dashboard open the corresponding Quote Status record. The detail page follows the request layout: Request Details and one Activity Timeline on the left, with Outside Processing and Materials on the right. There is no quote sidebar or Activity / Parts & threads / Emails / Removed tab bar. **Back to Quotes** returns to a separate searchable list.

Request Details shows the client, the actual Fulcrum Sales Person (separate from the estimating representative), estimating due date, RFQ due date, and last update. An emphasized **Status** field beneath RFQ due date shows the saved overall quote status. The quote header contains the quote number without repeating its customer or status. Estimating due-date overrides and **Use automatic** remain available. Current summary notes are in a compact disclosure, independent of timeline entries. **Save changes** and **Discard** appear only for unsaved details. The automatic estimating date continues to use the existing business-day calculation from the RFQ due date; RFQ dates remain read-only Fulcrum data.

The compact **Add Entry** form sits at the top of the right column, above Outside Processing and Materials, and saves an optional note with the selected quote or RFQ status. On mobile it follows Request Details and precedes the RFQs and timeline. Choose its destination using **Add entry to**, or open an RFQ from the right-hand panel. The timeline always retains the combined quote history. Each compact row opens **Entry Details** using a pointer-click affordance, including full notes, status changes, emails, and downloadable email attachments. Notes and status audit events remain independently reviewable and removable notes do not remove their status changes.

The timeline initially shows the five newest entries. **Show all** expands the complete history; **Show less** returns to five. Records with five or fewer entries need no toggle.

Outside Processing and Materials lists the existing RFQ threads with exactly three statuses: **Untouched**, **Waiting on vendor**, and **Quote received**. Its status selectors save immediately and add the status change to the main timeline. A vendor/contact and RFQ name are required; email is optional. Website/catalog pricing can use a thread without an email, with a safe HTTP(S) link or a file/catalog reference in its notes, and be marked **Quote received** when pricing is available. Standalone file upload is not implemented; attachments from imported emails remain supported. The ready count includes only Quote received.

Legacy current statuses are normalized for display without rewriting historical events: Rates requested and Reply received show as Waiting on vendor; Under review and Accepted show as Quote received; Declined and Cancelled show as Untouched. New status writes accept only the three current choices.

Direct quote links open a focused record with a **Back to Quotes** return action. The general Quote Status entry retains quote search and selection. Quote Status appears between Quotes Dashboard and Estimate Calculator in both desktop and mobile navigation.

## Email connection

The downloadable Outlook connector uses classic Outlook in the signed-in Windows user's session. It reads matching messages from the configured mailbox's Inbox and Sent Items, preserves mailbox contents and read flags, and sends correspondence to the authenticated Arda API. It requires neither Graph credentials nor a mailbox password. Existing workstation and Outlook policies still apply.

`Quote 4445`, with supported reply/forward prefixes, matches quote 4445 exactly. Matching a quote number never grants access to it. Conversation identity and the contact help choose a thread. Ambiguous correspondence must not be guessed into one of several part threads; it remains available at quote level for explicit placement.

Incoming replies and ordinary sent emails leave RFQ status unchanged. An incoming reply does not prove that pricing was supplied; an estimator marks **Quote received** when the pricing is actually available. Internal notes remain internal. Follow-up links open Outlook; Arda does not automatically send emails.

In Entry Details, an assigned sent email can be marked **Rates requested**. This adds an email label and sets its RFQ to **Waiting on vendor** in one save, including author/time audit events. The original subject and body are preserved. Removing the label does not reset the RFQ status. Repeating the same label action, duplicate imports, moving, removing, and restoring the email do not reapply its status change. Imported messages retain their original sent/received time separately from the time they were added to Arda.

Attachments are bounded and downloaded through authenticated endpoints. Email bodies render as text; incoming HTML is not executed. Duplicate messages and retry attempts do not create duplicate activity. Quote and attachment access is checked on direct API entry as well as in the interface.

See [Outlook connector operation](outlook-estimating-connector.md) for setup, limits, troubleshooting, and synthetic checks.

## Manual email import

Use **Attach email** or drop a saved `.msg` or `.eml` message into the quote's email import area. Review its original sender, subject, date, attachments, and destination before importing. Choose Received or Sent; for a sent email, choose the relevant recipient. An optional internal note can accompany the import. A selected part/vendor thread provides the destination; importing at quote level keeps the email on that quote for later organization.

For a sent email, the optional **Rates requested** checkbox requires a selected RFQ. Importing with it checked saves the label and moves that RFQ to **Waiting on vendor** together. It is never inferred from an email subject or from every sent message; incoming emails cannot receive the label.

Manual import uses the selected quote, so a missing or incorrect quote number in the original subject does not block it. Automatic Outlook matching retains its strict subject rule. Original email content is not rewritten to manufacture a match. Received and sent activity labels describe the direction and correspondent; Arda does not infer that an email is an RFQ or a completed vendor quote.

The server checks quote and thread access, validates the file and message, and records the importing user and time separately from the email's original date. Previewing a file does not save it to the quote. Duplicate message detection covers repeated file imports and messages with matching Internet Message IDs received from the connector. The manual import does not require a running Outlook connector or new mailbox permissions.

Direct dragging from an Outlook window depends on whether the browser receives an email file. If it receives no usable file, save the message in Outlook first and attach that file. Local file-upload tests do not establish native Outlook-to-browser drag support on every workstation.

## Correcting and removing records

Emails and internal notes have a visible trash action beside their timeline entry, followed by a confirmation. Editing notes and moving emails are available inside Entry Details. **Move email** changes its quote or part/vendor thread while preserving the original subject, message dates, body, and attachments. The user must have editing access to both quotes, and a vendor thread must match the email's correspondent. Moving correspondence does not change either quote's status or either thread's status.

**Remove from Arda** hides an email from active correspondence. The original Outlook email is untouched. **Remove note** hides an internal note from the active timeline. Removed items remain recoverable through **Undo** or the header's **Removed items** dialog; removal is not permanent erasure. Attachment downloads are unavailable while their email is removed. Internal notes can be edited, with their original author/date retained and the correction recorded in history. Automatic status and audit events are not editable notes.

Removal and restoration require the existing **Delete quotes** permission (`estimating.quotes.delete`) in addition to editing access to the quote. Note edits and email moves require quote editing access. Preview and read-only users cannot mutate records. These checks apply to direct API calls as well as the interface; no existing group permissions are automatically expanded by this feature.

Every removal, restoration, move, and note edit records the acting user and time. Changes use the loaded quote version to prevent stale actions from overwriting newer work. The source identity of a removed or moved email remains reserved, so Outlook reconciliation does not recreate or return the same identified email to its former location. An explicit restore is required to recover a removed email. Identity matching retains the limitations documented for manual imports when an original Internet Message ID is absent.

## Installation and release

The Estimating schema initializer adds the quote tracking tables for both SQLite and SQL Server, including an additive IsRateRequest flag that defaults to false for existing emails. The Outlook connector files are linked from `scripts/outlook-estimating` into the Estimating build and publish output under `Assets/OutlookConnector`.

The ZIP includes a destination from `OutlookConnector:BaseUrl`, defaulting to `https://estimating.hub.son4l.local`. Set this to the actual HTTPS Estimating address when it differs. The destination is not taken from a request's Host header. The connector is started explicitly by the user; downloading it does not start mailbox access.

Use the normal Hub publish/deploy process. Local previews and builds do not activate a production connection or demonstrate production schema installation.

## Local preview

Run the Estimating API on port 5282 with an explicit disposable SQLite `ConnectionStrings__ModuleAccessStore`, Development authentication against a synthetic user, and `EnterpriseQuoteSync__Enabled=false`. From the Estimating ClientApp, run `npm run dev -- --config tests/quote-status-preview.config.ts` to preview on port 5242. The preview must not point at the original development database.

## Request-layout verification on September 16, 2026

- RFQ status follow-up: production frontend build and lint passed; 149 frontend tests passed with one native Excel skip. Full backend suite: 241 passed, including canonical statuses, email-label permissions/concurrency, atomic manual imports, duplicate protection, and repeatable SQLite schema upgrades.
- Browser checks confirmed the three RFQ choices, legacy current-status normalization, marking an existing sent email, reload persistence, and clearing the label while retaining Quote received. The manual import checkbox required a selected RFQ and saved its label and Waiting on vendor status together; overall quote status remained unchanged. Incoming email details offered no Rates requested action. Mobile light/dark email details at 390 pixels had no horizontal overflow. Reload returned the timeline to five entries. These checks used only the disposable synthetic preview.

- Production frontend build and lint passed. Frontend suite: 144 passed, one native Excel test skipped. Related backend suite: 43 passed, including the true salesperson mapping and atomic status/note contracts.
- Browser verification used a new, isolated synthetic SQLite database, not the existing development database. Checked separate quote list/detail navigation, empty records, RFQ creation without email, immediate RFQ status updates, quote and RFQ status/note saves, and preservation of independent drafts during details saves, RFQ updates, and email imports.
- Confirmed note removal and Undo, email removal and restoration, full email details and attachment downloads, single timeline display of imported messages, pricing reference links, and navigation draft protection. A simulated preview user had no mutation controls.
- Light desktop and 390-pixel light/dark mobile layouts were inspected, including entry/RFQ dialogs, without document horizontal overflow. No application exceptions were observed. The isolated preview still logs existing cross-origin notification-presence failures to the separate Portal on port 5140; notification delivery was not part of this verification.
- This is a local implementation and synthetic preview; no release/deployment or real mailbox actions were performed.

## Verification on September 10, 2026

- 200 backend tests passed, including permissions, concurrency, shared quote workflow fields, canonical status filters, activity recency, exact subject matching, duplicate imports, ambiguous assignment, attachments, SQLite schema initialization, 19 manual email regressions, and 14 record lifecycle regressions.
- 133 frontend tests passed, including independent overview/activity drafts, email file validation, thread/correspondent matching, lifecycle request versions, immutable automatic activity, keyboard-menu behavior, and stale-version protection; one native Excel test was skipped. TypeScript, Vite production build, and lint passed.
- The downloaded ZIP passed 60 synthetic assertions in Windows PowerShell 5.1 without Outlook or network access during the self-test.
- Browser checks used a separate SQLite database with synthetic quotes. They covered multiple vendors on one part, combined and scoped history, general quote notes, manual email assignment, vendor search, unsaved-note retention/discard, light/dark themes, and a 390-pixel mobile layout without horizontal document overflow.
- The redesigned page was checked for dashboard row navigation, focused quote links, All quotes navigation, desktop/mobile navigation order, saving status and summary notes, custom estimating due dates, restoring the automatic date, and retaining/discarding unsaved quote edits.
- Inline editing was verified for status, date, and summary saves; preserving an activity draft during a details save and a summary draft during an activity save; retaining edits on a version conflict; and protecting unsaved changes during navigation.
- Manual email browser checks covered received and sent `.eml` imports, a valid Outlook `.msg` import, incorrect subjects, explicit vendor-thread placement, optional notes, duplicate rejection, preservation of both unsaved drafts, original message dates, and attachment download. The review dialog was checked in light/dark themes and at 390 pixels without horizontal document overflow. Unsupported files and malformed messages were rejected; uploads without the required request header returned 403. No native Outlook drag was performed.
- Record lifecycle browser checks covered editing and removing a saved note while both existing drafts were retained, immediate Undo, email removal surviving reload, restoration from Removed items, quote search and cross-quote moves, destination email counts, matching vendor-thread choices, and light/dark mobile dialogs without document overflow. The final built preview logged no browser warnings or errors.
- Live API checks on the disposable preview confirmed removal permission denial, removed attachment denial and restored download, repeated automatic import suppression after removal/move, wrong-vendor and stale-version rejection, quote/thread note author and date retention, and unchanged statuses. The populated preview database upgraded successfully; automated tests also repeated the legacy SQLite upgrade and checked atomic concurrency rollback and SQL Server upgrade guards.
- The Release API was published to a temporary directory with the built frontend in `wwwroot`; the page and authenticated connector ZIP were verified from that API. The sample attachment downloaded with forced-download headers.

No real mailbox was connected or production deployment performed. SQL Server DDL was structurally tested but was not executed against a live SQL Server. Live Outlook validation still depends on the intended user's profile, Windows authentication, and company policy.
