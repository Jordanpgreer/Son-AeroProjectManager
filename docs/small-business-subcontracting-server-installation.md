# Install Small Business Subcontracting on the company servers

Prepared October 1, 2026. These instructions use the new scoped installation code in this
checkout. **Commit, review, and push that code before running the IIS steps.** The old
`7215855` release does not contain this implementation. Use the full SHA of the newly approved
release when prompted; the installer verifies it against both checked-out source and GitHub.

This installs the module at **https://subcontracting.hub.son4l.local**. It first updates the
existing five Hub applications from the same package, with the new Portal card hidden, then
installs Subcontracting and adds it to startup recovery. Employee access and card activation
are separate final steps. Existing Quality storage configuration is preserved.

The code has local automated coverage; actual IIS and SQL Server execution must be verified
on the company infrastructure. Do not interpret generated SQL or a local publish as a completed
server installation. Apply the SQL schema in a reviewed test environment first where available.

## What is being installed

| Resource | Exact value |
| --- | --- |
| Application server | SON-IIS2, `10.50.10.244` |
| SQL/file server | SON-SQL2, `10.50.10.242` |
| IIS site and pool | `SmallBusinessSubcontracting` |
| Employee address | `https://subcontracting.hub.son4l.local` on port 443 |
| Internal HTTP health binding | `*:5180:`; restrict access to the internal network |
| Module database | `SmallBusinessSubcontracting` on SON-SQL2 |
| Shared authorization database | Existing `ProjectTracker` on SON-SQL2 |
| File-share root on SON-SQL2 | `C:\SonAero\Data\SmallBusinessSubcontracting` |
| Documents on SON-SQL2 | `C:\SonAero\Data\SmallBusinessSubcontracting\Documents` |
| UNC path used by IIS | `\\SON-SQL2\SmallBusinessSubcontracting$\Documents` |
| Remote SQL/file identity | `SON4L\SON-IIS2$` using IIS ApplicationPoolIdentity |
| First-install release root | `C:\SonAero\releases\small-business-subcontracting` |
| Installer journal | `C:\ProgramData\SonAero\deployment-state\small-business-subcontracting` |

No pilot HTTPS port is added. The five existing applications' HTTPS transaction state remains
unchanged. Do not rerun the old whole-server IIS, SQL, or HTTPS bootstrap to add this module.

## 1. Release preparation on the development PC

Review and commit the module migrations, SQL/storage/install scripts, optional six-module
integration, Production templates, tests, and documentation. Exclude the existing modified
`apps/project-tracker/src/ProjectTracker.Api/project-tracker-dev.db`, generated artifacts,
live configuration, and credentials. Push the reviewed commit normally to `main`.

Record the full 40-character SHA after confirming local `HEAD` equals `origin/main`.
Use that same SHA for the SQL files copied to SON-SQL2 and the IIS checkout.
Do not supply the SHA of an earlier release or guess that `git pull` installed the application.

Installation entry points:

- [SQL database and permissions](../deployment/Initialize-SmallBusinessSubcontractingDatabase.sql)
- [SQL schema migrations](../deployment/migrations/SmallBusinessSubcontracting.Initial.sql)
- [Document directory/share setup](../deployment/Configure-SmallBusinessSubcontractingStorage.ps1)
- [First-install orchestrator](../deployment/Install-SmallBusinessSubcontracting.ps1)
- [Scoped IIS release and recovery](../deployment/Deploy-SmallBusinessSubcontractingRelease.ps1)
- [Employee access check](../deployment/Test-SmallBusinessSubcontractingAccess.ps1)

## 2. DNS and certificate prerequisites

Have company DNS resolve `subcontracting.hub.son4l.local` to `10.50.10.244`.
The certificate in **Local Computer > Personal** on SON-IIS2 must cover that name, have its
private key, be valid for at least 30 more days, and be trusted by employee workstations.
An existing `*.hub.son4l.local` certificate may qualify; verify its SAN and validity.

On SON-IIS2, these read-only commands help identify the values:

```powershell
Resolve-DnsName subcontracting.hub.son4l.local
Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Select-Object Subject, Thumbprint, NotAfter, DnsNameList
```

Record the chosen certificate's 40-character thumbprint. The installer rechecks DNS,
certificate coverage, expiry, and binding conflicts before changing IIS.

## 3. SON-SQL2: database, schema, and document storage

Copy the three SQL/storage files linked in step 1 from the approved release to a review folder
on SON-SQL2. The commands below assume `C:\SonAero\module-setup` and the original filenames;
retain `migrations\SmallBusinessSubcontracting.Initial.sql` beneath that folder.
This review folder is for setup files, not the application's persistent data.

Have the DBA confirm current restorable backups and restore evidence for existing affected
databases. Arrange backup coverage for the new database and document directory.

### Database and permissions: SSMS on SON-SQL2

1. Connect to the default SQL instance on SON-SQL2 as an authorized DBA.
2. Open `Initialize-SmallBusinessSubcontractingDatabase.sql`.
3. Enable **Query > SQLCMD Mode**; this file uses `:setvar` and `:on error exit`.
4. Review the account `SON4L\SON-IIS2$`, then execute the complete file.
5. Require `SMALL_BUSINESS_SUBCONTRACTING_DATABASE_READY` and read/write/migration role values of `1`.
6. Open the schema migration file and select **SmallBusinessSubcontracting** as the database.
7. Review and execute the complete migration script. It refuses a different server/database.
8. Verify the initial migration appears in `dbo.__EFMigrationsHistory` and the expected tables exist.

The provisioning script creates the module database if absent, grants scoped database roles,
and grants SELECT on required shared authorization/credential tables. It does not assign
Arda users or groups and does not restart SQL Server. Existing untracked module tables are
a stop condition requiring DBA reconciliation; do not drop them or fabricate migration history.

The migration is idempotent. The application also checks/applies its EF migration chain during
startup. Applying the reviewed schema first lets the DBA verify SQL execution before IIS startup.

### Document storage: elevated Windows PowerShell 5.1 on SON-SQL2

```powershell
& {
    $ErrorActionPreference = 'Stop'
    if ($env:COMPUTERNAME -ine 'SON-SQL2') { throw 'Run on SON-SQL2.' }
    Set-Location -LiteralPath 'C:\SonAero\module-setup'
    .\Configure-SmallBusinessSubcontractingStorage.ps1 -WhatIf
}
```

Require `WHATIF_READY_SMALL_BUSINESS_SUBCONTRACTING_STORAGE`, then apply:

```powershell
& {
    $ErrorActionPreference = 'Stop'
    Set-Location -LiteralPath 'C:\SonAero\module-setup'
    .\Configure-SmallBusinessSubcontractingStorage.ps1 -Confirm:$false
}
```

Require `SMALL_BUSINESS_SUBCONTRACTING_STORAGE_CONFIGURED`.
The script grants Administrators/SYSTEM control over the protected directories and the IIS
computer account Modify access; the SMB share grants Administrators Full and IIS Change.
It requires encrypted SMB, disables offline caching, and rejects conflicting existing ACLs
or shares rather than overwriting them. It preserves any files already present.

## 4. SON-IIS2: retrieve the approved release and install

Use an elevated, interactive **64-bit Windows PowerShell 5.1** window as an authorized
`SON4L\...` operator. Do not run from a Local System remote-management shell.
SQL, DNS, certificate, storage, and backups must already be ready.

The block prompts for the reviewed pushed SHA and certificate thumbprint. Those prompts are
intentional release inputs, not permission to select an arbitrary commit/certificate.

```powershell
& {
    $ErrorActionPreference = 'Stop'
    if ($env:COMPUTERNAME -ine 'SON-IIS2' -or
        $PSVersionTable.PSVersion.Major -ne 5 -or
        $PSVersionTable.PSVersion.Minor -ne 1 -or -not [Environment]::Is64BitProcess) {
        throw 'Run on SON-IIS2 in 64-bit Windows PowerShell 5.1.'
    }
    $releaseCommit = (Read-Host 'Paste the approved NEW release commit SHA (40 characters)').Trim()
    $certificate = ((Read-Host 'Paste the certificate thumbprint') -replace '\s', '')
    if ($releaseCommit -notmatch '^[a-fA-F0-9]{40}$' -or $certificate -notmatch '^[a-fA-F0-9]{40}$') {
        throw 'A full release SHA and certificate thumbprint are required.'
    }
    Set-Location -LiteralPath 'C:\SonAero\src\SonAeroInternalHub'
    $branch = (& git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -cne 'main') { throw 'Checkout must be on main.' }
    $dirty = @(& git status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count) { throw 'Checkout is dirty. Stop and preserve local changes.' }
    & git fetch --prune origin
    if ($LASTEXITCODE -ne 0) { throw 'Fetch failed.' }
    $remoteCommit = (& git rev-parse origin/main).Trim()
    if ($LASTEXITCODE -ne 0 -or $remoteCommit -ine $releaseCommit) { throw 'Approved commit does not match origin/main.' }
    & git pull --ff-only origin main
    if ($LASTEXITCODE -ne 0) { throw 'Fast-forward pull failed. Do not reset or merge the production checkout.' }
    $headCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $headCommit -ine $releaseCommit) { throw 'Pulled source does not match the approved SHA.' }
    & .\deployment\Install-SmallBusinessSubcontracting.ps1 `
        -ExpectedCommit $releaseCommit -CertificateThumbprint $certificate
}
```

The installer publishes a fresh **six-application** package, previews the existing five-app
update and new-module installation, then asks for `BACKUPS_VERIFIED`. Type that only after
independently checking the actual backup and restore evidence. The existing five applications
are updated, the new site is installed, and six-application startup recovery is configured.

Expected checkpoints, in order:

1. `WHATIF_READY`
2. `WHATIF_READY_SMALL_BUSINESS_SUBCONTRACTING_RELEASE`
3. Operator types `BACKUPS_VERIFIED`.
4. `HUB_RELEASE_DEPLOYED_AND_HEALTHY`
5. `SMALL_BUSINESS_SUBCONTRACTING_RELEASE_DEPLOYED_AND_HEALTHY`
6. `WARM_START_CONFIGURED_AND_HEALTHY`
7. `SMALL_BUSINESS_SUBCONTRACTING_INSTALLED_ACCESS_SETUP_PENDING`

The final marker deliberately leaves user access/card activation pending. The startup health
check verifies module tables/migration state, shared access tables, and storage. Startup also
creates, reads, and removes a unique temporary file to verify document write access as the
application worker. It does not synchronize vendors with Fulcrum.

## 5. Assign permissions and verify as an employee

In Portal **Admin > Arda Access**, assign the intended group/people the Small Business
Subcontracting permissions. Give your testing administrator the appropriate module permissions
too; being the IIS administrator does not automatically grant access inside the module.

On the test employee's normal workstation, run the access script copied from the same release:

```powershell
& .\Test-SmallBusinessSubcontractingAccess.ps1 `
    -ExpectedAccountName ([Security.Principal.WindowsIdentity]::GetCurrent().Name) `
    -ExpectedRole Viewer
```

Use the actual intended role (`Viewer`, `Editor`, or `Admin`). For a customized group, supply
`-ExpectedPermissions` with its exact permission keys. Repeat as an unassigned employee with
`-ExpectedRole NoAccess`; that must prove HTTP 403 rather than treating HTTP 401 as success.
Require `SMALL_BUSINESS_SUBCONTRACTING_USER_ACCESS_VERIFIED` for each expected result.

Open the module's permanent URL and verify Vendors and Compliance. Using approved test data,
verify document upload/download and persistence. Confirm the existing five applications still
work. Activate Fulcrum synchronization only through the authorized module workflow once its
existing protected credential and outbound connectivity are verified.

## 6. Make the Portal card available

Back on SON-IIS2, use an elevated domain account that is a Portal administrator and also has
Small Business Subcontracting access. Preview:

```powershell
Set-Location -LiteralPath 'C:\SonAero\src\SonAeroInternalHub'
.\deployment\Configure-PortalProductionModuleVisibility.ps1 -SmallBusinessSubcontractingOnly -WhatIf
```

Require `WHATIF_READY_PORTAL_PRODUCTION_MODULE_VISIBILITY`, followed by its explanatory text.
Then apply:

```powershell
.\deployment\Configure-PortalProductionModuleVisibility.ps1 -SmallBusinessSubcontractingOnly -Confirm:$false
```

Require `PORTAL_PRODUCTION_MODULE_POLICY_APPLIED_AND_VERIFIED` (or
`PORTAL_PRODUCTION_MODULE_POLICY_ALREADY_APPLIED_AND_VERIFIED`). This sets only the new card's
permanent URL, Active status, and visibility policy; API permissions remain independently enforced.
Verify the Portal card opens the permanent module URL from an authorized employee workstation.

## If a step fails

- **No preview marker:** stop before applying; fix the stated prerequisite.
- **The existing Hub updated but new-module installation failed:** preserve the completed Hub
  release. Diagnose the new module and use the scoped release script; do not rerun the whole installer.
- **Interrupted installation:** the protected journal blocks a new release. Use the transaction
  ID printed by the error with `Deploy-SmallBusinessSubcontractingRelease.ps1 -RecoverTransactionId`
  and preview with `-WhatIf` before approved recovery. Keep the original output and journal.
- **IIS rollback succeeded:** SQL migrations and document data remain. Do not drop databases or
  delete data directories; have the DBA assess any necessary repair.
- **Installed successfully but warm start/access/card checks failed:** repair that stage only.
- **HTTP 401 or repeated credentials:** investigate Windows authentication, workstation browser
  policy, and SPNs with IT. Do not grant broader Arda permissions to mask authentication failure.

## Later application updates

For an ordinary full-Hub release, both publishing and deployment must explicitly use
`-IncludeSmallBusinessSubcontracting`. The deploy script refuses an old five-app invocation
once the Subcontracting site exists. Include the module in backup readiness and employee checks.

For a strictly Subcontracting-only release with no shared/Portal changes, use the scoped
`Deploy-SmallBusinessSubcontractingRelease.ps1` without `-FirstInstall` or
`-ProductionSettingsPath`; it preserves the active Production configuration byte-for-byte.
Use a fresh package/release ID, validated certificate, preview, verified backups, and health checks.
Do not rerun SQL provisioning or initial storage setup as a substitute for an ordinary update.

Retain the release SHA/ID, active IIS paths, migration history, backup/restore evidence, success
markers, operator/date, and employee test results with the deployment record.

## Local implementation verification

On October 1, 2026, before release:

- All **939 .NET tests** passed across the six applications, including 24 Subcontracting tests.
- All **21 deployment test scripts** passed in Windows PowerShell 5.1; the scoped installer
  suite covers 107 assertions, including rollback, interrupted recovery, and journal replacement.
- Subcontracting frontend lint and all five frontend tests passed.
- Publishing all six applications succeeded. The resulting Subcontracting package passed the
  installer's manifest/web.config checks, and its Production template passed configuration validation.
- Runbook PowerShell blocks parsed and linked implementation files existed.

The test run also corrected a Quality test fixture that crossed the October quarter boundary;
Quality application behavior was unchanged. Actual SQL Server schema execution, IIS changes,
SMB permissions, DNS/certificate trust, and employee sign-in remain server acceptance checks.
