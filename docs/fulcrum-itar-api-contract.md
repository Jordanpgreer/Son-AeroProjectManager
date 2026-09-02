# Fulcrum ITAR API contract

This integration targets Fulcrum's ITAR public API at `https://api.fulcrumpro.us/`.
The contract was checked against the live ITAR OpenAPI schema at
`https://api.fulcrumpro.us/swagger/v1/swagger.json` on September 2, 2026.

| Use | Method and path | Request | Response | Required Fulcrum permission |
| --- | --- | --- | --- | --- |
| Connection test and quote reporting | `POST /api/reporting/quote/list` | Optional `DtoReportingQuoteReportFilter`; `Skip` and `Take` are query parameters | Paged reporting rows | View Quote |
| Quote details | `POST /api/quotes/list` | Optional `QuoteRequestFindParameters` | Quote array | View Quote |
| Project job lookup | `POST /api/jobs/list` | `JobRequestFindParameters` using `numbers`, `jobNames`, or `statuses` | Job array | View Job |
| Project sales-order lookup | `POST /api/sales-orders/list` | `SalesOrderRequestFindParameters` using `numbers` | Sales-order array | View Sales Order |
| Required quantity | `GET /api/sales-orders/{salesOrderId}/part-line-items/{lineItemId}` | Path identifiers from the matched job | Sales-order part line | View Sales Order |

Contract safeguards:

- Authentication is `Authorization: Bearer <token>` and the token is never stored in source or application settings.
- `Take` is clamped to Fulcrum's documented maximum of 5,000.
- `Sort.Dir` uses only `ascending` or `descending`; the abbreviated values `asc` and `desc` are invalid.
- Job status filters use only the published values: `draft`, `needsReview`, `approved`, `engineering`, `scheduled`, `inProgress`, `complete`, `cancelled`, and `hold`.
- Reporting quote rows may contain null `id` or `number` values. Such reporting rows cannot be joined and are ignored without discarding the corresponding quote detail.
- Exact job, sales-order, and part-number relationships are verified before quantities are applied.
