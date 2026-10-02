# Adding a new Arda module to the company servers

For the implemented Small Business Subcontracting procedure, use the
[module-specific server installation guide](small-business-subcontracting-server-installation.md).
The baseline inventory below describes the original reviewed commit; the module-specific guide
documents the added scripts and optional six-application deployment path.

This guide covers implementing a new module and installing it on **SON-IIS2** and
**SON-SQL2**, including database storage, IIS hosting, HTTPS, access, and verification.
It is intended for the developer/Codex preparing the release and the IT operator applying it.

**Reviewed against the repository on October 1, 2026**, at commit
`721585513a5333bb1069e70883e7b4b6e4e23706`. This is a code review snapshot, not a live
inspection of either company server. Recheck the scripts and server state before each release.

Adding a Portal card, starting the application locally, or pushing to GitHub does not install
a module on the company servers. Completion requires a verified application, database,
storage configuration, and employee access on the production infrastructure.

## 1. Where each step happens

| Location | Responsibility |
| --- | --- |
| Development PC | Implement the module, migrations, deployment support, tests, and release instructions; commit and push reviewed changes. |
| SON-SQL2 (`10.50.10.242`) | Provision the module database and SQL permissions; arrange approved file storage, backups, and restore verification. |
| SON-IIS2 (`10.50.10.244`) | Pull the verified commit, publish, configure the new IIS application, apply the release, and check health. |
| Company DNS/certificate administration | Create the permanent hostname and ensure the server certificate covers it. |
| Employee workstation | Verify trusted HTTPS, Windows sign-in, Portal access, and actual module behavior as that employee. |

Use an elevated, interactive **Windows PowerShell 5.1** session on SON-IIS2 under an
authorized `SON4L\...` account. The production checkout is
`C:\SonAero\src\SonAeroInternalHub`. Do not use an N-central Local System shell to prove
employee identity or run an interactive release command.

Run SQL through SSMS, `sqlcmd`, or an explicitly reviewed provisioning script on SON-SQL2.
T-SQL, including `GO`, cannot be pasted directly into ordinary PowerShell.

## 2. Understand the existing deployment limits

At the reviewed commit:

- `Publish-Hub.ps1` publishes Project Tracker, Portal, Engineering, Estimating, and Quality.
- `Deploy-HubRelease.ps1` deploys those five applications. It requires existing IIS sites,
  pools, active Production configuration files, and healthy applications before applying.
- It does **not** have a generic first-install switch for a sixth application.
- `Configure-IisServer.ps1` is an initial five-site setup script. Rerunning it against an
  existing installation can change existing physical paths and settings; it is not a
  safe substitute for a scoped new-module installation.
- `Configure-SqlServer.ps1` is broad initial server setup and can change service/network
  configuration. Adding one database should use a reviewed, scoped DBA operation.
- Quality's `-FirstActivation` option is specific to its existing-site activation contract.
  It does not create an arbitrary new module.

The September 1 production handoff recorded Quality running on IIS with shared access data
in SON-SQL2's `ProjectTracker` database, but Quality operational data in protected SQLite on
SON-IIS2. A SQL Server template in Git does not prove a live database migration occurred.
Preserve that installation unless a separate, tested data migration is explicitly planned.

Use SQL Server for a new module's operational database by default. Do not copy Quality's
temporary SQLite arrangement into a new module merely because Quality used it previously.

## 3. Define the module before implementing deployment

Complete this worksheet and include the final values in the release handoff.
The example below is **proposed for Small Business Subcontracting**, not evidence that these
server resources already exist. Confirm hostname, port availability, and storage with IT.

| Item | Example |
| --- | --- |
| Display name | Small Business Subcontracting |
| Stable module/Portal ID | `small-business-subcontracting` |
| Source directory | `apps/small-business-subcontracting` |
| API project/DLL | `SmallBusinessSubcontracting.Api.csproj` / `SmallBusinessSubcontracting.Api.dll` |
| Published folder | `SmallBusinessSubcontracting` |
| IIS site and application pool | `SmallBusinessSubcontracting` |
| Permanent address | `https://subcontracting.hub.son4l.local` |
| Internal HTTP port | `5180`, subject to collision checks |
| Employee HTTPS port | `443`, using the hostname/SNI binding |
| Optional retained pilot HTTPS port | `6180`, only if the reviewed deployment design requires a pilot binding |
| Operational database | `SmallBusinessSubcontracting` on SON-SQL2 |
| Shared identity/access database | `ProjectTracker` on SON-SQL2 |
| Network identity with ApplicationPoolIdentity | `SON4L\SON-IIS2$` |
| Persistent uploaded documents | An IT-approved UNC directory outside Git, staging, and releases |
| Health and identity routes | `/api/health` and `/api/me` |
| Release owner / IT operator / backup owner | Record names and maintenance window |

Use stable identifiers consistently across permissions, configuration, publishing, deployment,
and verification. A local port or local database filename is not a production configuration.

## 4. Implement the application on the development PC

1. Add the API/frontend and test projects under `apps/<module>/`, and register the relevant
   projects in `SonAeroInternalHub.sln`. Follow the existing ASP.NET Core 8/React architecture.
2. Ensure `dotnet publish` builds the frontend and includes the served files in `wwwroot`,
   together with the API DLL, dependencies, and valid IIS `web.config`.
3. Use Windows authentication in Production and the shared access database. Unassigned users
   must have no module access. Development authentication must never be a production fallback.
4. Define module IDs, roles, permission expansion, and server-side policies through
   `shared/SonAero.Platform/Security/ApplicationModuleAccess.cs` and the relevant permission
   definitions. Include Portal/Admin access management and module URL mappings where needed.
5. Enforce permissions at API boundaries, including export, uploads/downloads, sync, edits,
   and administrative actions. Hiding buttons or a Portal card does not authorize an API.
6. Provide health and identity endpoints plus a read-only functional check that exercises
   database access. A health endpoint returning a constant `ok` alone does not prove SQL works.
7. Use versioned EF migrations tested against SQL Server. Verify generated keys, column types,
   indexes, constraints, and incremental upgrades; SQLite tests alone are insufficient.
8. Decide whether the application applies migrations at startup or a separate deployment
   identity applies them. Document the required permissions and startup order.
9. Keep module-owned operational tables in its own database. Reuse shared identity/access
   data without having the new module independently recreate or own that shared schema.
10. Implement persistent storage and integration configuration. Keep API credentials in the
    existing protected configuration/credential system, outside source and browser responses.
11. Match Arda's UI, themes, navigation, and responsive behavior. Verify actual browser flows.
12. Add the module to `scripts/Start-Hub.ps1` for local testing and
    `scripts/Sync-Branding.ps1` if it consumes the shared branding assets.

Do not use `Database.EnsureCreatedAsync()` as the production upgrade strategy. If it has
already created a database, inspect its schema and plan a verified migration baseline;
switching to `MigrateAsync()` without reconciling that database is not a migration plan.

## 5. Add all production deployment support

Complete this work before providing an executable production installation command.

| Repository surface | Required change |
| --- | --- |
| `deployment/Publish-Hub.ps1` | Publish the module and validate its project/output. A six-module release must contain all six folders. |
| `deployment/Deploy-HubRelease.ps1` | Include its folder, DLL, site/pool, internal port, configuration preservation, health checks, and rollback in ordinary future updates. |
| New or extended first-install transaction | Support an absent site/pool/configuration explicitly, with preview, collision checks, owned-resource tracking, failure recovery, and tested success markers. This capability must be implemented. |
| `deployment/Configure-IisServer.ps1` | Keep fresh-environment provisioning consistent, while using a scoped transaction for the existing company's installation. |
| `deployment/templates/<module>.appsettings.Production.json` | Add a sanitized template using the exact configuration keys consumed by the module. |
| `deployment/Create-Databases.sql` and `deployment/Configure-SqlServer.ps1` | Keep database inventories/grants consistent; provide a scoped new-database procedure for the existing SQL server. |
| Portal base configuration and `deployment/templates/portal.appsettings.Production.json` | Register the same stable ID, permanent URL, metadata, and intentional launch visibility. |
| `deployment/PortalApplicationCatalog.psm1` | Verify ID-based merge, preservation of existing entries, and how the new entry's URL/visibility will be changed after first activation. |
| `deployment/HubProductionHttps.Common.psm1` | Extend the explicit application map and supporting hostname/binding validation. |
| `deployment/Configure-HubProductionHttps.ps1` and `deployment/Configure-HubHttpsApplicationConfig.ps1` | Review application inventories, certificate/DNS checks, configuration transforms, transaction state, and rollback for the added module. |
| `deployment/Configure-IisWarmStart.ps1` | Add the site, host, port, validation lists, initialization, and recovery checks. |
| `deployment/Test-HubProductionHttpsReadiness.ps1` and `deployment/Test-HubUserAccess.ps1` | Cover the new hostname, expected Windows identity, allowed permissions, and denied access. |
| `deployment/Test-HubBackupReadiness.ps1` | Include the new database and required storage in prerequisite checks. Arrange actual backups separately. |
| `tests/deployment` and `deployment/tests` | Add first-install, upgrade, failure, rollback, configuration, catalog, and access coverage. Update fixed five-app expectations. |
| Relevant production runbooks | Record the expanded application inventory, rollout order, exact markers, and recovery procedure. |

Audit all other fixed application lists, `ValidateSet` declarations, artifact-count assumptions,
and saved transaction formats. Existing HTTPS transaction records may describe five applications;
adding a sixth map entry may require a reviewed state migration or scoped extension. Do not
delete those records or weaken ownership checks to get an old transaction accepted.

### Portal visibility requires an explicit activation step

The current catalog merge matches applications by ID to avoid positional JSON-array corruption.
It adds missing entries from the production template, but only selected existing IDs have
template-owned URL/role policies. Editing a template alone may not update an already-existing
new-module entry during a later release.

Keep the module unavailable to employees until activation succeeds, and implement a scoped,
verified way to enable its permanent link afterward. `AllowedRoles: []` means no role-based
card restriction; it does not mean disabled. Preserve independent API authorization.
`Configure-PortalProductionModuleVisibility.ps1` currently handles Engineering/Quality,
so do not assume it can activate the new module without changes.

## 6. Prepare SON-SQL2 and persistent storage

The DBA should use a reviewed module-specific provisioning script or SQL file. Validate the
exact database and account names before applying; use SQLCMD mode if the script uses `:setvar`.

1. Inspect the existing SQL instance, database names, migration history, and any partial
   module schema. Verify backup ownership, restore evidence, capacity, and connectivity.
2. Create the module database if absent. If it exists, inspect it; never drop/recreate it
   simply to make installation succeed.
3. Create or verify the Windows login `SON4L\SON-IIS2$` and its database user mapping.
4. Grant the module's required data permissions. The current repository pattern uses
   `db_datareader` and `db_datawriter`; review whether narrower grants are appropriate.
5. Grant migration rights only to the identity that will apply migrations. Existing startup
   migration paths commonly need `db_ddladmin`; do not grant server `sysadmin` or blanket
   `db_owner` just to resolve an application permission error.
6. Verify required shared `ProjectTracker` access without removing permissions needed by
   other Hub applications. Use the existing shared-schema initializer/migration owner.
7. Apply the reviewed schema through the chosen migration path and verify migration history,
   expected tables, and access using the application's real execution identity.
8. Include the database in the approved backup schedule, retention, monitoring, and restore tests.

With ApplicationPoolIdentity, the local code-directory principal is
`IIS AppPool\<PoolName>`, while remote SQL/SMB normally sees `SON4L\SON-IIS2$`.
Separate application pools therefore do not automatically provide separate remote SQL identities.
If IT chooses a dedicated service account, revise grants and verify the resulting identity.

For modules with uploaded files:

- IT selects a persistent UNC directory, for example a module directory in an approved
  SON-SQL2 share. The actual share/path must be confirmed, not guessed.
- Grant both SMB share and NTFS permissions to the actual network identity. Grant only the
  file operations the module needs; retain administrative control.
- Keep files outside the checkout, published package, and immutable release directories.
- Validate absolute paths, containment, traversal, and reparse points. Match upload limits
  in the application and IIS. Verify authenticated download access as well as upload access.
- Back up documents and database metadata consistently enough to meet the agreed recovery goal.
- Verify write/read access from the application; an administrator browsing the share is not proof.

## 7. Prepare Production configuration

For first installation, supply a validated Production configuration from a sanitized template
and approved server values. Later releases preserve the active file. Any new required setting
needs an explicit validated configuration migration; preserving a file does not add new keys.

This is an **illustrative Small Business Subcontracting configuration**, using keys read by
the current application. Replace the UNC placeholder with the IT-approved path before use.
Other modules must use their own actual provider and connection-string keys.

```json
{
  "Authentication": { "Mode": "Windows" },
  "Database": { "Provider": "SqlServer" },
  "SubcontractingDatabase": { "Provider": "SqlServer" },
  "ConnectionStrings": {
    "RoleStore": "Server=tcp:SON-SQL2,1433;Database=ProjectTracker;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=true",
    "SubcontractingStore": "Server=tcp:SON-SQL2,1433;Database=SmallBusinessSubcontracting;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
  },
  "VendorDocumentStorage": {
    "RootPath": "\\\\SON-SQL2\\<approved-share>\\SmallBusinessSubcontracting\\Documents",
    "RequireUncPath": true,
    "MaximumFileBytes": 20971520
  }
}
```

The SQL TLS options above match existing templates. Have IT confirm the SQL certificate policy;
`TrustServerCertificate=True` encrypts the connection but bypasses SQL certificate validation.
Use certificate validation when the approved SQL certificate/trust setup supports it.

Never commit live Production settings or credentials. Check IIS environment variables and
connection-string overrides as well as JSON: they can override the intended provider or server.
Verify `Production` environment selection and that no Development database/configuration ships.

## 8. Implement and execute the first IIS installation

The developer must implement/test the scoped first-install transaction before IT runs it.
The transaction should perform the following, with no server mutation during its preview:

1. Validate machine, operator, approved commit/package, paths, required configuration,
   SQL/storage readiness, IIS features, and ASP.NET Core Hosting Bundle compatibility.
2. Check all site/pool names, ports, host bindings, and existing resources for collisions.
   If a partial install exists, compare it to recorded transaction state instead of guessing.
3. Prepare a fresh immutable application directory under `C:\SonAero\releases` using the
   validated package and Production settings. Grant the pool read/execute access to code.
4. Create the dedicated pool/site with ApplicationPoolIdentity, No Managed Code, the reviewed
   worker/recycle settings, Windows authentication, and explicit Production configuration.
   New modules normally disable Anonymous authentication. Preserve Project Tracker's existing
   CORS-related exception and the Portal gateway's separate authentication settings.
5. Apply the permanent HTTPS binding with SNI on port 443. Company DNS must resolve the
   module hostname to SON-IIS2, and the managed certificate must cover that hostname, have
   an accessible private key, and be trusted by employee workstations.
6. Retain the reviewed internal HTTP binding for deployment health checks. Add any pilot
   binding only if the chosen tested topology requires it. Restrict firewall rules appropriately.
7. Start the application, apply migrations through the chosen identity, and verify health,
   real database access, and required storage. Keep external writes/synchronization controlled.
8. On failure, restore prior settings and remove only new IIS resources owned by this
   transaction. Preserve logs, database contents, files, and evidence for recovery.
9. Emit the first-install script's documented success marker only after all required checks.
10. Deploy any required Portal/shared changes using the verified dependency order, then enable
    the module's Portal visibility through the reviewed configuration transaction.

Do not point IIS at the Git checkout or staging folder, overwrite a running site's DLLs, or
reuse an incomplete release directory. A first-install rollback cannot undo database migrations;
database restore or forward repair is a separate DBA decision.

Choose the rollout order before coding the transaction. A staged approach can first install
the module while hidden, then deploy the expanded full Hub once all sites exist and are healthy.
Any shared-schema prerequisite must precede the new module's startup. An integrated approach
must explicitly handle absent resources; merely appending a sixth array entry is insufficient.

## 9. Validate, commit, and push from development

Required evidence before generating the production command:

- Backend tests, frontend lint/tests/build, and full solution checks appropriate to the release.
- SQL Server fresh-install and incremental-migration tests against disposable test databases.
- Browser verification of desktop/mobile, light/dark, navigation, forms, console, and permissions.
- A fresh publish containing every expected application, DLL, `web.config`, and served frontend.
- Windows PowerShell 5.1 deployment tests covering preview without mutations, first install,
  upgrade, conflicting resources, missing config, failed health, and restoration of prior state.
- Production config preservation, secret exclusion, path containment, existing HTTPS transaction
  compatibility, Portal catalog merge/activation, and the new module's authorization checks.

Run frontend commands from that application's `ClientApp`, using its actual package scripts.
Run existing deployment suites from the repository root with Windows PowerShell 5.1, including
applicable tests in both `tests/deployment` and `deployment/tests`. Require documented test markers
and successful exit codes. Do not run production integration tests against live business data.

Inspect `git status`, the full intended diff, and `git diff --check`. Preserve unrelated local
work, especially `apps/project-tracker/src/ProjectTracker.Api/project-tracker-dev.db`.
Stage explicit reviewed paths. Once a push is authorized, commit and push normally, fetch,
and verify local `HEAD` exactly equals `origin/main`; record the full 40-character SHA.

## 10. Generate the exact operator commands for this release

There is no currently supported universal command that installs any new module.
After implementing deployment support, provide separate, exact instructions for:

1. **SON-SQL2 / DBA:** scoped database grants/schema prerequisites, persistent storage, and backups.
2. **DNS/certificate administration:** the verified hostname and certificate/binding prerequisites.
3. **SON-IIS2:** one tested, self-contained PowerShell 5.1 block for the agreed installation order.
4. **Employee workstation:** identity, access, and functional verification.

The IIS command must:

- Require SON-IIS2, Windows PowerShell 5.1, elevation, and an authorized interactive domain user.
- Require clean `main` in `C:\SonAero\src\SonAeroInternalHub`; fetch and compare `origin/main`
  against the exact approved SHA, pull with `--ff-only`, then verify `HEAD` and clean state again.
- Stop for a changed remote SHA or dirty production checkout; do not reset, stash, merge, or force.
- Publish to a fresh unique `C:\SonAero\staging\...` directory and verify all expected modules.
  The existing publisher takes `-OutputRoot`, `-ProjectTrackerUrl '/project-tracker-api'`,
  and `-Configuration Release`; it must first be extended to include the new module.
- Run the correct first-install preview and validate its implemented marker. Record a single
  argument set and reuse its package, release ID, configuration, paths, and timeout for apply.
- Require the operator to attest `BACKUPS_VERIFIED` only after actual backups and restore
  evidence exist for every affected SQL database and persistent store, including Quality's
  SQLite file if still active. Readiness checks alone do not create or prove backups.
- Apply only after preview and prerequisites pass; require the script's actual success marker.
- Verify all existing services as well as the new module, identity, database/storage behavior,
  and active release paths. Emit a release-specific final completion marker.
- Distinguish failed apply/rollback from a failed postcheck after successful apply.

For subsequent ordinary full-Hub releases, the existing markers are `WHATIF_READY` and
`HUB_RELEASE_DEPLOYED_AND_HEALTHY`. They only cover applications implemented in the script.
Do not invent first-install markers or pass unsupported switches. Generate and validate them
as part of the new transaction and its tests. Portal-only deployment does not update other modules.

Parse the exact supplied PowerShell block with the Windows PowerShell 5.1 parser and require
zero errors before delivery. Use ASCII quotes and actual newlines. Provide resolved values and
the current approved SHA in the final operator command, never an old release's copied command.
See the [deployment handoff](codex-production-deployment-handoff.md) for the parser procedure
and detailed command-generation requirements.

## 11. Prove the installation works

On SON-IIS2, verify the intended immutable paths and successful `/api/health` responses from:

- `https://hub.son4l.local`
- `https://projects.hub.son4l.local`
- `https://engineering.hub.son4l.local`
- `https://estimating.hub.son4l.local`
- `https://quality.hub.son4l.local`
- `https://hub.son4l.local/project-tracker-api`
- The new module's permanent HTTPS origin.

Also verify the actual production database server/name/provider and migration history. Read
configuration locally without printing secrets. SQL connection success as the administrator
does not establish application access; exercise a module endpoint that reads the database.

From a normal employee workstation:

1. Open the permanent Portal and module URLs without bypassing certificate validation.
2. Confirm automatic Windows sign-in and the exact expected `SON4L\username` at `/api/me`.
3. Confirm an assigned user can open the Portal card and load module data.
4. Confirm an authenticated unassigned user is denied protected module APIs with HTTP 403.
   HTTP 401 indicates authentication failure, not merely missing Arda permissions.
5. Verify viewer/editor/admin permissions with appropriate test accounts and approved test
   records. Exercise the critical workflow and, if applicable, file upload/download persistence.
6. Check that the records/files survive a controlled application-pool recycle or subsequent
   release in the agreed verification window. Do not use destructive production QA data cleanup.
7. Confirm existing modules remain functional and the browser console has no new errors.

If sign-in prompts persist, IT should inspect browser authentication allowlists, Local Intranet
mapping, and unique `HTTP/<hostname>` SPNs for the actual service identity. These are domain/
workstation settings; an IIS application release alone does not configure them.

For Portal catalog checks in PowerShell 5.1, avoid interpreting a nested JSON array as zero cards.
The established parsing pattern is:

```powershell
$response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials `
    -Uri 'https://hub.son4l.local/api/apps' -TimeoutSec 30
$parsed = $response.Content | ConvertFrom-Json -ErrorAction Stop
$apps = @($parsed)
$apps | Select-Object Id, Name, Url, Status
```

`/api/apps` is filtered for the current user. Compare it with the active Portal catalog and the
user's grants before diagnosing a missing card as a deployment failure.

## 12. Failure handling and completion record

| Result | Next action |
| --- | --- |
| Preview fails | Fix the named prerequisite; no apply should occur. |
| Apply fails or reports rollback | Preserve full output and transaction state; diagnose before retrying. |
| Apply success marker appears, then a postcheck fails | Investigate only the failed check; do not automatically redeploy. |
| SQL schema changed before application failure | Ask the DBA to assess compatibility and forward repair or tested restore; IIS rollback does not reverse schema changes. |
| Interrupted first install | Inspect ownership, partial resources, data, and recorded state; use the implemented recovery procedure. |

Record the final commit SHA, release ID, active paths, site/pool, hostname, certificate thumbprint,
database/provider, migration state, storage location, backup/restore evidence, success markers,
employee access results, date, operator, and recovery reference. Keep secrets out of the record.

Call the module **installed and verified** only when its permanent URL, application identity,
database, persistent storage where applicable, Portal access, and critical user workflow all pass.

## 13. Small Business Subcontracting: remaining work at this review

The application, local launcher, permissions, frontend publishing, and local UI exist.
Before its first company-server installation:

- Add it to publishing, normal release deployment, scoped first install, IIS/HTTPS, warm start,
  readiness, access tests, database provisioning, and backup coverage.
- Add its Production template and production Portal catalog entry/activation behavior.
- Replace `EnsureCreatedAsync()` with a reviewed migration path; reconcile any existing database.
- Confirm the proposed hostname/ports and provision SQL permissions and persistent document storage.
- Keep Fulcrum credentials in the protected integration store; verify connectivity and enable
  synchronization only through the authorized module workflow.
- Complete deployment tests, push the final implementation, and generate new commands from that SHA.

## References

- [Production implementation and deployment handoff](codex-production-deployment-handoff.md)
- [Portal application registration](adding-an-application.md) - card registration only, not a server installation procedure.
- [Deployment overview](../deployment/README.md)
- [Production rollout](../deployment/production-rollout.md)
- [Permanent HTTPS](../deployment/production-hostname-https.md)
- [Publisher](../deployment/Publish-Hub.ps1) and [ordinary Hub release](../deployment/Deploy-HubRelease.ps1)
- [SQL provisioning inventory](../deployment/Create-Databases.sql)
