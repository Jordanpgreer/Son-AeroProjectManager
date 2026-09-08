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
| Quality shipper lookup | `POST /api/shipments/list` | `ShipmentsListParameters` using exact `names`; `Skip`, `Take`, and sort are query parameters | Shipment array | View Shipment |
| Quality shipping values | `POST /api/reporting/shipping/list` | Shipping report filter; `Skip`, `Take`, and sort are query parameters | Paged shipment-line reporting rows | View Shipping Report |

Contract safeguards:

- Authentication is `Authorization: Bearer <token>` and the token is never stored in source or application settings.
- `Take` is clamped to Fulcrum's documented maximum of 5,000.
- `Sort.Dir` uses only `ascending` or `descending`; the abbreviated values `asc` and `desc` are invalid.
- Job status filters use only the published values: `draft`, `needsReview`, `approved`, `engineering`, `scheduled`, `inProgress`, `complete`, `cancelled`, and `hold`.
- Reporting quote rows may contain null `id` or `number` values. Such reporting rows cannot be joined and are ignored without discarding the corresponding quote detail.
- Exact job, sales-order, and part-number relationships are verified before quantities are applied.
- Quality records are joined only on an exact shipper-number match. Fulcrum ship-by date, customer, purchase order, shipped status, part quantities, and unit prices are mapped into Arda; no Quality workflow writes back to Fulcrum.
- The Fulcrum browser-record URL is tenant-specific and is not returned by the public API. Production must configure `QualityIntegration:FulcrumShipmentUrlTemplate` as an absolute HTTPS template using `{id}` and optionally `{shipperNumber}` before Arda renders shipper hyperlinks.

## API-generated estimates (September 8, 2026)

The live ITAR OpenAPI schema was checked again for this workflow. All requests are
read-only in Fulcrum; the public credential stays on the Arda server.

| Use | Method and path | Contract details |
| --- | --- | --- |
| Recheck source quote and current assignment | `GET /api/quotes/{quoteId}` | `number`, `status`, configured estimating custom fields |
| Individual parts and quoted quantities | `POST /api/quotes/{quoteId}/part-line-items/list` | Entire line array; each line retains its own ID and quantity |
| Available stock | `GET /api/inventory/availableByItem` | Dictionary keyed by item ID; the endpoint explicitly documents an omitted ID as zero; malformed values fail generation |
| Part identity and Make/Buy | `GET /api/items/{itemId}` | `number`, nested `revision.revision`, `itemOrigin`, notes, units and vendor details |
| Routing steps | `POST /api/items/{itemId}/routing/operations/list` | Paged by `skip`/`take`; normal operation setup/labor/machine time and outside-processing costs |
| Components | `POST /api/items/{itemId}/routing/input-items/list` | `requires` uses `valueTypeUnits`; `creates` uses its reciprocal; `make` and `makeOrBuy` recurse |
| Material definitions/nesting | `POST /api/items/{itemId}/routing/input-materials/list` | Retain material specification, dimensions, costing method and nesting; unresolved purchasing units/cost require review |

The current `QuotePartLineItemDto` has no quote-specific price-break array, and
there is no quote-specific price-break endpoint in the published schema. Arda
does not substitute unrelated item/catalog sales-price tiers. It imports the
line quantity, accepts an additive `priceBreaks` array if Fulcrum later exposes
one, and otherwise clearly warns users to verify suggested quantities against
the Fulcrum Pricing panel.

Fixed setup/labor times remain per-lot; per-unit times remain per-unit;
`unitsPerHour` converts to `60 / value` minutes per unit. Machine times are
retained separately in routing notes and are not double-counted as labor.
Unknown operation rules preserve the Fulcrum title and times with a review
warning. Material-line associations cannot be established uniquely from the
published input-item schema, so raw material nesting is retained with explicit
costing/reconciliation warnings rather than charging selected material twice.

Generation requires View History, Manage Inputs, and Manage Quotes plus an
unambiguous assignment to the requester in both Arda and the freshly fetched
Fulcrum quote. Circular BOMs, unsafe import sizes, missing identifiers, invalid
quantity bases, unsupported time units and unavailable API permissions stop
generation instead of returning a silently truncated estimate.
