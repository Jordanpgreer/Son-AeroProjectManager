# Arda navigation, inbox, and automatic data refresh

Implemented locally on top of `d5fa610` on `codex/quality-fulcrum-sync`.

## User-visible changes

- All six applications use the bell beside the theme switch as their notification inbox.
  The whole bell pulses while unread notifications remain. Reduced-motion preferences
  receive a steady highlight.
- Opening a notification marks it read and navigates to its destination. The item stays
  in the inbox until explicitly removed. Each row has a hover/focus remove button;
  touch devices show it directly. **Clear all** removes the user's current inbox items,
  including read items and entries beyond the visible page. Source workflow records
  are retained, and future notifications can still arrive.
- The desktop menu starts collapsed, expands on hover or keyboard focus, and has a
  persistent **Pin menu / Unpin menu** control at the bottom. Notification popups do
  not count as hovering or focusing the menu. Existing mobile navigation stays usable.
- Removed the duplicate Quality notification button and routine page Refresh controls.
  Browser views revalidate local Arda data periodically and on returning to the page;
  draft editing pauses or invalidates background replacements.
- Shipping Excel import moved to **Admin Console → Quality Assurance → Shipping
  Excel Import**. The existing Quality permissions and preview restrictions still apply.
  The workbook uses its existing `Complete List` worksheet. Rows without a shipment
  suffix, such as `SO2877`, are skipped; `SO2877-2` becomes `SHP-SO2877-2`.
- Both Quality shipment quick views display the `SHP-` prefix and link matched records
  to `https://son-aero.fulcrumpro.com/ui/ship/{id}/details`. Unmatched records have plain
  headings until a successful match supplies their real ID. The Fulcrum Sync card was removed.

## Fulcrum schedule

See [the module audit and synchronization guide](universal-fulcrum-sync.md).
Tracker, Estimating, Quality, and Subcontracting share a 20-minute schedule, request
pacing, and a database snapshot cache. Identical reads are reused across processes.
Different Fulcrum resources still require separate paginated requests. Engineering
and Portal do not have periodic business-data pulls to schedule.

**Local limitation:** the shared `IntegrationCredentials` table has no Fulcrum API
credential. Configure it at `http://localhost:5140/#/admin/integrations/api-keys`.
Live upstream synchronization, real shipment matching, and tenant throughput could
not be verified without that credential. A large complete sweep can exceed 20 minutes;
overlapping runs are prevented.

## Local runtime

| Application | URL |
| --- | --- |
| Arda / Admin Console | http://localhost:5140 |
| Project Tracker | http://localhost:5135 |
| Engineering | http://localhost:5150 |
| Estimating | http://localhost:5160 |
| Quality | http://localhost:5170 |
| Small Business Subcontracting | http://localhost:5180 |

Restart from the checkout with `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Start-Hub.ps1`.
The launcher rebuilds changed interfaces and starts or updates the six local services.

Browser testing exposed shared SQLite reader/write contention on `/api/me`. Tracker
now enables WAL for its file-backed local database, and last-seen telemetry is a
bounded, throttled update that cannot deny access because of a temporary lock.
All six native runtimes were verified as SQLite 3.53.3. Estimating and Quality were
aligned to the SQLite bundle already used by the other modules. Startup validates a
patched runtime before enabling WAL; see [SQLite's WAL documentation](https://www.sqlite.org/wal.html#walreset).
SQL Server deployment behavior was not changed by this local concurrency fix.

Local databases were backed up using SQLite's backup API under
`logs/backups/before-arda-update-20261015-152401`. The shared database passed
`PRAGMA quick_check`. Preserve WAL sidecars for hot file copies, or use SQLite's
backup API; copying an active `.db` file alone is insufficient.

## Verification

- Release follow-up: PowerShell 5.1 candidate configuration tests passed 129 assertions;
  the mocked release-wrapper suite passed 61 assertions; existing Hub release contracts passed.
  Subcontracting SQL/provisioning contracts passed 14 tests. SQL Server execution remains a DBA step.
- Shipping import follow-up: 40 Quality integrity/import tests and 11 Portal transport/UI tests passed.
- After the release transport fixes, all six frontend bundles were rebuilt and all six local
  services returned HTTP 200 from `/api/health` on October 16, 2026.
- Production frontend builds completed for all six applications.
- Full backend suites: Estimating 424 passed; Quality 137 passed.
- Focused backend suites: Portal inbox 27 passed; Tracker projection 49 passed;
  shared cache/coordinator 19 passed; SQLite activity/concurrency/version guards 19 passed.
- Release follow-up for cross-app inbox mutations: 49 focused Portal backend tests and
  14 inbox frontend tests passed. These verify headerless credentialed POST requests,
  mandatory exact Origin validation, recipient isolation, inactive-account denial,
  read-item retention, dismissal, and clear-all behavior. This is automated coverage;
  Windows-authenticated IIS behavior still requires the production rollout check.
- Portal frontend: 130 passed before the final additional sidebar transition regression;
  all three sidebar regression tests passed after that change.
- Other frontend suites: Estimating 180 passed, one native Excel case skipped;
  Quality 15 passed; Subcontracting 5 passed; Tracker guide 40 passed.
- Subcontracting backend: 23 of 24 passed. The existing reviewed SQL migration version
  assertion expects EF 8.0.31 while the wildcard restore resolves 8.0.32. The migration
  script was not changed for this interface task.
- Actual browser checks covered all six module pages, one global bell per page,
  collapsed menus, pin persistence, header cleanup, desktop/mobile import layout,
  and both shipping quick views. Browser-only notification fixtures verified hover
  removal, clear all, and opening without removal; backend tests separately cover
  persistence and authorization. Browser-only shipment fixtures verified the link
  rendering in both quick views. These fixtures did not edit user notification or
  shipment records and were removed after testing.

Browser captures and snapshots are under the ignored `output/playwright` folder.
Final runtime checks returned HTTP 200 from all six health endpoints and twelve
concurrent Tracker identity requests. The final Admin import browser page had zero
console errors. Sidebar movement from bell to menu expanded it from 72 to 322 pixels;
movement back to the bell collapsed it to 72 pixels. At the 21:40 UTC boundary, all
four upstream workers logged a skipped run because the shared credential was absent.
Production rollout is documented in [the release instructions](arda-interface-release.md).
No production deployment was performed during implementation. Local runtime databases are excluded
from the source release.
