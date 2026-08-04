# SON-AERO Hub production rollout

This runbook is for the current two-server environment:

| Server | Purpose |
|---|---|
| `SON-IIS2` | IIS and the four Hub applications |
| `SON-SQL2` | `ProjectTracker` and `EngineeringHub` SQL databases plus Engineering drawings |

Current HTTP endpoints are Portal `:5140`, Project Tracker `:5135`, Engineering `:5150`, and
Estimating `:5160`. Complete the sections in order. In particular, deploy the default-deny release
and assign roles **before** giving employees the shortcut.

## Shell conventions

- Commands labelled **N-central CMD** are single-line commands for the N-central System Shell. It
  is a CMD prompt running as `NT AUTHORITY\SYSTEM`, so PowerShell cmdlets must be launched through
  `powershell.exe`.
- Commands labelled **employee PowerShell** must be run in the employee's normal interactive
  `SON4L\firstname.lastname` Windows session. Do not run user-access tests through N-central.
- Upload the referenced script and `son-aero.ico` to the exact paths shown before running a
  command. Run `-WhatIf` first wherever it is offered.

## 1. Deploy the default-deny release first

The release stops new authenticated visitors from being automatically granted access and enforces
Project Tracker's module-view permission. Existing records are handled by the mandatory audit in
section 3. Do not deploy the employee shortcut until this release is healthy.

The Portal shell may still open for an authenticated SON4L employee; that does not grant module
access. Project Tracker, Engineering, and Estimating remain denied until their explicit group or
module assignment is saved.

On `SON-IIS2`, update the existing source checkout and publish from the commit supplied for this
rollout. Do not point IIS at the checkout or copy files over a running site. The deploy script
builds a new immutable release folder, preserves the four current Production settings files,
switches all four IIS paths together, checks health, and restores the previous paths if the new
release is not healthy.

**Elevated PowerShell on SON-IIS2:**

```powershell
$repo = 'C:\SonAero\src\SonAeroInternalHub'

if (-not (Test-Path -LiteralPath "$repo\.git")) {
  if (Test-Path -LiteralPath $repo) {
    throw "$repo exists but is not a Git checkout. Do not overwrite it; inspect or rename it first."
  }
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $repo) | Out-Null
  git clone https://github.com/Jordanpgreer/Son-AeroProjectManager.git $repo
  if ($LASTEXITCODE -ne 0) { throw 'Initial Git clone failed; IIS was not changed.' }
}

git -C $repo fetch --prune origin
if ($LASTEXITCODE -ne 0) { throw 'git fetch failed.' }
git -C $repo pull --ff-only origin main
if ($LASTEXITCODE -ne 0) { throw 'git pull failed; do not merge or force-reset the server checkout.' }

$dirty = @(git -C $repo status --porcelain)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) {
  throw "The server checkout is not clean. Stop and review: $($dirty -join '; ')"
}

$releaseId = (git -C $repo rev-parse --short HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($releaseId)) {
  throw 'Could not determine the release commit.'
}
$packageRoot = "C:\SonAero\staging\hub-$releaseId"
if (Test-Path -LiteralPath $packageRoot) {
  throw "Staging path already exists; inspect it and choose a unique release ID: $packageRoot"
}

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "$repo\deployment\Publish-Hub.ps1" `
  -OutputRoot $packageRoot `
  -ProjectTrackerUrl 'http://SON-IIS2:5135'
if ($LASTEXITCODE -ne 0) { throw 'Hub publish failed; IIS was not changed.' }

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "$repo\deployment\Deploy-HubRelease.ps1" `
  -PackageRoot $packageRoot `
  -ReleaseId $releaseId `
  -WhatIf
if ($LASTEXITCODE -ne 0) { throw 'Release preview failed; IIS was not changed.' }

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "$repo\deployment\Deploy-HubRelease.ps1" `
  -PackageRoot $packageRoot `
  -ReleaseId $releaseId `
  -Confirm:$false
if ($LASTEXITCODE -ne 0) { throw 'Release deployment failed; review the rollback result.' }
```

The preview must end with `WHATIF_READY`. The apply run must end with
`HUB_RELEASE_DEPLOYED_AND_HEALTHY`. Keep the previous immutable release and the pre-deployment SQL
backup. Do not use `xcopy`, `Copy-Item -Force`, or a manual DLL replacement against a running IIS
application pool.

After cutover, verify all four health endpoints from a domain workstation:

```text
http://SON-IIS2:5135/api/health
http://SON-IIS2:5140/api/health
http://SON-IIS2:5150/api/health
http://SON-IIS2:5160/api/health
```

All four must return HTTP 200 before continuing.

## 2. Configure warm start on SON-IIS2

Upload `Configure-IisWarmStart.ps1` to `C:\SonAero\Configure-IisWarmStart.ps1` on `SON-IIS2`.
This enables IIS Application Initialization, always-running pools, preload, and authenticated
health warming. It does not remove normal scheduled recycling.

**N-central CMD on SON-IIS2 — preview:**

```bat
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\SonAero\Configure-IisWarmStart.ps1" -Scheme http -WhatIf
```

Expected final line:
`WHATIF_READY: no IIS features, settings, files, or scheduled tasks were changed.`

**N-central CMD on SON-IIS2 — apply:**

```bat
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\SonAero\Configure-IisWarmStart.ps1" -Scheme http -Confirm:$false
```

Expected final line: `WARM_START_CONFIGURED_AND_HEALTHY`, with HTTP 200 for all four sites. The
apply run also installs an idempotent Local System startup-recovery task with bounded retries, so a
simultaneous SON-IIS2/SON-SQL2 reboot can recover after SQL becomes available. Stop and investigate
if any site is not healthy; do not distribute shortcuts around a failed result.

## 3. Audit legacy access, then assign each employee's roles

The release stops **new** visitors from being auto-provisioned, but it intentionally does not
guess which existing records were previously approved. Before distributing shortcuts, review
every active user already listed on the three access pages. Remove unintended Engineering and
Estimating assignments, and either deactivate unintended Project Tracker users or remove all of
their Project Tracker groups. An active user with no Project Tracker group is denied by the new
release. Keep at least one verified administrator; the API prevents removal of the final access
manager/module administrator.

Sign in as the existing Hub administrator and use these Portal pages:

- Project Tracker groups: `http://SON-IIS2:5140/#/admin/project-tracker/access`
- Engineering role: `http://SON-IIS2:5140/#/admin/engineering/access`
- Estimating role: `http://SON-IIS2:5140/#/admin/estimating/access`

Enter every account in canonical form, for example `SON4L\jordan.greer`. For each employee:

1. Add only the Project Tracker group(s) the employee needs.
2. Set Engineering to `Viewer`, `Editor`, `Admin`, or no assignment.
3. Set Estimating to `Viewer`, `Editor`, `Admin`, or no assignment.
4. Save, then reopen the employee to confirm the stored assignments.

Also test at least one prior View Only/legacy user who should now have no access. Clean up that
record first, then use the `NoAccess` verification example in the next section. Existing legacy
assignments are preserved until an administrator explicitly removes them.

No assignment must mean no module access. Do not use a shared employee account and do not grant
`Admin` merely to make a test pass.

## 4. Verify roles as the actual employee

Copy `Test-HubUserAccess.ps1` to a local temporary folder on the employee workstation. Have the
employee sign in normally, open PowerShell, and first confirm:

**Employee PowerShell:**

```powershell
whoami
```

It must show the employee's own `SON4L\firstname.lastname`, not `NT AUTHORITY\SYSTEM`. Then run a
test whose expectations exactly match the assignments made in the Admin Console. Administrator
example:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& 'C:\Temp\Test-HubUserAccess.ps1' `
  -ExpectedAccountName "SON4L\jordan.greer" `
  -ExpectedPortalRole Admin `
  -ExpectedPortalModuleRoles @{ engineering = 'Admin'; estimating = 'Admin' } `
  -ExpectedProjectTrackerAccess Access `
  -ExpectedProjectTrackerGroups Administrators `
  -ExpectedEngineeringRole Admin `
  -ExpectedEstimatingRole Admin
```

Unassigned-user/default-deny example:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& 'C:\Temp\Test-HubUserAccess.ps1' `
  -ExpectedAccountName "SON4L\firstname.lastname" `
  -ExpectedPortalRole Viewer `
  -ExpectedPortalModuleRoles @{ engineering = 'NoAccess'; estimating = 'NoAccess' } `
  -ExpectedProjectTrackerAccess NoAccess `
  -ExpectedEngineeringRole NoAccess `
  -ExpectedEstimatingRole NoAccess
```

Expected final line: `HUB_USER_ACCESS_VERIFIED`. A mismatch is a failed role check: correct the
Admin Console assignment or the stated expectation, then rerun it. Do not bypass the test.

## 5. Install the employee desktop shortcut

Only start this section after the default-deny release is healthy and the intended employee's role
test passes. Use `Install-EmployeeHubShortcut.ps1` with `son-aero.ico`; it creates an all-users
desktop URL shortcut and can be rerun safely.

### One-computer local test

Put both files in `C:\Temp\SonAero`, open PowerShell as Administrator, and run:

```powershell
& 'C:\Temp\SonAero\Install-EmployeeHubShortcut.ps1' `
  -HubUri 'http://SON-IIS2:5140' `
  -IconSource 'C:\Temp\SonAero\son-aero.ico' `
  -WhatIf

& 'C:\Temp\SonAero\Install-EmployeeHubShortcut.ps1' `
  -HubUri 'http://SON-IIS2:5140' `
  -IconSource 'C:\Temp\SonAero\son-aero.ico' `
  -Confirm:$false
```

Expected status is `INSTALLED_OR_UPDATED` on the first run and `ALREADY_CURRENT` when repeated.

### N-central deployment

Upload both files to `C:\SonAero\EmployeeShortcut` on each approved employee computer.

**N-central CMD on an employee computer — preview then apply:**

```bat
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\SonAero\EmployeeShortcut\Install-EmployeeHubShortcut.ps1" -HubUri "http://SON-IIS2:5140" -IconSource "C:\SonAero\EmployeeShortcut\son-aero.ico" -WhatIf
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\SonAero\EmployeeShortcut\Install-EmployeeHubShortcut.ps1" -HubUri "http://SON-IIS2:5140" -IconSource "C:\SonAero\EmployeeShortcut\son-aero.ico" -Confirm:$false
```

### Group Policy deployment

Place the script and icon in a read-only domain package location, then configure a **Computer
Configuration** startup script to run as Local System:

```bat
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "\\SON4L.LOCAL\SYSVOL\SON4L.LOCAL\scripts\SonAero\Install-EmployeeHubShortcut.ps1" -HubUri "http://SON-IIS2:5140" -IconSource "\\SON4L.LOCAL\SYSVOL\SON4L.LOCAL\scripts\SonAero\son-aero.ico" -Confirm:$false
```

Scope the GPO to the approved workstation security group, test on one computer, then expand the
scope. If your domain uses a different SYSVOL package path, replace both paths with that approved
path.

After HTTPS is fully configured and tested, rerun the same shortcut script with
`-HubUri "https://SON-IIS2:5140"`; it updates the existing shortcut in place.

## 6. HTTPS: readiness check only for now

Do not create a self-signed production certificate and do not change IIS bindings yet. First obtain
a certificate from the trusted internal CA that:

- is installed in `Cert:\LocalMachine\My` on `SON-IIS2`;
- has a private key and Server Authentication EKU;
- has SANs for `SON-IIS2` and `SON-IIS2.SON4L.LOCAL`;
- chains successfully to a CA trusted by employee workstations.

Upload `Test-HubHttpsReadiness.ps1` to `C:\SonAero\Test-HubHttpsReadiness.ps1`. List candidate
certificates without changing anything:

**N-central CMD on SON-IIS2:**

```bat
powershell.exe -NoProfile -Command "Get-ChildItem Cert:\LocalMachine\My | Select-Object Subject,Thumbprint,NotBefore,NotAfter,HasPrivateKey | Format-Table -AutoSize"
```

After the CA certificate is present, run the read-only audit with its real thumbprint:

```bat
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\SonAero\Test-HubHttpsReadiness.ps1" -CertificateThumbprint "PASTE_REAL_THUMBPRINT_HERE"
```

Required server status: `HTTPS_SERVER_PREREQUISITES_READY_WORKSTATION_TRUST_PENDING`. The script
never changes bindings. After that result, verify the certificate chain on a representative domain
workstation. Only then should a separate maintenance-window change rebuild the Portal URLs for
HTTPS, add the four trusted IIS bindings, verify Windows Authentication on all four `/api/me`
endpoints, and retarget the shortcut. Keep HTTP working until HTTPS verification is complete.

## 7. Backups: readiness check only until two decisions are supplied

Before backup jobs can be configured, obtain:

1. an approved **off-server UNC folder** (not a folder on `SON-SQL2`); and
2. the required recovery point objective (RPO): nightly, every four hours, or point-in-time with
   frequent transaction-log backups.

Upload `Test-HubBackupReadiness.ps1` to `C:\SonAero\Test-HubBackupReadiness.ps1` on `SON-SQL2`.
Replace the example host and path below with the approved destination; do not run the example
unchanged.

**N-central CMD on SON-SQL2 — read-only:**

```bat
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\SonAero\Test-HubBackupReadiness.ps1" -OffServerBackupRoot "\\BACKUP01\SonAeroHub" -ExpectedBackupHost "BACKUP01" -RecoveryPointObjective Daily
```

Choose exactly one RPO value: `Daily`, `FourHours`, or `PointInTime15Minutes`. The example uses
`Daily`; replace it if the approved requirement is different.

The audit reports the SQL service network identity, SQL Agent state, database health, drawing size,
remote capacity, and direct NTFS/share permissions. It uses a read-only CIM session to the backup
host, so remote management/query access is required. It does not create a backup or grant
permissions. Required status is `BACKUP_PREREQUISITES_READY`; the reported SQL network identity
must have direct NTFS Write and SMB Change/Full access to the approved UNC folder before scheduled
jobs are built.

The operational backup set must include:

- the `ProjectTracker` database, which also contains centralized user/module role assignments;
- the `EngineeringHub` database;
- `C:\SonAero\Data\EngineeringDrawings` as a matched/quiesced recovery set with EngineeringHub;
- production configuration and the eventual TLS certificate/binding recovery information.

Backups are not operational until jobs run on schedule, checksum verification succeeds, multiple
off-server restore points exist, and a restore drill succeeds on a non-production SQL instance.

> **Current estimating limitation:** Estimating quote drafts/history are stored in each browser's
> local storage (`sonaero-estimating-quotes:v1`). SQL/server/file backups do not protect those
> quotes. Treat them as workstation-local data until quote persistence is moved to the server; if
> users need durable quotes now, use the application's available export process and retain the
> exports in an approved backed-up location.

## Completion gates

- Default-deny release is deployed and all four health checks return 200.
- Warm-start script ends with `WARM_START_CONFIGURED_AND_HEALTHY`.
- Each intended user ends with `HUB_USER_ACCESS_VERIFIED`; an unassigned test user is denied.
- Shortcut is piloted, then deployed only to approved workstations.
- HTTPS stays unchanged until server readiness passes, workstation trust is verified, and a
  maintenance window is approved.
- Backup jobs stay unconfigured until the approved off-server UNC and RPO are supplied.
