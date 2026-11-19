# Fulcrum purchasing and estimating timing diagnostics

## Connection failures

The Admin API connection test checks five collection endpoints. Its success does not
verify quote planning, inventory availability, purchase history, or vendor details.
Materials and Outside Processing report the failed operation and actual upstream
HTTP status when available. A local retry pause is reported separately from a new
Fulcrum response.

Server warnings use category `Arda.Fulcrum.Purchasing`, event ID `4201`, and the
incoming request trace identifier. They include fixed endpoint templates, never
credentials, raw request URLs, record IDs, response bodies, or private exceptions.
Existing purchasing drafts remain unchanged when shared coordination defers a read.

An actual upstream failure may start a shared retry delay. Requests made during
that delay must preserve its original deadline; they must not prolong it simply
because another page was opened. The shared concurrency and pacing limits still apply.

## Missing completion timing

Average completion uses `Date to Estimating` through `Estimating Completion Date`,
counting Monday through Friday inclusively. `RFQ Due Date` is the deadline used for
on-time comparisons, not an input to elapsed estimating workdays.

The personal dashboard currently withholds its average if any completed quote in
the cohort lacks verified timing. A completed status alone does not establish a
start or completion date. Never substitute record creation, modification, import,
or due dates for the actual estimating dates.

Use the existing authenticated reads first:

1. `GET /api/quote-history?search=<quote-number>&pageSize=10` shows imported dates.
   Search spans several fields, so select the record with the exact quote number.
2. `GET /api/quote-history/<history-id>/audit` shows recorded import changes.
   Initial creation does not include a separate audit for every date.
3. `GET /api/quote-history/<history-id>/timing-diagnostics` compares the stored row,
   effective field mapping, recent sync outcomes, and fresh quote-list/detail reads.
   This route requires both History access and Admin permission and disables caching.

The timing diagnostic makes two bounded Fulcrum reads for the selected quote and
one paged read of quote date-field definitions (normally one page, at most four
pages of 100 definitions). It returns date-related projections, field names and
bounded definition name/label pairs, not arbitrary field
values, credentials, raw payloads, or exception details. It does not change quote
dates or apply an import; normal credential-use and shared coordination metadata
may still be recorded. SQL administrator access is not required.

If the list and detail disagree, investigate the source API response before changing
sync behavior. If the dates are present but the configured name differs, correct the
mapping. A successful source correction still needs a successful scheduled or manual
sync before it affects Arda. Deployment health alone does not verify that sync.

Fulcrum's [official schema](https://api.fulcrumpro.us/swagger/v1/swagger.json) exposes
quote date definitions through `POST /api/custom-fields/list` with
`{"module":"quote","type":"date"}`. Sync retrieves these once per pull through
the existing shared 20-minute cache. The configured field still takes priority,
including an explicit blank or invalid value. When that field is absent, a unique
definition matching its name or label can identify the actual key present on the
quote. No historical key is assumed to mean an estimating date without that
definition. Ambiguous or incomplete definitions are ignored; unavailable metadata
preserves direct mapping and existing-date fallback with a safe sync warning.

`projectedDates` shows the result of the actual sync mapper, which may retain a
stored fallback. Check `recognizedFields` and `selectedByMapper` to distinguish
an observed source field from that fallback; `definitions` reports metadata status
and safe name/label pairs. The diagnostic requests fresh metadata explicitly and
does not persist these projected dates or alter the average's eligibility rules.
