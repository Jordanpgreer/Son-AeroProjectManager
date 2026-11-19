# Quotes dashboard

The Quotes dashboard searches the imported Arda quote history. Searching and changing filters do not call Fulcrum or change source records.

## Scope and dates

- **My quotes** uses Arda's existing unambiguous estimator identity matching.
- **All quotes** requires the existing Quote Dashboard and History View permissions. It includes all estimators' quote metadata; it does not grant access to another estimator's correspondence or editing actions.
- The initial period is the current calendar month in the browser's local calendar. This is not a rolling 30-day window.
- Periods use **RFQ Due Date**, inclusive of both endpoints. Presets cover this month, last month, this quarter, this year, all time, and a custom range.
- Undated quotes appear under All time. They do not silently enter a dated period.
- Performance uses the selected scope and period. Search, status, and pagination narrow the results table without changing that performance cohort.

## Metrics

- **Active:** quotes not marked completed in imported history.
- **Overdue:** active quotes whose RFQ Due Date is before today. This dashboard uses the customer-facing RFQ deadline; the separate internal estimating deadline remains unchanged.
- **Completed:** quotes marked completed in imported history.
- **Average completion:** the existing verified estimating workday calculation, from Date to Estimating through Estimating Completion Date. Incomplete timing coverage leaves the overall average unavailable.
- **On-time %:** completed quotes whose Estimating Completion Date is on or before RFQ Due Date, divided by completed quotes with valid dates for that comparison, multiplied by 100. Comparisons use calendar dates, so finishing on the due date is on time. Missing/invalid dates and future completion dates are excluded and disclosed; they are not counted as late. An empty eligible sample displays no percentage.
- **Won / Lost:** the normalized Fulcrum outcomes and total quoted values within the same scope and RFQ-due-date period.

## Results and details

Live search matches quote number, customer, and imported part numbers across the full filtered dataset, before pagination. Missing part metadata is shown as unavailable; no part number is invented. The details panel starts closed. Selecting a row opens it; closing it restores the full-width table.

The dashboard endpoint is read-only. Existing Quote Status, estimate generation, and editing permissions continue to control their actions independently. Restricted notes are not returned for another estimator's quote merely because All quotes is selected.

## Verification boundary

The local browser preview under `ClientApp/output/dashboard-preview/` uses synthetic responses and cannot access the live database. Service and endpoint tests validate the actual backend separately. Browser-preview values are not production performance results.
