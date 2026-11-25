# Quotes dashboard

The Quotes dashboard searches the imported Arda quote history. Searching and changing filters do not call Fulcrum or change source records.

## Quotes and performance

- **My quotes** uses Arda's existing unambiguous estimator identity matching.
- **All quotes** requires the existing Quote Dashboard and History View permissions. It includes all estimators' quote metadata; it does not grant access to another estimator's correspondence or editing actions.
- The Quotes tab starts with **Assigned to: Me** and **Status: Active quotes**. Separate filter chips support all eligible estimators or an individual estimator, plus overdue/completed groups and individual Arda statuses, subject to existing permissions.
- The Quotes tab uses **All time**. The dashboard API retains its optional inclusive RFQ Due Date bounds for existing callers; the current Quotes tab omits them, so undated quotes remain eligible.
- Performance has its own tab and read-only endpoint. It starts with My performance; authorized users may select all eligible estimators or an individual estimator. Unassigned/placeholder estimator names are excluded from team performance.
- Performance is independent of the Quotes tab's search, status, and pagination. It starts with All time and offers a month/year picker. Selected months use RFQ Due Date; undated quotes are excluded from a selected month. The estimator roster and ownership matching still use all history.

## Metrics

- **Active:** quotes not marked completed in imported history.
- **Overdue:** active quotes whose RFQ Due Date is before today. This dashboard uses the customer-facing RFQ deadline; the separate internal estimating deadline remains unchanged.
- **Completed:** quotes marked completed in imported history.
- **Average completion:** the existing verified estimating workday calculation, from Date to Estimating through Estimating Completion Date. Incomplete timing coverage leaves the overall average unavailable.
- **On-time %:** completed quotes whose Estimating Completion Date is on or before RFQ Due Date, divided by completed quotes with valid dates for that comparison, multiplied by 100. Comparisons use calendar dates, so finishing on the due date is on time. Missing/invalid dates and future completion dates are excluded and disclosed; they are not counted as late. An empty eligible sample displays no percentage.
- **Won / Lost:** the normalized Fulcrum outcomes and total quoted values within the selected performance scope.

## Results and details

Live search matches quote number, customer, and imported part numbers across the full filtered dataset, before pagination. Missing part metadata is shown as unavailable; no part number is invented. Quote links open the existing Quote Status page when permitted. The former dashboard detail pane and inline performance strip have been removed.

The dashboard and performance endpoints are read-only. Existing Quote Status, estimate generation, and editing permissions continue to control their actions independently. Restricted notes are not returned for another estimator's quote merely because All quotes is selected.

## Quoted quantities

The Quote Status page consolidates quoted parts and quantity tiers in one table. Quantity confirmation stores all selected tiers and remains compatible with earlier minimum/maximum selections. Material demand uses the selected bounds; outside-processing requests retain every verified quantity tier.

An OP request based on complete Fulcrum tiers can be prepared independently. Automatic progression to RFQ Sent still requires verified material quantities whenever material requirements exist, and all required material and OP requests must be sent. Stock coverage based only on an unconfirmed base-line quantity cannot establish coverage for larger tiers.

## Correspondence and RFQ tracking

Automatic Outlook sync skips internal correspondent copies based on the mailbox domain and configured `QuoteMailImport.InternalDomains` (the packaged base setting includes `sonaero.com`). External copies retain their original recipients; deliberate manual imports remain supported.

Removing RFQ tracking keeps the email in the Activity Ledger as quote correspondence. Empty automatically created correspondence threads disappear from the RFQ list, while intentional RFQ activity remains visible. Both tracking-removal entry points use the existing unsaved-changes guard before opening their confirmation.

## Verification boundary

Local browser fixtures under `ClientApp/output/` use synthetic responses and cannot establish production performance or sync status. Service and endpoint tests validate the actual backend separately. The tracking-removal regression uses the real detail panel and draft guard with mocked requests, checking Cancel, Discard, confirmation, and preservation of the email in the ledger.
