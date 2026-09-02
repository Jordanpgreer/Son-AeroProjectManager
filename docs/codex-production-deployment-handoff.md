# SON-AERO Hub production implementation and deployment handoff

## Purpose

This reusable handoff covers implementation review, testing, safe commit/push, IIS-versus-SQL
boundaries, PowerShell 5.1 command generation, and success/rollback interpretation. It is guidance,
not permission: follow the current request, verify live state, and never substitute the dated
snapshot below for current checks.

## Instruction and evidence priority

When instructions differ, use this order:

1. the user's current, explicit request;
2. the current repository's `AGENTS.md` and other scoped instruction files;
3. the current implementation in `deployment/*.ps1` and its tests;
4. `deployment/README.md` and the current production runbooks;
5. this handoff;
6. old screenshots, old commands, and remembered release IDs.

Scripts/tests are the executable contract. Do not revive historical HTTP, pilot, or manual-copy
procedures when a current immutable deployment script covers the operation.

## Repository and production topology

| Item | Current convention |
| --- | --- |
| Development checkout | `C:\Users\USER\projects\non project folder\Project Tracker` |
| Production checkout | `C:\SonAero\src\SonAeroInternalHub` on SON-IIS2 |
| Application server | SON-IIS2 (`10.50.10.244`) |
| SQL/file server | SON-SQL2 (`10.50.10.242`) |
| Production branch | `main` unless the user explicitly changes the release policy |
| Portal | `https://hub.son4l.local` |
| Project Tracker | `https://projects.hub.son4l.local` |
| Engineering Hub | `https://engineering.hub.son4l.local` |
| Estimating Dashboard | `https://estimating.hub.son4l.local` |
| Quality Assurance | `https://quality.hub.son4l.local` |
| Project Tracker gateway | `https://hub.son4l.local/project-tracker-api` |
| Persistent Quality SQLite | `C:\ProgramData\SonAero\deployment-state\quality-assurance-data\quality-assurance.db` |

Production uses IIS, Windows Authentication, immutable releases, preserved Production configuration,
health-gated startup, and IIS-path rollback. Never point IIS at Git or staging.

## Dated state at this handoff

As of 2026-09-01, local/remote `main` and the release source were
`64ed9fc09b1adc35d3926cc1d32cfcfb626371f3`; the local worktree separately retained a modified
`project-tracker-dev.db`. A normal five-app release emitted `HUB_RELEASE_DEPLOYED_AND_HEALTHY`, and
all six HTTPS health/identity endpoints, auth, CORS, and Estimating rules checks passed. Quality used
the SQLite bridge with shared access in SON-SQL2 `ProjectTracker`. A later card check returned zero;
PS5 nesting can cause that false result, but active `AllowedRoles` must also be checked before deciding.

## Non-negotiable safety rules

- Never overwrite, delete, restore, commit, or reset `project-tracker-dev.db` unless the user
  explicitly requests that exact database operation.
- Never use `git reset --hard`, forced checkout, force-push, an automatic broad stash, or an
  unreviewed merge to make a checkout appear clean.
- Never stage with `git add -A` until every modified and untracked path has been reviewed. Prefer
  explicit path lists.
- Never commit secrets, active Production settings, passwords, tokens, `.env` files, private keys,
  PFX files, live databases, or generated release directories.
- Never deploy a commit that was not pushed and then verified against `origin/main`.
- Never reuse a failed or incomplete package/release directory. Use a fresh, unique ID.
- Never manually copy DLLs into a running IIS application, point IIS at staging, or use
  `xcopy`, `Copy-Item -Force`, or manual physical-path changes as a deployment substitute.
- Never use the local Development `scripts/Start-Hub.ps1` launcher as a production deployment.
- Never weaken a manifest, configuration, ACL, identity, backup, or health guard merely to get a
  green marker.
- Never delete a retained failed release or rollback state before it has been diagnosed.
- Never blindly rerun after an automatic rollback, a missing success marker, or a message telling
  the operator to stop.
- Never manually edit active `appsettings.Production.json` to repair a deployment.
- Never delete, move, replace, recreate, or casually copy the live Quality SQLite database.
- Never paste T-SQL directly into PowerShell. `GO`, `IF DB_ID(...)`, and other T-SQL belong in SSMS,
  `sqlcmd`, or a reviewed SQL input file.
- Pressure or urgency does not authorize bypassing backups, tests, or rollback boundaries.

## Execution contexts

| Work | Correct location and identity |
| --- | --- |
| Implement, test, commit, push | Development checkout where Codex is installed |
| Pull, publish, preview, deploy, IIS inspection | Interactive RDP session on SON-IIS2, elevated **Windows PowerShell 5.1**, authorized `SON4L\...` user |
| SQL provisioning or DBA repair | SON-SQL2 using SSMS or an explicitly reviewed SQL/PowerShell provisioning script and sufficient SQL authority |
| Employee shortcut install | The employee's normal workstation session; extract installer ZIP first and approve UAC when requested |
| N-central System Shell | Only commands explicitly documented for Local System; not user identity/access verification or interactive release deployment |

Git commands require the checkout on SON-IIS2; a SQL Server desktop does not turn PowerShell into a
T-SQL console.

## Phase 1: inspect and understand the requested change

Before editing or promising a push:

```powershell
git status --short --branch; git rev-parse --show-toplevel; git remote -v
git log -5 --oneline --decorate; git diff --stat; git diff --name-status
git diff --check
```

Then:

1. inventory all modified and untracked files;
2. identify pre-existing user work and preserve it;
3. inspect the complete diff, not only the files mentioned in the request;
4. map each changed file to its application, API, migration, shared library, deployment script,
   production template, or branding source;
5. inspect current `origin/main` before deciding what must be merged or released;
6. decide whether the work is implementation, diagnosis only, documentation only, or production
   deployment plumbing;
7. do not commit or push until the user authorizes it.

If the local development database is modified, record its hash and ensure it never enters the index:

```powershell
$devDb = 'apps\project-tracker\src\ProjectTracker.Api\project-tracker-dev.db'
$devDbHashBefore = if (Test-Path -LiteralPath $devDb) { (Get-FileHash -Algorithm SHA256 -LiteralPath $devDb).Hash }
git diff --cached --name-only -- $devDb
```

If the final command prints the database path, stop and unstage only that path. Do not discard it.

## Phase 2: test before pushing

Match tests to the changed surface and report exactly what passed, failed, or was not run.

### Baseline .NET tests

Use an installed .NET 8 SDK. If system `dotnet` has no SDK, the known development fallback is
`C:\Users\USER\.dotnet\dotnet.exe`.

```powershell
dotnet test .\SonAeroInternalHub.sln --configuration Release --nologo
```

For a narrow change, run the affected project first, but run the full solution before a full-Hub
production push when practical. Database/migration changes require the affected migration tests.

### Frontend commands

Run frontend commands inside the relevant `ClientApp` directory, never at repository root.

| Application | Directory | Required commands when affected |
| --- | --- | --- |
| Project Tracker | `apps/project-tracker/src/ProjectTracker.Api/ClientApp` | `npm run lint`, all four `npm run test:*` scripts, `npm run build` |
| Portal | `apps/portal/src/Portal.Api/ClientApp` | `npm run lint`, `npm test`, `npm run build` |
| Engineering | `apps/engineering-hub/src/EngineeringHub.Api/ClientApp` | `npm run lint`, `npm run build` |
| Estimating | `apps/estimating-dashboard/src/EstimatingDashboard.Api/ClientApp` | `npm run lint`, `npm test`, `npm run build` |
| Quality | `apps/quality-assurance/src/QualityAssurance.Api/ClientApp` | `npm run lint`, `npm test`, `npm run build` |

Use `npm ci` when a clean dependency installation is needed and a lockfile is present. Do not claim
the API serves new frontend output merely because `ClientApp/dist` built; Project Tracker serves
generated assets from its API `wwwroot`, and normal publish/start workflows must refresh them.

### Deployment contract tests

Deployment scripts target Windows PowerShell 5.1. Run the applicable direct tests with Windows
PowerShell, not only PowerShell 7:

```powershell
$ps51 = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$tests = Get-ChildItem -LiteralPath '.\tests\deployment' -Filter '*.Tests.ps1' | Sort-Object Name
foreach ($test in $tests) {
    & $ps51 -NoLogo -NoProfile -ExecutionPolicy Bypass -File $test.FullName
    if ($LASTEXITCODE -ne 0) { throw "Deployment test failed: $($test.Name)" }
}
```

When HTTPS scripts are touched, also run the matching scripts under `deployment/tests`. Require each
test's explicit `*_PASSED` marker; an exit code alone is insufficient if the test specifies a marker.

### Quality database tests

Quality changes require the full Quality test project, including:

- `QualitySqliteMigrationTests`: full migration chain against a precreated `Mode=ReadWrite` SQLite
  file and refusal to create a missing database;
- `QualitySqlServerMigrationTests`: migration discovery/order, SQL Server-native schema, identities,
  provider-specific SQL, and model snapshot checks;
- incremental migration/backfill and feature-specific service tests.

Do not approve a Quality production release from a frontend test alone.

### UI and runtime verification

For UI behavior:

1. inspect listeners and health before starting a new local process;
2. preserve an already healthy process rather than creating a duplicate;
3. test the real interaction in a browser, including close/reopen or return paths;
4. inspect console errors;
5. test a narrow viewport when layout is affected;
6. rebuild/publish the actual assets served by the API before claiming visual success.

### Final pre-commit checks

```powershell
git diff --check; git status --short; git diff --stat
```

Also inspect staged content for credentials, active configuration, generated packages, database
files, and unexpected binaries.

## Phase 3: commit and push safely

Only after authorization and passing tests:

1. stage intended paths explicitly;
2. inspect the staged patch and path list;
3. commit with a focused conventional message;
4. push normally, never force;
5. fetch and prove local HEAD exactly equals `origin/main`;
6. give the full 40-character commit SHA to the deployment command generator.

Example procedure, with the path list replaced by reviewed paths:

```powershell
git add -- path\one path\two
git diff --cached --check; git diff --cached --name-status; git diff --cached --stat
git diff --cached
git commit -m "feat(scope): concise description"
if ($LASTEXITCODE -ne 0) { throw 'Commit failed.' }
git push origin main
if ($LASTEXITCODE -ne 0) { throw 'Push failed; do not deploy.' }
git fetch --prune origin
$head = (git rev-parse HEAD).Trim()
$remote = (git rev-parse origin/main).Trim()
$divergence = (git rev-list --left-right --count HEAD...origin/main).Trim()
if ($head -cne $remote -or $divergence -cne "0`t0") {
    throw "Push verification failed: HEAD=$head origin/main=$remote divergence=$divergence"
}
Write-Host "SOURCE_COMMIT_VERIFIED $head" -ForegroundColor Green
```

If a fast-forward pull is required while an unrelated local file is dirty, do not broad-stash.
Inspect overlap first. A targeted stash is acceptable only for the exact preserved path, with a
before/after SHA-256 check and removal of the stash only after restoration is proven. On the
production checkout, any dirty state is a stop condition; do not stash or reset it automatically.

## Phase 4: decide whether database work is required

### Normal deployment: usually no separate SQL command

Normal full-Hub and scoped deployments carry active Production configuration forward. Applications
run reviewed EF migrations during serial, health-gated startup. Do not invent a SQL command merely
because the application uses SQL Server.

A current restorable backup is still mandatory because IIS rollback does not undo a database schema
or data migration.

### SQL administrator work is required when

- a required database does not exist;
- `SON4L\SON-IIS2$` lacks required login/database roles;
- startup reports SQL connectivity, login, create-database, or DDL permission failures;
- first SQL-backed Quality activation is planned;
- migration history or a partial schema must be reconciled;
- a reviewed migration explicitly requires DBA work;
- the Quality SQLite bridge will be migrated to the dedicated SON-SQL2 Quality database.

Use `deployment/Configure-SqlServer.ps1` elevated on SON-SQL2 for the reviewed provisioning path, or
run `deployment/Create-Databases.sql` through real SQL tooling. Do not paste its T-SQL into ordinary
PowerShell. `Configure-SqlServer.ps1` currently lacks formal preview/apply success markers, so do not
embed it in a routine release wrapper; inspect or improve its verification separately. If the
operator lacks SQL authority, stop and state the exact DBA action required.

### Current Quality storage boundary

The reviewed bridge keeps shared users, groups, and permissions in SON-SQL2 `ProjectTracker` while
Quality operational data is stored only in the protected persistent SQLite file. The connection is
`Mode=ReadWrite`; a missing file must fail instead of silently creating an empty database.

While active, ordinary releases omit all one-time SQLite switches. Deployment revalidates the exact
nonempty path, allowed db/journal/shm/wal contents, non-reparse paths, protected exact ACLs,
ApplicationPoolIdentity, one worker, and non-overlapping recycle.

### Backup gate

Before any schema-capable apply, independently verify current restorable backups for every affected
SQL database and persistent data store. While the SQLite bridge is active, include the Quality file
using an approved consistent backup procedure. A live `Copy-Item` is not automatically a valid
SQLite backup, especially with WAL files.
For a full-Hub apply, treat every persistent store whose candidate may run migrations as affected,
even when the visible UI diff suggests otherwise.

`Test-HubBackupReadiness.ps1` is read-only prerequisite evidence. Its
`BACKUP_PREREQUISITES_READY` marker does not create a backup and does not prove a restore. Do not let
an implementation wrapper print `BACKUPS_VERIFIED` automatically. The human may type that marker
only after the required backups and restore evidence genuinely exist.

## Phase 5: choose the deployment transaction

| Change scope/state | Script and mode | Preview marker | Apply marker |
| --- | --- | --- | --- |
| Any shared code/config, Engineering, Estimating, multiple modules, or Quality combined with another module/shared change | `Deploy-HubRelease.ps1` without retention | `WHATIF_READY` | `HUB_RELEASE_DEPLOYED_AND_HEALTHY` |
| Only Project Tracker, with no Portal, Engineering, Estimating, Quality, shared-production-configuration, or cross-module database dependency | `Deploy-ProjectTrackerRelease.ps1` | `WHATIF_READY_PROJECT_TRACKER_RELEASE` | `PROJECT_TRACKER_RELEASE_DEPLOYED_AND_HEALTHY` |
| Only Portal root | `Deploy-PortalRelease.ps1` | `WHATIF_READY_PORTAL_RELEASE` | `PORTAL_RELEASE_DEPLOYED_AND_HEALTHY` |
| Compatible Project Tracker plus Portal | Publish once; Project Tracker transaction first, then Portal transaction | `WHATIF_READY_PROJECT_TRACKER_RELEASE`, then `WHATIF_READY_PORTAL_RELEASE` | `PROJECT_TRACKER_RELEASE_DEPLOYED_AND_HEALTHY`, then `PORTAL_RELEASE_DEPLOYED_AND_HEALTHY` |
| Ordinary healthy Quality-only update | `Deploy-QualityAssuranceRelease.ps1`, no special switch | `WHATIF_READY_QUALITY_ASSURANCE_RELEASE` | `QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY` |
| Reviewed first SQL-backed Quality activation with old site unhealthy | Quality script with `-FirstActivation` | `WHATIF_READY_QUALITY_ASSURANCE_RELEASE` | `QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY` |
| Exact legacy Quality config missing both reviewed database leaves | Quality script with `-RepairMissingProductionDatabaseSettings` | `WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_PRODUCTION_DATABASE_SETTINGS_REPAIRED` | `QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_PRODUCTION_DATABASE_SETTINGS_REPAIRED` |
| One-time reviewed empty-Quality SQLite transition only | Quality script with `-UseServerLocalSqlite` | `WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_SERVER_LOCAL_SQLITE` | `QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SERVER_LOCAL_SQLITE` |
| Exact protected empty prepared-directory recovery only | Add `-ResumeServerLocalSqlitePreparation` with the SQLite switch | `WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_SERVER_LOCAL_SQLITE_RESUME` | `QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SERVER_LOCAL_SQLITE_RESUME` |
| Immediately after a scoped Quality repair, while package and active Quality artifacts are proven equal | Full Hub with `-RetainVerifiedQuality` | `WHATIF_READY_HUB_RELEASE_WITH_VERIFIED_QUALITY_RETAINED` | `HUB_RELEASE_DEPLOYED_AND_HEALTHY_WITH_VERIFIED_QUALITY_RETAINED` |

`-FirstActivation` and `-RepairMissingProductionDatabaseSettings` are mutually exclusive; SQLite is mutually exclusive with either, and `-ResumeServerLocalSqlitePreparation` requires `-UseServerLocalSqlite`. If the diff is ambiguous or touches shared production behavior, prefer the normal five-application transaction.

### Critical retained-Quality lesson

`-RetainVerifiedQuality` is not a general way to avoid deploying Quality. It requires package and active Quality artifacts to match byte-for-byte except for explicitly excluded configuration. A prior package had 109 sanitized artifacts while active Quality had 110 because active and source were from different commits. The guard correctly stopped before IIS changed.

Never fix this by deleting the extra active DLL, weakening the count/hash comparison, or copying the
active tree into staging to manufacture equality. If current Quality changes are intended, use a
fresh normal full-Hub deployment. Retention may use only the intended pushed commit's fresh package,
normally the package used for the scoped Quality repair. If it does not match active Quality, retention
is ineligible; never select an older package merely because its Quality tree matches.

## Phase 6: requirements for the generated SON-IIS2 command

The receiving Codex must generate one self-contained PowerShell block and validate the exact text before giving it to the operator.

### Required command structure

1. outer `& { ... }` block;
2. `$ErrorActionPreference = 'Stop'`;
3. Windows PowerShell major-version check for version 5;
4. exact computer check for SON-IIS2;
5. interactive authorized `SON4L\...` identity check and administrator check;
6. exact production repository path;
7. exact expected full 40-character pushed commit SHA, never a placeholder in the final command;
8. clean `main` checkout check before fetch/pull;
9. `git fetch --prune origin`, exact `origin/main` comparison, `git pull --ff-only`, exact HEAD
   comparison, and a second clean check;
10. explicit backup attestation before apply;
11. preflight of currently active applications and any special Quality storage boundary;
12. compute a fresh timestamped `$packageRoot`, valid `$releaseId`, explicit `$releaseRoot`, timeout,
    and mode switches once; require the release ID to match `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`
    (prefer 12 SHA characters plus a short scope/timestamp; keep the full SHA in the commit check);
13. `Publish-Hub.ps1 -OutputRoot $packageRoot -ProjectTrackerUrl '/project-tracker-api' -Configuration Release`;
14. a post-build clean-checkout assertion;
15. deployment preview using `-PackageRoot $packageRoot` and one captured base splat, followed by an exact preview-marker assertion;
16. deployment apply using the same base splat unchanged, adding only `-Confirm:$false` instead of `-WhatIf`, followed by an exact apply-marker assertion;
17. postchecks appropriate to the changed feature;
18. a final unique green completion marker;
19. a `catch` that distinguishes failure before verified apply from failure during a later postcheck.

Package path, release ID/root, mode switches, and timeout must be reused unchanged between preview and apply. Use a 300-second health timeout unless current evidence supports another value; scripts accept 30-600 seconds.

### Formatting contract

- Generate ASCII quotes/apostrophes only; a Unicode U+2019 apostrophe can break parsing.
- Prefer splatting or one argument per line. If backticks are used, the backtick must be the final
  character on the line with no trailing spaces.
- Never leave an empty pipe or a pipe before a closing brace.
- Never include `PS C:\...>`/`>>` prompt text. Unexpected `>>` means incomplete syntax: press
  Ctrl+C and repaste the complete balanced block.
- Avoid a colon immediately after an interpolated variable such as `"$uri: failed"`; use
  `("{0}: failed" -f $uri)` or `${uri}`.
- Do not use broad `Set-StrictMode -Version Latest` around repository scripts; earlier wrappers
  converted optional-property reads into unrelated failures.
- Do not use `Out-LineOutput` for captured deployment results.
- Capture preview/apply output, print it, filter string objects, and require exact marker equality.
- Use `$LASTEXITCODE` after external programs such as Git and child `powershell.exe`; use exceptions
  and explicit markers for invoked PowerShell deployment scripts.
- Keep T-SQL out of the IIS command.
- Do not make the command delete old packages, releases, logs, rollback state, or databases.

### Mandatory parser validation before delivery

Write the exact proposed command to a temporary `.ps1`, parse it with Windows PowerShell 5.1, and
require zero errors:

```powershell
$checkerSource = @'
$tokens=$null; $errors=$null
[void][System.Management.Automation.Language.Parser]::ParseFile($env:SONAERO_PARSE_TARGET,[ref]$tokens,[ref]$errors)
if (@($errors).Count) { $errors | ForEach-Object Message; exit 1 }
'WINDOWS_POWERSHELL_51_PARSE_OK'
'@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($checkerSource))
$env:SONAERO_PARSE_TARGET = $temporaryScript
try {
    $parseOutput = @(& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -EncodedCommand $encoded)
    $parseExit = $LASTEXITCODE
    $parseOutput | Out-Host
    if ($parseExit -ne 0 -or $parseOutput -notcontains 'WINDOWS_POWERSHELL_51_PARSE_OK') {
        throw 'Do not give this command to the operator.'
    }
} finally {
    Remove-Item Env:SONAERO_PARSE_TARGET -ErrorAction SilentlyContinue
}
```

After validation, remove only that known temporary file. Parser success does not replace tests or
live preflight checks.

## Phase 7: post-deployment verification

### Success versus postcheck failure

Set an internal flag only after the exact apply marker is captured. If a later read-only postcheck
fails, print a message equivalent to:

```text
POSTCHECK_STOP_AFTER_VERIFIED_APPLY: Deployment succeeded but a postcheck failed. DO NOT rerun deployment.
```

Do not label a verified deployment as failed, and do not redeploy to fix a read-only check.

If apply itself throws and the deployment reports that previous IIS paths were restored, that is an
automatic rollback. Stop. Preserve the output and retained candidate. Do not recycle pools, change
paths, or rerun with the same ID.

### Required service checks

Verify HTTP 200 from `/api/health` at `hub.son4l.local`, `projects.hub.son4l.local`,
`engineering.hub.son4l.local`, `estimating.hub.son4l.local`, `quality.hub.son4l.local`, and
`hub.son4l.local/project-tracker-api` (all using HTTPS).

Run `Test-HubUserAccess.ps1` as the actual employee with explicit expected roles/access. Authorized
surfaces must return 200 and the exact account; expected NoAccess must return 403. A 401 always means
Windows authentication failed. Verify feature-specific endpoints, not only generic health.

For Project Tracker, preserve these boundaries:

- direct site: Anonymous=True and Windows=True so CORS preflight can reach the app; application
  authorization must still return HTTP 401 for anonymous `/api/me`;
- Portal gateway: Anonymous=False and Windows=True;
- permanent hostname and retained HTTP/HTTPS pilot origins must pass the configured CORS preflight.

### Windows PowerShell 5.1 JSON-array rule

Do not write:

```powershell
$apps = @(Invoke-RestMethod -UseDefaultCredentials -Uri $uri)
```

Windows PowerShell 5.1 can preserve the JSON array as one nested object, causing every card lookup
to report zero. Use the repository-tested pattern:

```powershell
$response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec 30
$parsed = $response.Content | ConvertFrom-Json -ErrorAction Stop
$apps = @($parsed)
```

The configured Portal catalog and `/api/apps` answer different questions. Active
`appsettings.Production.json` proves what is configured. `/api/apps` is filtered for the current
user's role and module access. A missing user-visible card can be an access-policy issue even when
the deployment is healthy. Do not redeploy until the distinction is diagnosed.

## Portal module visibility and access

Portal visibility is presentation, not authorization. `Configure-PortalProductionModuleVisibility.ps1`
changes only Engineering/Quality visibility and cannot repair Project Tracker `AllowedRoles`; use it
only when its preconditions apply, preview first, and require its exact markers.

The Admin Console card is Admin-only. Engineering, Estimating, and Quality cards can be filtered by
module access. Project Tracker has independent application authorization. Always verify access as
the actual employee in that employee's normal Windows session; an administrator's successful test
does not prove Josh's access.

## Employee shortcut and browser authentication

The permanent shortcut target is `https://hub.son4l.local`. Build with
`New-EmployeeHubInstallerPackage.ps1`, preview first, and inspect the ZIP. On the employee computer:

1. use **Extract All**, then run `Install Son-Aero Hub.cmd` from the extracted folder;
2. run from the employee's normal session and approve UAC if requested;
3. require `SONAERO_HUB_EMPLOYEE_INSTALL_COMPLETE` and verify `Arda` opens the HTTPS Portal.

Do not launch the installer from inside the ZIP. Browser integrated-auth allowlists and Local
Intranet mapping are per-workstation/per-user policy concerns. Run them on the employee workstation,
not on IIS or SQL, and do not claim an IIS deployment fixes a Chrome/Edge policy problem.

## What to provide to the operator

After pushing, the receiving Codex should provide:

1. the pushed full commit SHA and a short tested-change summary;
2. the exact machine, shell, elevation, and identity required;
3. one complete copy/paste command with no placeholders;
4. any separately required SQL/backup prerequisite, clearly separated from the IIS command;
5. the exact expected preview, apply, and final postcheck markers;
6. explicit stop instructions for rollback, missing markers, and postcheck-only failures;
7. workstation shortcut steps only if the release changes or requires them.

The operator should never have to infer whether a command belongs on IIS, SQL, or a workstation.

## Final receiving-Codex checklist

- [ ] Inspect instructions/diffs, preserve unrelated work/dev DB, and run every affected test.
- [ ] Stage intentionally; commit/push only when authorized; prove HEAD equals `origin/main`.
- [ ] Verify real backups, choose an eligible mode/fresh ID, and PS5-parse the exact command.
- [ ] Require exact markers/postchecks; never misuse special Quality switches or blindly rerun.
