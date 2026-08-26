# SON-AERO Hub production rollout

This runbook is for the current two-server environment:

| Server | Purpose |
|---|---|
| `SON-IIS2` | IIS and the five Hub applications |
| `SON-SQL2` | `ProjectTracker` and `EngineeringHub` SQL databases plus Engineering drawings |

Current HTTP endpoints are Portal `:5140`, Project Tracker `:5135`, Engineering `:5150`,
Estimating `:5160`, and Quality Assurance `:5170`. Complete the sections in order. In particular, deploy the default-deny release
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
builds a new immutable release folder, preserves the five current Production settings files,
switches all five IIS paths together, checks health, and restores the previous paths if the new
release is not healthy. Candidate applications are cold-started and health-gated one at a time,
including the separate Project Tracker gateway, so first-run SQL migrations and permission seeding
cannot overlap across modules.

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
  -ProjectTrackerUrl '/project-tracker-api'
if ($LASTEXITCODE -ne 0) { throw 'Hub publish failed; IIS was not changed.' }

& "$repo\deployment\Configure-PortalProjectTrackerGateway.ps1" -WhatIf
if ($LASTEXITCODE -ne 0) { throw 'Gateway preview failed; IIS was not changed.' }

& "$repo\deployment\Configure-PortalProjectTrackerGateway.ps1" -Confirm:$false
if ($LASTEXITCODE -ne 0) { throw 'Gateway configuration failed; do not deploy the release.' }

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "$repo\deployment\Deploy-HubRelease.ps1" `
  -PackageRoot $packageRoot `
  -ReleaseId $releaseId `
  -WhatIf
if ($LASTEXITCODE -ne 0) { throw 'Release preview failed; IIS was not changed.' }

& "$repo\deployment\Deploy-HubRelease.ps1" `
  -PackageRoot $packageRoot `
  -ReleaseId $releaseId `
  -Confirm:$false
if ($LASTEXITCODE -ne 0) { throw 'Release deployment failed; review the rollback result.' }
```

The preview must end with `WHATIF_READY`. The apply run must end with
`HUB_RELEASE_DEPLOYED_AND_HEALTHY`. Keep the previous immutable release and the pre-deployment SQL
backup. Do not use `xcopy`, `Copy-Item -Force`, or a manual DLL replacement against a running IIS
application pool. If apply rolls back, keep the retained failed release and use its reported final
endpoint result plus the Windows Application log before attempting a new immutable release ID.

After cutover, verify all five health endpoints from a domain workstation:

```text
http://SON-IIS2:5135/api/health
http://SON-IIS2:5140/api/health
http://SON-IIS2:5150/api/health
http://SON-IIS2:5160/api/health
http://SON-IIS2:5170/api/health
```

All five must return HTTP 200 before continuing.

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
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& 'C:\SonAero\Configure-IisWarmStart.ps1' -Scheme http -Confirm:$false"
```

Expected final line: `WARM_START_CONFIGURED_AND_HEALTHY`, with HTTP 200 for all five sites and the
Project Tracker gateway. The
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

- Shared users, groups, and permissions: `http://SON-IIS2:5140/#/admin/access`

Enter every account in canonical form, for example `SON4L\jordan.greer`. For each employee:

1. Assign the employee to one or more shared groups.
2. Expand each group and review its permissions under Project Tracker, Engineering, Estimating, and Quality Assurance.
3. Add or remove granular module permissions as required for that group.
4. Save, then reopen the group to confirm the stored permissions and user assignments.

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
  -ExpectedPortalModuleRoles @{ engineering = 'Admin'; estimating = 'Admin'; 'quality-assurance' = 'Admin' } `
  -ExpectedProjectTrackerAccess Access `
  -ExpectedProjectTrackerGroups Administrators `
  -ExpectedEngineeringRole Admin `
  -ExpectedEstimatingRole Admin `
  -ExpectedQualityAssuranceRole Admin
```

Unassigned-user/default-deny example:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& 'C:\Temp\Test-HubUserAccess.ps1' `
  -ExpectedAccountName "SON4L\firstname.lastname" `
  -ExpectedPortalRole Viewer `
  -ExpectedPortalModuleRoles @{ engineering = 'NoAccess'; estimating = 'NoAccess'; 'quality-assurance' = 'NoAccess' } `
  -ExpectedProjectTrackerAccess NoAccess `
  -ExpectedEngineeringRole NoAccess `
  -ExpectedEstimatingRole NoAccess `
  -ExpectedQualityAssuranceRole NoAccess
```

Expected final line: `HUB_USER_ACCESS_VERIFIED`. A mismatch is a failed role check: correct the
Admin Console assignment or the stated expectation, then rerun it. Do not bypass the test.

## 5. Install the employee desktop shortcut

Only start this section after the default-deny release is healthy and the intended employee's role
test passes. The employee ZIP creates an all-users desktop shortcut and can be rerun safely. It
checks Hub health and the signed-in employee's Portal identity before requesting elevation; only
the shortcut write runs elevated.

### Double-click employee ZIP

Build the ZIP from a trusted repository checkout:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  .\deployment\New-EmployeeHubInstallerPackage.ps1 `
  -HubUri 'http://SON-IIS2:5140'
```

The explicit HTTP origin is required for this retained baseline runbook because the package
builder's default is now the permanent production HTTPS hostname.

Distribute `deployment\artifacts\SonAero-Hub-Employee-Installer.zip` only to approved pilot
computers. On each employee computer:

1. Sign into Windows as the intended employee.
2. Right-click the ZIP and choose **Extract All**. Running from inside the compressed-folder view
   is not supported.
3. Open the extracted folder and double-click `Install Son-Aero Hub.cmd`.
4. Approve the UAC prompt or supply approved workstation-administrator credentials.
5. Require the final message `SONAERO_HUB_EMPLOYEE_INSTALL_COMPLETE`.

The ZIP does not assign roles and does not change server settings. The administrator must still
assign roles in the Admin Console first. The generic identity check also does not replace the exact
`Test-HubUserAccess.ps1` role verification required for initial pilots.

### Manual one-computer fallback

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
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& 'C:\SonAero\EmployeeShortcut\Install-EmployeeHubShortcut.ps1' -HubUri 'http://SON-IIS2:5140' -IconSource 'C:\SonAero\EmployeeShortcut\son-aero.ico' -Confirm:$false"
```

### Group Policy deployment

Place the script and icon in a read-only domain package location, then configure a **Computer
Configuration** startup script to run as Local System:

```bat
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '\\SON4L.LOCAL\SYSVOL\SON4L.LOCAL\scripts\SonAero\Install-EmployeeHubShortcut.ps1' -HubUri 'http://SON-IIS2:5140' -IconSource '\\SON4L.LOCAL\SYSVOL\SON4L.LOCAL\scripts\SonAero\son-aero.ico' -Confirm:$false"
```

Scope the GPO to the approved workstation security group, test on one computer, then expand the
scope. If your domain uses a different SYSVOL package path, replace both paths with that approved
path.

After HTTPS is fully configured and tested, rerun the same shortcut script with
`-HubUri "FINAL_APPROVED_PORTAL_HTTPS_URI"`; it updates the existing shortcut in place. Do not
assume the current HTTP port `5140` is also an HTTPS endpoint.

## 6. HTTPS and Web Push prerequisites

Do not create a self-signed production certificate and do not change IIS bindings yet. First obtain
a certificate from the trusted internal CA that:

- is installed in `Cert:\LocalMachine\My` on `SON-IIS2`;
- has a private key and Server Authentication EKU;
- has SANs for `SON-IIS2` and `SON-IIS2.SON4L.LOCAL`;
- chains successfully to a CA trusted by employee workstations.

If the final binding plan uses application-specific DNS names, those names must also be present in
the certificate SAN list before the certificate is issued.

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

Production must omit `-PilotRootThumbprint`. That parameter is reserved for the isolated
two-workstation private-CA pilot, whose root is pinned explicitly because it has no revocation
service. Production readiness must continue to use the default online revocation check and a
company-managed certificate chain.

Required server status: `HTTPS_SERVER_PREREQUISITES_READY_WORKSTATION_TRUST_PENDING`. The script
never changes bindings. After that result, verify the certificate chain on a representative domain
workstation.

Web Push and service workers require a browser secure context. Consequently, desktop mention
notifications must remain disabled while employees use the current HTTP endpoints. A successful
application deployment alone cannot make browser push work over HTTP.

The proposed low-risk binding plan is to keep the proven HTTP bindings and add separate HTTPS
bindings during an approved maintenance window:

| Application | Existing HTTP | Proposed HTTPS |
|---|---:|---:|
| Project Tracker | `5135` | `6135` |
| Portal | `5140` | `6140` |
| Engineering | `5150` | `6150` |
| Estimating | `5160` | `6160` |
| Quality Assurance | `5170` | `6170` |

This repository deliberately does not automate certificate selection or binding creation. Those
operations affect machine-level TLS state and require the approved certificate thumbprint, port
ownership, workstation trust result, and a maintenance window. The binding change must be
idempotent, must add rather than replace the HTTP bindings, and must open only the five approved
HTTPS firewall ports. Use `SON-IIS2` as the initial URL host because it is in the required SAN and
does not introduce a new Windows-authentication SPN alias.

After the five direct HTTPS `/api/health` and `/api/me` checks succeed from a domain workstation:

1. Add `https://SON-IIS2:6140` to Project Tracker's production `Cors:HubOrigins` without removing
   the HTTP origin during the pilot.
2. Change the four Portal application URLs to `https://SON-IIS2:6135`, `:6150`, `:6160`, and `:6170`,
   republish the Portal, and deploy through the immutable release script.
3. Confirm the same-origin Project Tracker gateway at
   `https://SON-IIS2:6140/project-tracker-api/api/health`.
4. Run warm start with `-Scheme https`. The script's HTTPS defaults are the proposed `61xx` ports;
   override its five `*HttpsPort` parameters if the approved binding plan differs.
5. Run `Test-HubUserAccess.ps1 -Scheme https` as a real employee. It uses the same HTTPS defaults
   and accepts the same port overrides.
6. Retarget the employee shortcut to `https://SON-IIS2:6140` only after all prior checks pass.

Keep the HTTP bindings working until the HTTPS pilot is signed off. Do not repurpose ports
`5135`-`5170` as HTTPS and do not remove a working binding merely to test TLS.

### Configure Project Tracker Web Push after HTTPS is trusted

`deployment/templates/project-tracker.appsettings.Production.json` intentionally contains only a
disabled, keyless Web Push block. Never put the VAPID private key in that file, Git, chat, a ticket,
a screenshot, or a command-line argument. Generate the P-256 VAPID pair once and reuse it across
normal application releases. Changing the pair invalidates every existing browser subscription.

Copy `Configure-ProjectTrackerWebPush.ps1` to `C:\SonAero` on `SON-IIS2`. The preferred first-time
path creates the key pair in memory, writes the private key only to IIS configuration, and displays
only the public key and its SHA-256 fingerprint:

```powershell
& 'C:\SonAero\Configure-ProjectTrackerWebPush.ps1' `
  -GenerateKeys `
  -VapidSubject 'mailto:APP_OWNER@SONAERO.COM' `
  -VerificationUri 'https://SON-IIS2:6135/api/push/public-key' `
  -WhatIf

& 'C:\SonAero\Configure-ProjectTrackerWebPush.ps1' `
  -GenerateKeys `
  -VapidSubject 'mailto:APP_OWNER@SONAERO.COM' `
  -VerificationUri 'https://SON-IIS2:6135/api/push/public-key' `
  -Confirm:$false
```

Replace the subject with an approved real application-owner address. `-WhatIf` validates the
operation but deliberately does not generate a throwaway pair; the apply run generates it. The
preview must end with
`WHATIF_READY`; the apply run must end with
`PROJECT_TRACKER_WEB_PUSH_CONFIGURED_AND_HEALTHY`. The script writes the four `WebPush__*` values
only to Project Tracker's IIS `aspNetCore/environmentVariables` location in
`applicationHost.config`, restarts only that app pool, checks the HTTPS public-key endpoint, and
restores the prior values if verification fails. IIS configuration backups now contain the private
key and must be protected as secrets; server administrators can read it. Record the displayed
public key and fingerprint in the operations record. The private key exists only in the protected
IIS configuration and its protected backups.

If an approved VAPID pair already exists, omit `-GenerateKeys`, enter the private key via
`Read-Host -AsSecureString`, and pass `-VapidPublicKey`, `-VapidPrivateKey`, and `-VapidSubject`.
Never paste the private key directly into the command line.

To turn push off without deleting browser notification records, run:

```powershell
& 'C:\SonAero\Configure-ProjectTrackerWebPush.ps1' `
  -Disable `
  -VerificationUri 'https://SON-IIS2:6135/api/push/public-key' `
  -Confirm:$false
```

Finally, sign in as one pilot employee at the HTTPS Project Tracker origin and explicitly enable
desktop notifications in the application's notification control. Browser permission is per user,
browser profile, workstation, and origin; an administrator cannot grant it centrally through the
application. Test a real `@mention` while the browser is closed, click the notification, and verify
that it opens the correct same-origin project page. Also confirm SON-IIS2 can reach the browser
push-service endpoints and that Edge/Windows push notifications are not blocked by firewall or
Group Policy. Do not enable company-wide rollout until both an online and closed-browser mention
test succeed.

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

- the `ProjectTracker` database, which also contains centralized users, groups, and module permissions;
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

- Default-deny release is deployed and all five health checks return 200.
- Warm-start script ends with `WARM_START_CONFIGURED_AND_HEALTHY`.
- Each intended user ends with `HUB_USER_ACCESS_VERIFIED`; an unassigned test user is denied.
- Shortcut is piloted, then deployed only to approved workstations.
- HTTPS stays unchanged until server readiness passes, workstation trust is verified, and a
  maintenance window is approved.
- Backup jobs stay unconfigured until the approved off-server UNC and RPO are supplied.
