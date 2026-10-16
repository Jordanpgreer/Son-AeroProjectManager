# Deploy the Arda interface and shared Fulcrum update

This release includes the Quality work through `d5fa610`, the shared notification bell,
hover/pinned navigation, Admin Console shipping import, shipment links, and the shared
20-minute Fulcrum schedule. Use a **full Hub release**. Do not retain the old Quality binaries.

## Before running the release

Run on **SON-IIS2**, in elevated interactive **Windows PowerShell 5.1**, as an authorized
`SON4L\...` user. The production checkout is `C:\SonAero\src\SonAeroInternalHub`, on `main`.
It must be clean. Preserve and resolve any server-local changes before deployment; do not
stash, reset, or overwrite production work to make the checks pass.

1. Verify current restorable backups and restore evidence for ProjectTracker, EngineeringHub,
   the active Quality database, and controlled drawing storage. If Quality uses the protected
   server-local SQLite bridge, include that database using a consistent SQLite backup. Include
   Subcontracting's database and documents when that module is already installed.
   `Test-HubBackupReadiness.ps1` checks prerequisites; it does not create or prove a backup.
2. Confirm all Fulcrum consumers use the same **ProjectTracker role database on SON-SQL2**:
   Tracker `ConnectionStrings:SqlServer`, Estimating/Quality `ConnectionStrings:ModuleAccessStore`,
   and Subcontracting `ConnectionStrings:RoleStore`. Keep each module's business database unchanged.
3. Verify the active enterprise provider is Fulcrum and its shared credential is current in
   **Admin Console -> Integrations -> API Keys**. Enter secrets only there. The existing token is
   protected on SON-IIS2; do not copy a development database or token into configuration.
4. Verify SQL schema permissions. The normal SON-IIS2 machine identity has reader/writer/DDL
   roles under the existing provisioning convention. Portal initializes the inbox-dismissal
   table at startup. The shared cache creates `dbo.FulcrumReadSnapshots` on first use under an
   application lock. Health can pass before the first cache operation exposes a permissions issue.
   For a restricted dedicated Subcontracting identity, complete the SQL upgrade below first.
5. Confirm pools are AlwaysRunning, idle timeout is zero, and application preload is enabled.
   The release wrapper checks this. If repair is needed, use the existing
   `Configure-IisWarmStart.ps1 -Scheme https -PermanentHttps` preview/apply transaction; include
   `-IncludeSmallBusinessSubcontracting` when already installed. Require
   `WARM_START_CONFIGURED_AND_HEALTHY`, then start a fresh release run.

### Restricted Subcontracting SQL identity

If Subcontracting is already installed with a dedicated identity that has only read access to
shared authorization tables, have the DBA run
[`Upgrade-SubcontractingFulcrumSync.sql`](../deployment/Upgrade-SubcontractingFulcrumSync.sql)
on **SON-SQL2 in SSMS with SQLCMD Mode enabled**, after verifying the ProjectTracker backup.
Review `AppServiceAccount` and set it to the identity actually used by the installed module;
the default is the existing machine identity `SON4L\SON-IIS2$`.

Require `SUBCONTRACTING_FULCRUM_SYNC_DATABASE_READY`. The upgrade requires existing databases and
identity, validates/precreates the shared cache, and grants only cache CRUD and provider-settings
read access. It does not grant broad roles or change credentials/business records. Do not rerun
first-install provisioning for an installed module. Do not paste the SQL into PowerShell.
The usual shared machine identity with existing DDL/read/write rights does not need this extra grant.

## Release command

Use the **exact full commit SHA supplied in the release handoff**. Fetch and fast-forward the clean
server `main` checkout to that SHA, then run:

```powershell
& .\deployment\Invoke-ArdaInterfaceRelease.ps1 -ExpectedCommit '<full SHA from the handoff>'
```

The handoff contains a ready-to-paste bootstrap block with the SHA filled in. The script verifies
host, shell, identity, branch, cleanliness, and `origin/main` again. It asks for `BACKUPS_VERIFIED`
only after the operator has verified the backups described above.

It publishes to a fresh `C:\SonAero\staging\arda-...` package and deploys a fresh immutable
`C:\SonAero\releases\arda-...` release. It updates the standard five applications and includes
Small Business Subcontracting automatically **only when its IIS site is already installed**.
This release does not perform its first installation.

The same package, release ID, module selection, and 300-second health timeout are reused for
preview and apply. Required markers are:

1. `WHATIF_READY`
2. `HUB_RELEASE_DEPLOYED_AND_HEALTHY`
3. `ARDA_INTERFACE_RELEASE_DEPLOYED_HEALTHY` after permanent HTTPS health checks

The wrapper uses the new `Deploy-HubRelease.ps1 -EnableUniversalFulcrumSync` switch. After copying
and verifying the live Production configuration, this switch updates **only the new candidate**:

| Setting | Value |
| --- | --- |
| UniversalFulcrumSync.Enabled | true |
| UniversalFulcrumSync.IntervalMinutes | 20 |
| UniversalFulcrumSync.MaximumConcurrentRequests | 2 |
| UniversalFulcrumSync.MinimumRequestSpacingMilliseconds | 250 |
| UniversalFulcrumSync.MaximumStaleMinutes | 1440 |
| QualityIntegration.Enabled (Quality only) | true |
| QualityIntegration.FulcrumShipmentUrlTemplate (Quality only) | `https://son-aero.fulcrumpro.com/ui/ship/{id}/details` |

All other settings, credentials, connections, and Quality storage mode are preserved. Active
Production files are not edited. Environment overrides for these settings cause preflight to
stop instead of silently overriding the reviewed policy. Do not replace active settings with templates.

## Browser and synchronization acceptance checks

Open [Arda](https://hub.son4l.local) from an authorized workstation after deployment:

- Across every installed module, confirm one bell beside the theme toggle, unread highlighting,
  opening without removal, hover/focus removal, and Clear all. Test the bell from a module on a
  different hostname from Portal as well as from Portal itself.
- Confirm the menu starts collapsed, expands on hover/focus, and remembers pin/unpin.
- Open **Admin Console -> Quality Assurance -> Shipping Excel Import**. Import an approved workbook
  only when intended: import creates records. A row such as `SO2877` must be skipped; a suffixed
  row such as `SO2877-2` must be accepted subject to normal validation/duplicate rules.
- Verify Quality dashboard and Shipping Status have no routine Refresh/Import buttons, both quick
  views show `SHP-SO...-...`, matched titles open the correct Fulcrum record, and the sync box is absent.
- Check the browser console/network for failures. Shipping upload uses a browser multipart request
  with strict trusted-Origin validation, preserving Windows-only IIS authentication. No anonymous
  authentication change is required for this upload.
- Observe the next UTC `:00`, `:20`, or `:40` boundary in application logs. Confirm each installed
  consumer reports completion or an explained failure/skip, and that the shared SQL cache is being
  populated. Inspect counts/timestamps rather than response bodies or credentials.

Engineering and Portal do not pull business data from Fulcrum. Tracker, Estimating, Quality, and
installed Subcontracting share scheduling, request pacing, and identical read snapshots. Different
Fulcrum resources still require separate paginated calls. Large scans may take longer than 20 minutes;
overlapping runs are prevented. The local environment had no Fulcrum credential, so real tenant
matching and throughput require this production acceptance check.

## If a step fails

Stop on a missing success marker or a reported rollback. The deployer attempts to restore previous
IIS paths and health after a failed cutover; this does not restore database changes. Keep the package,
release, and diagnostic output. Do not rerun blindly or weaken a guard.

If apply already succeeded and a later check fails, the wrapper reports
`POSTCHECK_STOP_AFTER_VERIFIED_APPLY`. Diagnose the existing release without publishing or deploying
again. Browser/sync acceptance remains required even after the final health marker.

## Local testing

Production has not been deployed by preparing this release. Local restart remains:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Start-Hub.ps1
```

Open `http://localhost:5140`. Local databases are excluded from this release commit.
Implementation and test evidence: [interface notes](arda-interface-update.md) and
[Fulcrum audit](universal-fulcrum-sync.md).
