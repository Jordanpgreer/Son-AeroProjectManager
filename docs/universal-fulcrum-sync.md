# Shared Fulcrum synchronization

Arda uses one 20-minute cadence for the four modules that read Fulcrum. The schedule runs at
:00, :20, and :40; it does not make a startup API burst. Portal and Engineering currently have no
Fulcrum reader to schedule. Browser revalidation reads Arda's own records, independently of this
upstream schedule.

This is a common schedule and shared read transport, not one Fulcrum HTTP endpoint. Fulcrum exposes
quotes, items, jobs, shipping, vendors, and their details through separate paginated endpoints.

## Module audit and ownership

| Module | Fulcrum reads | Automatic projection |
| --- | --- | --- |
| Project Tracker | Items/list/v2; jobs/list; sales-orders/list; reporting/sales-order-lines/list; job-tracking-timers/list; SO part lines; job operations | Lookup catalog plus active, identified projects. Normal routing synchronization preserves named/Arda-only steps and manual quantities. Each project has its own version/concurrency guard and transaction. |
| Estimating | Reporting quote list and quote list; active quote part lines; items; routing operations/input items | Shared quote history and active quote production summaries. Arda-owned status, notes, due-date overrides, and local drafts remain Arda-owned. |
| Quality | Shipments/list by requested names; reporting/shipping/list; shipment-line-items/list; SO part lines | Existing shipping records, canonical `SHP-SO…-…` identity and verified Fulcrum record URL, shipping state and parts. |
| Small Business Subcontracting | Vendors/list and each vendor's contacts/list | Vendor/contact snapshots. Local compliance classifications, certifications, documents, and related decisions are preserved. |
| Engineering | No configured Fulcrum reader | None. |
| Portal/Admin | Credential management/test and shared application data | No business-data pull. Connection tests remain explicit administrative actions. |

Code locations: `ProjectQuantityProviders.cs`, `ProjectQuantityScheduledSyncService.cs`,
`FulcrumQuoteClient.cs`, `EnterpriseQuoteSyncProviders.cs`, `FulcrumQuoteInspectionService.cs`,
`QualityShipmentProviders.cs`, and `FulcrumVendorClient.cs` in their respective API service folders.

Previously, Estimating's personal Refresh performed the entire quote pull and active BOM inspection
before filtering to the signed-in estimator. Quality ran hourly. Project Tracker's worker only
refreshed the lookup catalog at selected daytime hours. Subcontracting had only an explicit vendor
pull. Those scheduled reads now use the common cadence. Existing authorized explicit endpoints share
fresh snapshots; they do not create another identical upstream call within the same cache window.

## Shared snapshots and concurrency

`shared/SonAero.Platform/Integrations` contains the transport, store, and coordinator. All modules
connect to the **same role database** (ProjectTracker), including Quality and Subcontracting, whose
business records remain in separate module databases. `FulcrumReadSnapshots` supports SQLite for
local use and SQL Server for IIS. Database compare-and-swap leases coalesce identical reads across
independent application processes. There is no cache folder or new service to deploy.

Cache identity covers API authority, credential hash, HTTP method, canonical URL/query, and canonical
request JSON. Credentials and request URLs/bodies are never stored. Responses stay inside the shared
role database, whose existing access restrictions and backups also protect these business snapshots.
Rotating the token uses a new namespace. Expired credentials are rejected before cached access.

Only explicitly allowlisted read endpoints are cached. Mutating requests are excluded. Cost estimate
generation, quote-link identity verification, material review, quantity confirmation, and RFQ draft
preparation request current data; they still share global request pacing but bypass response caching.
Those actions do not write to Fulcrum. Preparing a vendor email draft does not send it.

Successful read snapshots expire on the next shared boundary. Errors never overwrite them. Ingestion
fails on expired-data/upstream failures and keeps the last Arda projection; it must not reapply old
provider data as a new successful sync. A read-only caller may explicitly opt into retained snapshots
with `AllowStale`, receiving freshness headers; none of the ingestion clients opt in. HTTP 401/403
never use stale data. A 401 imposes a five-minute credential pause; a 403 pauses only the affected
endpoint permission scope so other authorized modules can continue. Network failures, 429 and 5xx
impose shared backoff. `Retry-After` is honored for 429 and 503, including service restarts.

Default pacing allows two in-flight requests per credential and starts requests at least 250ms apart
across all processes. This is a configurable operating limit, not a claimed Fulcrum rate allowance.
The official developer documentation does not provide a numeric allowance for this tenant. Observe
durations and any 429 responses before increasing throughput.

The coordinator claims each module once per window. Runs are bounded to 90 minutes, below their
two-hour crash-recovery lease; cancellation cannot be reported as a completed quote run. A long run
skips intervening windows rather than overlapping. An abandoned process claim can delay that module
until its lease expires. Old snapshots/credential scopes are pruned after eight days. No failure
deletes business records or replaces a valid snapshot with an empty list.

## Configuration and deployment

`UniversalFulcrumSync` is the authoritative schedule, in the four module base configurations and
production templates:

```json
{
  "UniversalFulcrumSync": {
    "Enabled": true,
    "IntervalMinutes": 20,
    "MaximumConcurrentRequests": 2,
    "MinimumRequestSpacingMilliseconds": 250,
    "MaximumStaleMinutes": 1440
  }
}
```

The active enterprise provider must be Fulcrum and a non-expired shared API credential must exist.
Missing/inactive/expired configurations log a skipped run without an external request. The Quality
module additionally honors `QualityIntegration:Enabled`; the local development setting is enabled.
Legacy per-module interval settings no longer schedule the shared workers. Use
`UniversalFulcrumSync:Enabled=false` to disable automatic work, including disposable preview servers.

The SQL table initializes idempotently under an SQL Server application lock; SQLite uses
`CREATE TABLE IF NOT EXISTS`. A restricted production account needs the table provisioned by a DBA
before enabling the workers. The schema is in `FulcrumSnapshotStore.cs`; no production deployment
is performed by local implementation or tests. All four modules must use the same database and
settings for shared coordination. Existing production configuration files are preserved by the
release process, so review merged effective settings during deployment.

For an already installed Subcontracting module with a restricted service account, the DBA should
review `deployment/Upgrade-SubcontractingFulcrumSync.sql` on SON-SQL2 in SSMS SQLCMD Mode before
enabling this release. Set its existing `AppServiceAccount` variable to the installed identity;
the default is `SON4L\SON-IIS2$`. The script requires existing databases/login/user, precreates the
cache under the same schema lock, and grants only cache CRUD plus read access to the provider
setting. It validates existing schema and effective permissions, rolls back failures, and emits
`SUBCONTRACTING_FULCRUM_SYNC_DATABASE_READY` after commit. It grants no broad shared-database roles
and does not alter credentials or business records. Do not rerun first-install provisioning for
an installed module. New installations receive these grants from the first-install SQL script.
These SQL artifacts have automated schema/grant contract checks; execution against the production
SQL Server remains a DBA deployment step.

Quality links use `https://son-aero.fulcrumpro.com/ui/ship/{id}/details`, as verified by the supplied
tenant record URL. Shared transport only accepts the ITAR API host `https://api.fulcrumpro.us`.

The Portal Admin Console's **Quality → Shipping Excel Import** uses a credentialed, browser-generated
multipart POST directly to Quality. This one upload omits custom request headers so it does not need
an unauthenticated CORS preflight through Windows-only IIS. Quality requires a single valid `Origin`
matching its own origin or an exact configured `HubAdmin` origin, even if an AJAX header is present;
missing, opaque, malformed, and untrusted origins are rejected before workbook import. Authentication,
shipment view/import permissions, preview restrictions, workflow checks, and file limits still apply.
Keep Quality's existing Windows authentication configuration; no IIS authentication change or proxy
delegation is required. Other admin mutations retain their existing AJAX headers.

The shared notification bell also sends empty, credentialed POST requests for **Remove** and
**Clear all**, with no custom headers or JSON content type. Portal requires exactly one valid
browser `Origin` matching its own origin or an exact origin from `Portal:Applications` for
inbox read/dismiss/clear mutations; missing, opaque, malformed, and untrusted origins are rejected.
The legacy client header does not bypass this check. Notification opening uses the authenticated
Portal redirect, marks the item read, and retains it until explicit removal. Portal keeps its
existing Windows-only IIS authentication; these empty POSTs do not require CORS preflight.

## Operational limits and verification

A 20-minute interval is a freshness target, not a guarantee that unlimited data can be loaded in
20 minutes. Vendor contacts and active quote BOMs require per-record calls. At the maximum configured
20,000 vendors, contacts alone can take over 80 minutes at the default pace. Large tenants need
measured volume and a confirmed incremental/bulk API before promising a full 20-minute sweep.
Completed runs log module, duration and interval; failed or skipped runs explain why. SQL Server
permissions and actual tenant throughput must be checked on the deployment host.

Focused automated tests use separate service graphs/connections to a disposable SQLite store and
verify concurrent coalescing, credential/filter separation, URL encoding, freshness boundaries,
live bypass, authorization failure behavior, and 429/503 backoff. They do not call real Fulcrum.
