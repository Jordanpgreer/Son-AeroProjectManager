# SON-AERO Hub server deployment

The assigned production servers are:

- **SON-IIS2** (`10.50.10.244`): application server for IIS and all five web applications.
- **SON-SQL2** (`10.50.10.242`): SQL Server and controlled Engineering drawing storage.

Do **not** run `scripts/Start-Hub.ps1` as the production host. That launcher intentionally uses
Development authentication and localhost URLs. Production uses IIS, Windows Authentication, SQL
Server, and each employee's real `SON4L\firstname.lastname` domain identity.

Follow [server-deployment.md](server-deployment.md) for the first installation. For the current
post-install rollout (warm start, role verification, employee shortcut, HTTPS readiness, and
backup readiness), follow [production-rollout.md](production-rollout.md) in order. Example
Production settings are in [`templates`](templates). No passwords, live secrets, or certificates
belong in Git.

### Estimating Fulcrum quote-sync token

Do not put the Fulcrum token in IIS settings, an appsettings file, or Git. After deployment, an
Arda administrator opens **Admin Hub → API Keys** and saves it as **Fulcrum Public API**. The Hub
encrypts the value with Windows machine-level protection before storing it in the shared SQL
database. The API never returns the saved value. Portal and Estimating Dashboard must remain on
the same Windows application server (`SON-IIS2`) so both applications can use that protected
credential. Server migrations require the credential to be entered again on the new server.

For the explicitly limited Jordan/Josh HTTPS test, use
[two-person-https-pilot.md](two-person-https-pilot.md). Its private mini-CA and per-computer ZIPs
are pilot-only; they do not replace the managed PKI/trust design required for company rollout.

For the permanent IT-issued `hub.son4l.local` / `*.hub.son4l.local` certificate and DNS rollout,
use [production-hostname-https.md](production-hostname-https.md). Its transaction owns only the five
hostname-based SNI bindings on TCP 443 and preserves the existing HTTP and pilot bindings.
Section 2 contains the required four-site legacy-pilot compatibility gate: protect the authentic
legacy state first, then run `Configure-HubHttpsPilotQualityExtension.ps1` to add only the missing
Quality Assurance 6170 rollback surface before production readiness. That extension has its own
protected state at
`C:\ProgramData\SonAero\deployment-state\https-pilot-quality-extension.json`; permanent TCP 443
rollback does not consume it or remove any 61xx binding.

For an update that introduces the permanent hostnames, deploy the newly published release first,
then apply the shared-port 443 binding transaction, enable the bounded direct-site CORS
authentication baseline, and only then preview and apply the production application configuration:

```powershell
& .\deployment\Configure-ProjectTrackerCorsAuthentication.ps1 -WhatIf
& .\deployment\Configure-ProjectTrackerCorsAuthentication.ps1 -Confirm:$false
& .\deployment\Configure-HubHttpsApplicationConfig.ps1 -Topology Production -WhatIf
& .\deployment\Configure-HubHttpsApplicationConfig.ps1 -Topology Production -Confirm:$false
```

Production uses `https-production-application-config.json`; new explicit Pilot transactions use
`https-application-config.json`. A Pilot application-config state written by the pre-hardening
script is retained only as untrusted historical evidence: this release will not read it when its
file ACL is inherited or broader than SYSTEM/Administrators. Do not treat that legacy file as an
available rollback. The Production transaction instead snapshots the currently active Pilot
configuration into its own protected state, and that is what restores the retained HTTP/61xx
baseline. Production CORS and post-apply checks retain both the HTTPS 61xx pilot and HTTP endpoints
until stabilization ends.
Project Tracker keeps Anonymous and Windows Authentication enabled together only on its direct IIS
site so anonymous CORS preflight reaches ASP.NET Core; every protected API still requires Windows
identity. The same-origin gateway remains Windows-only, and production rollback preserves this
topology-neutral boundary. The authentication bootstrap validates the approved Portal origins
already present in the active Project Tracker configuration; the following application-config
transaction installs and transactionally verifies the permanent origin.

After a trusted HTTPS endpoint is operational, use
`Configure-ProjectTrackerWebPush.ps1` to generate or install the VAPID pair in Project Tracker's
server-only IIS environment and verify the public-key endpoint. Run its `-WhatIf` mode first. The
production settings template stays disabled and never contains the private key.

Engineering Hub and Quality Assurance are production-enabled in the Portal after both sites are
healthy and Quality's SQL Server migration has been validated against the provisioned
`QualityAssurance` database on SON-SQL2. After deploying the reviewed release, apply the visibility
policy to the active Portal configuration on SON-IIS2:

```powershell
& .\deployment\Configure-PortalProductionModuleVisibility.ps1 -WhatIf
& .\deployment\Configure-PortalProductionModuleVisibility.ps1 -Confirm:$false
```

Require `WHATIF_READY_PORTAL_PRODUCTION_MODULE_VISIBILITY`, then
`PORTAL_PRODUCTION_MODULE_POLICY_APPLIED_AND_VERIFIED`. If the policy was already applied, either
command may instead return `PORTAL_PRODUCTION_MODULE_POLICY_ALREADY_APPLIED_AND_VERIFIED`. The
operation changes only the Engineering and Quality Portal-card policies and recycles only the
Portal pool. It does not stop, remove, or weaken authorization on either module site; direct module
URLs remain independently protected and both modules still require their assigned permissions.
Later full releases synchronize this production visibility policy from the production template
while preserving server-local URLs, other production fields, and custom applications.

To build the approved employee workstation ZIP:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  .\deployment\New-EmployeeHubInstallerPackage.ps1
```

The package defaults to the permanent Portal at `https://hub.son4l.local/`. Supply
`-HubUri 'http://SON-IIS2:5140'` only when deliberately building the retained HTTP pilot package.
The ignored output is `deployment\artifacts\SonAero-Hub-Employee-Installer.zip`. Assign the
employee's roles centrally before distributing it. On the employee computer, use **Extract All**
and then double-click `Install Son-Aero Hub.cmd`; do not run the launcher from inside the ZIP.

To build all five applications from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\deployment\Publish-Hub.ps1 `
  -ProjectTrackerUrl "/project-tracker-api"
```

Artifacts are written under `deployment\artifacts\hub` and are ignored by Git. Point IIS at
separate site directories, never at the repository or staging directory. For production updates,
use `Deploy-HubRelease.ps1`; it stages an immutable release, preserves Production settings, checks
all five applications plus the same-origin Project Tracker gateway, and rolls IIS back to the prior
paths if health verification fails. Its cold-start sequence health-gates the Portal, Project Tracker
gateway, and each remaining SQL-backed module one at a time so migration and permission-seeding work
does not overlap. A timeout reports the last endpoint failure and retains the failed immutable
release for inspection. Run `Configure-PortalProjectTrackerGateway.ps1` once on SON-IIS2 before the
first gateway-aware release.

If Quality Assurance is the only unhealthy current site because its deferred SQLite-shaped
migration chain cannot start on SQL Server, the all-app transaction cannot pass its current-health
preflight. After provisioning and backing up the `QualityAssurance` database, use the narrowly scoped
Quality transaction from a fresh `Publish-Hub.ps1` staging root:

```powershell
& .\deployment\Deploy-QualityAssuranceRelease.ps1 `
  -PackageRoot C:\SonAero\staging\hub `
  -ReleaseId quality-sqlserver-20260826 `
  -FirstActivation `
  -WhatIf
& .\deployment\Deploy-QualityAssuranceRelease.ps1 `
  -PackageRoot C:\SonAero\staging\hub `
  -ReleaseId quality-sqlserver-20260826 `
  -FirstActivation `
  -Confirm:$false
```

Use `-FirstActivation` only for that reviewed bootstrap condition; later Quality-only updates require
the current endpoint to be healthy and omit the switch. Require
`WHATIF_READY_QUALITY_ASSURANCE_RELEASE`, then
`QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY`. The transaction preserves the active Production
settings byte-for-byte, changes only the Quality IIS path/pool, and restores the exact prior path and
pool state on failure. Run it as an administrator who also has Quality module access, then apply the
Portal visibility transaction above.

If the current Quality site is healthy but its active Production JSON predates the dedicated
`QualityDatabase.Provider` and `ConnectionStrings.QualityStore` leaves, do not use
`-FirstActivation` and do not edit the active file. Use the same scoped transaction with
`-RepairMissingProductionDatabaseSettings`. This mode accepts only the reviewed legacy state where
both leaves are genuinely absent, builds an immutable candidate containing exactly those two values
from the trusted production template, and rechecks the active/candidate hashes before IIS cutover.
Require these exact markers:

```text
WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_PRODUCTION_DATABASE_SETTINGS_REPAIRED
QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_PRODUCTION_DATABASE_SETTINGS_REPAIRED
```

If SON-SQL2 cannot provision the dedicated `QualityAssurance` database and the production Quality
module is confirmed to contain no data that must be retained, the scoped transaction also provides
an explicit one-time server-local bridge. Use `-UseServerLocalSqlite` only for that reviewed empty
Quality state. It keeps shared users, groups, and permissions on SON-SQL2 `ProjectTracker`, while
placing only Quality operational data in the protected persistent file
`C:\ProgramData\SonAero\deployment-state\quality-assurance-data\quality-assurance.db`.
The connection uses `Mode=ReadWrite`, so a missing file is a hard startup failure rather than a
silently recreated empty database. The transition pre-creates the file, grants only Administrators,
SYSTEM, and `IIS AppPool\QualityAssurance`, requires one pool worker, disables overlapping recycle,
and rolls the prior IIS path and recycle setting back if candidate verification fails.

Require these exact one-time markers:

```text
WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_SERVER_LOCAL_SQLITE
QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SERVER_LOCAL_SQLITE
```

Windows records an allowed `Modify` ACE as the exact canonical
`Modify, Synchronize` mask. The deployment validates that exact representation; it does not use a
subset match and does not admit permission-management or ownership rights. If an older deployment
stopped before IIS changed after creating only the protected empty data directory, do not delete or
re-ACL that directory and do not reuse the retained application candidate. Build a fresh package,
choose a fresh release ID, and add `-ResumeServerLocalSqlitePreparation` together with
`-UseServerLocalSqlite`. Resume is accepted only when the approved directory already exists, is not
a reparse point, contains zero entries including hidden entries, is owned by Administrators with
inheritance disabled, and has exactly the explicit Administrators/SYSTEM FullControl and Quality
application-pool `Modify, Synchronize` rules. It never repairs unexpected state. Require these
distinct markers:

```text
WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_SERVER_LOCAL_SQLITE_RESUME
QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SERVER_LOCAL_SQLITE_RESUME
```

Any missing, nonempty, or differently secured directory is a stop condition. Preserve the old
candidate and report the exact output instead of deleting state or retrying with the ordinary
one-time switch.

After the transition succeeds, omit `-UseServerLocalSqlite` on ordinary Quality releases. Both the
targeted and full-Hub transactions revalidate the persistent path, file, ACL, single-worker pool,
and non-overlapping recycle boundary whenever this explicit storage mode is active. Do not delete,
move, replace, or reuse the SQLite file after a failed apply. A rollback message is a stop condition.
Once Quality begins receiving production records, include this file in the approved backup and
restore process until the dedicated SON-SQL2 database and a reviewed data migration are available.

After that scoped repair succeeds, the same fresh Hub package can deploy the other four applications
with `Deploy-HubRelease.ps1 -RetainVerifiedQuality`. This recovery mode requires the package Quality
artifacts to match the now-active Quality artifacts byte-for-byte except for excluded Production and
Development JSON, and it verifies the Quality path, IIS state, bindings, authentication, runtime
environment, configuration hash, critical ACLs, and health without copying, stopping, starting,
re-ACLing, or repointing Quality. Require
`WHATIF_READY_HUB_RELEASE_WITH_VERIFIED_QUALITY_RETAINED`, then
`HUB_RELEASE_DEPLOYED_AND_HEALTHY_WITH_VERIFIED_QUALITY_RETAINED`. Normal full-Hub releases remain
five-application transactions and reject incomplete Quality Production settings; neither mode
repairs SQL data or replaces the required pre-deployment database backup.

When a release changes **only Project Tracker** and does not require a Portal, Engineering,
Estimating, Quality, shared-production-configuration, or cross-module database change, use
`Deploy-ProjectTrackerRelease.ps1` instead. Continue to build a fresh deterministic Hub staging
root with `Publish-Hub.ps1`; the targeted transaction reads only its `ProjectTracker` folder and
switches and verifies only the direct `ProjectTracker` IIS site and the existing
`/project-tracker-api` Portal gateway. It preserves the active Project Tracker
`appsettings.Production.json` and automatically restores both prior paths if candidate health
verification fails. This avoids making an unrelated module's health a release gate for an otherwise
independent Project Tracker update.

Run the targeted transaction with `-WhatIf` first and require
`WHATIF_READY_PROJECT_TRACKER_RELEASE`. Apply only after that exact marker, and require
`PROJECT_TRACKER_RELEASE_DEPLOYED_AND_HEALTHY` from the apply. A message reporting automatic
rollback, rollback verification failure, or the absence of the success marker is a stop condition:
do not rerun the command, recycle pools, change IIS paths, or delete the retained failed release.
Capture the complete output and diagnose the retained candidate first. See the Project Tracker-only
procedure in [production-hostname-https.md](production-hostname-https.md).

When a release changes **only the Portal root**, use `Deploy-PortalRelease.ps1`. It reads only the
`Portal` folder from a fresh `Publish-Hub.ps1` staging root, preserves the active Portal
`appsettings.Production.json` byte-for-byte, and switches only the Portal root virtual-directory
path. The `SonAeroPortal` site and dedicated Project Tracker gateway pool remain started; the
gateway path and its Anonymous=False/Windows=True authentication boundary are verified unchanged.
The transaction stops and starts only the Portal root pool and restores the exact prior root path
if root or gateway verification fails.

For a compatible release containing both Project Tracker and Portal changes, publish once, deploy
Project Tracker first, and require its healthy marker before deploying the Portal root from the
same staging package. This leaves the existing Portal compatible if the Project Tracker transaction
fails and avoids health-gating unrelated modules. Require `WHATIF_READY_PORTAL_RELEASE` from the
Portal preview and `PORTAL_RELEASE_DEPLOYED_AND_HEALTHY` from apply. The same automatic-rollback
stop rule applies: do not rerun blindly or delete the retained candidate.

The default publish is intentionally topology-neutral: the same immutable frontend bundle resolves
permanent hostnames to `https://hub.son4l.local`, HTTP endpoints to the 51xx topology, and retained
HTTPS pilot endpoints to the 61xx topology at runtime. Publish-time Hub/module origin overrides are
intentionally unavailable so a server environment variable cannot silently break that rollback path.
