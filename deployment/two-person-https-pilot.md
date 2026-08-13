# Son-Aero Hub two-person HTTPS pilot

This runbook is only for the named Jordan/Josh pilot. It deliberately keeps every working HTTP
binding, does not redirect HTTP, does not enable HSTS, and restricts the new HTTPS firewall rule to
the two pilot workstations plus SON-IIS2's own address. It is not the later company-wide PKI plan.

The pilot root has no managed CA database, CRL, or OCSP service. Trust it only on SON-IIS2 and the
two named pilot computers. Keep its encrypted private key offline and never put any PFX, password,
or private key in Git, chat, email, a screenshot, N-central, or a file share.

## Values to record before starting

Run `hostname`, `whoami`, and `ipconfig` locally on each pilot computer. Record the exact active
IPv4 address, not a VPN or virtual-adapter address.

| Value | Jordan pilot | Josh pilot |
|---|---|---|
| Windows account | `SON4L\jordan.greer` | `SON4L\JoshGreer` |
| Computer name | fill in | fill in |
| Active IPv4 | fill in | fill in |

Also record the secured admin workstation's `hostname`. The admin workstation must not be
`SON-IIS2` or `SON-SQL2`.

## 1. Generate the pilot CA bundle on the admin workstation

Use an elevated Windows PowerShell window on the secured admin workstation. Use a new empty local
NTFS/ReFS folder outside the repository. Run the preview first:

```powershell
$repo = 'C:\Users\USER\projects\non project folder\Project Tracker'
$adminComputer = $env:COMPUTERNAME
$pkiRoot = 'C:\SonAeroPilotPki'

& "$repo\deployment\New-HubPilotPkiBundle.ps1" `
  -ExpectedAdminWorkstationName $adminComputer `
  -OutputDirectory $pkiRoot `
  -WhatIf
```

Require `WHATIF_READY_PILOT_PKI_BUNDLE`, then apply:

```powershell
& "$repo\deployment\New-HubPilotPkiBundle.ps1" `
  -ExpectedAdminWorkstationName $adminComputer `
  -OutputDirectory $pkiRoot `
  -Confirm:$false
```

Enter two different strong passwords when prompted: one for the offline root recovery PFX and one
for the leaf transport PFX. Require `PILOT_PKI_BUNDLE_CREATED` and record the displayed root/leaf
thumbprints and SHA-256 values.

The output is physically separated:

- `OFFLINE-ROOT-PRIVATE-DO-NOT-COPY`: make two encrypted offline backups, verify their hashes, then
  remove this directory from the daily workstation. Never copy it to either server.
- `SERVER-PILOT-HANDOFF`: the only directory copied to SON-IIS2.
- `WORKSTATION-PILOT-TRUST`: public root material used to build the two locked workstation ZIPs.

## 2. Import only the server handoff on SON-IIS2

Copy `SERVER-PILOT-HANDOFF` to `C:\SonAero\pilot-pki\SERVER-PILOT-HANDOFF` on SON-IIS2. Copy the
current `Install-HubPilotServerCertificate.ps1` there as well. In elevated Windows PowerShell on
the SON-IIS2 Remote Desktop, preview and apply:

```powershell
$handoff = 'C:\SonAero\pilot-pki\SERVER-PILOT-HANDOFF'
$script = 'C:\SonAero\Install-HubPilotServerCertificate.ps1'

& $script -BundleDirectory $handoff -WhatIf
& $script -BundleDirectory $handoff -Confirm:$false
```

The preview must return `WHATIF_READY_PILOT_SERVER_CERTIFICATE_IMPORT`; apply must return
`PILOT_SERVER_CERTIFICATES_IMPORTED` or the idempotent `...ALREADY_INSTALLED` status. Enter the
leaf transport password only at the secure prompt. Record `RootThumbprintSha1` and
`LeafThumbprintSha1` from the output.

After the bindings are healthy, remove the server-local handoff directory containing the
transport PFX. SON-IIS2 retains the non-exportable leaf private key and the public root; it never
receives the root private key.

## 3. Add pilot HTTPS bindings and the restricted firewall rule

First run the read-only HTTPS readiness audit with the explicit pilot root pin. Private pilot mode
intentionally performs no online revocation lookup because this isolated CA publishes no
CDP/CRL/OCSP endpoint. The audit still requires a successful Windows chain build containing
exactly the selected leaf and installed pilot root, plus every hostname, validity, EKU, key, and
algorithm check. Omitting `-PilotRootThumbprint` keeps production mode's online revocation check.

```powershell
$repo = 'C:\SonAero\src\SonAeroInternalHub'
$leafThumbprint = 'PASTE_LEAF_SHA1_THUMBPRINT'
$rootThumbprint = 'PASTE_ROOT_SHA1_THUMBPRINT'

& "$repo\deployment\Test-HubHttpsReadiness.ps1" `
  -CertificateThumbprint $leafThumbprint `
  -PilotRootThumbprint $rootThumbprint
```

Require `HTTPS_SERVER_PREREQUISITES_READY_WORKSTATION_TRUST_PENDING` before continuing.

Then substitute the two pilot IPv4 addresses. Keep SON-IIS2's own address in the list so its local
health verification is not blocked.

```powershell
$leafThumbprint = 'PASTE_LEAF_SHA1_THUMBPRINT'
$rootThumbprint = 'PASTE_ROOT_SHA1_THUMBPRINT'
$pilotAddresses = @(
  'PASTE_JORDAN_IPV4',
  'PASTE_JOSH_IPV4',
  '10.50.10.244'
)
$httpsScript = "$repo\deployment\Configure-HubHttpsPilot.ps1"

& $httpsScript `
  -CertificateThumbprint $leafThumbprint `
  -PilotRootThumbprint $rootThumbprint `
  -PilotRemoteAddress $pilotAddresses `
  -WhatIf
```

Require `WHATIF_READY`, then apply the same command with `-Confirm:$false` instead of `-WhatIf`.
Require `HTTPS_PILOT_CONFIGURED_AND_DUAL_SCHEME_HEALTHY`.

The script adds HTTPS 6135/6140/6150/6160, preserves HTTP 5135/5140/5150/5160, refuses broad
firewall aliases, verifies the exact leaf-to-root chain, and automatically restores the prior IIS
and firewall state if any check fails.

The retained transaction state must remain a `.json` file directly under
`C:\ProgramData\SonAero\deployment-state` (the default is `https-pilot.json`). The script refuses
to trust the state if any existing path ancestor is a reparse point, or if the state directory/file
inherits permissions, has an unexpected owner, or grants access to anyone other than local SYSTEM
and BUILTIN\Administrators.
Every update uses a uniquely named protected sibling temporary file and an atomic replacement. Do
not manually loosen, relocate, or copy this state merely to bypass a protection check.

### One-time protection migration for the deployed pilot state

Before any future pilot rollback, retirement, or permanent-binding transaction, pull the reviewed
release and run this exact-path migration in elevated Windows PowerShell 5.1 on SON-IIS2:

```powershell
$repo = 'C:\SonAero\src\SonAeroInternalHub'
& "$repo\deployment\Configure-HubHttpsPilot.ps1" -MigrateLegacyStateProtection -WhatIf
& "$repo\deployment\Configure-HubHttpsPilot.ps1" -MigrateLegacyStateProtection -Confirm:$false
```

The preview must end with `WHATIF_READY_HTTPS_PILOT_STATE_PROTECTION_MIGRATION`. The apply must end
with `HTTPS_PILOT_STATE_PROTECTION_MIGRATED`; an already-secured rerun ends with
`HTTPS_PILOT_STATE_PROTECTION_ALREADY_CURRENT`. This deliberately narrow mode accepts only the
exact `C:\ProgramData\SonAero\deployment-state\https-pilot.json` v1 `Applied` state for SON-IIS2
with an empty pre-pilot 61xx baseline. It recognizes either the authentic four-site pilot generation
(Project Tracker, Portal, Engineering, and Estimating) or the later exact five-site generation. For
the historical generation it requires the recorded four HTTP/four HTTPS topology, all five current
HTTP bindings, absent QA 6170, and the exact four-port firewall. It verifies the saved leaf/root,
remote addresses, generation bindings, and health before changing only state ACLs. It never changes
JSON, IIS, or Windows Firewall.
If it reports path, schema, certificate, binding, firewall, address, or health drift, stop and
preserve the file for administrator review; never manually grant the JSON rollback authority.

For authentic four-site state, next run the separate QA extension transaction from the permanent
HTTPS runbook. It owns only QA 6170 and the firewall's fifth port in
`https-pilot-quality-extension.json`. On retirement, roll back the QA extension first; the original
pilot rollback refuses while that extension remains applied.

## 4. Publish and deploy an HTTPS-aware immutable release

After these changes have been reviewed, committed, and pushed, update the clean server checkout in
elevated PowerShell on SON-IIS2:

```powershell
$repo = 'C:\SonAero\src\SonAeroInternalHub'
git -C $repo fetch --prune origin
if ($LASTEXITCODE -ne 0) { throw 'git fetch failed; IIS was not changed.' }
git -C $repo pull --ff-only origin main
if ($LASTEXITCODE -ne 0) { throw 'git pull failed; do not force-reset the server checkout.' }
$dirty = @(git -C $repo status --porcelain)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw "Server checkout is dirty: $($dirty -join '; ')" }

$releaseId = (git -C $repo rev-parse --short HEAD).Trim()
$packageRoot = "C:\SonAero\staging\hub-$releaseId-https-pilot"
if (Test-Path -LiteralPath $packageRoot) { throw "Staging path already exists: $packageRoot" }

& "$repo\deployment\Publish-Hub.ps1" `
  -OutputRoot $packageRoot `
  -ProjectTrackerUrl '/project-tracker-api'
if ($LASTEXITCODE -ne 0) { throw 'HTTPS-aware publish failed; IIS was not changed.' }

& "$repo\deployment\Deploy-HubRelease.ps1" `
  -PackageRoot $packageRoot -ReleaseId "$releaseId-https-pilot" -WhatIf
if ($LASTEXITCODE -ne 0) { throw 'Release preview failed; IIS was not changed.' }

& "$repo\deployment\Deploy-HubRelease.ps1" `
  -PackageRoot $packageRoot -ReleaseId "$releaseId-https-pilot" -Confirm:$false
if ($LASTEXITCODE -ne 0) { throw 'Release deploy failed; review its rollback result.' }
```

Require `WHATIF_READY`, then `HUB_RELEASE_DEPLOYED_AND_HEALTHY`.

## 5. Apply HTTPS module URLs and dual-origin CORS

Run the script from the clean server checkout on SON-IIS2 so the reviewed version is used:

```powershell
$repo = 'C:\SonAero\src\SonAeroInternalHub'
$corsAuthenticationScript = "$repo\deployment\Configure-ProjectTrackerCorsAuthentication.ps1"
& $corsAuthenticationScript -WhatIf
& $corsAuthenticationScript -Confirm:$false

$configScript = "$repo\deployment\Configure-HubHttpsApplicationConfig.ps1"
& $configScript -Topology Pilot -WhatIf
```

The CORS-authentication preview must end with
`WHATIF_READY_PROJECT_TRACKER_CORS_AUTHENTICATION`; its apply must end with
`PROJECT_TRACKER_CORS_AUTHENTICATION_CONFIGURED_AND_VERIFIED` (or the idempotent `ALREADY`
variant). It enables Anonymous and Windows Authentication together only on the direct Project
Tracker site so browser preflight can reach ASP.NET Core authorization. The same-origin gateway
remains Windows-only. This setting is shared by HTTP, 61xx, and permanent HTTPS and is not reversed
during pilot or production binding rollback.

Require `WHATIF_READY`. Only then apply it:

```powershell
& $configScript -Topology Pilot -Confirm:$false
```

Require
`HTTPS_APPLICATION_CONFIG_APPLIED_AND_DUAL_SCHEME_GATEWAY_HEALTHY`. The transaction backs up the
two active production JSON files under a restricted ACL, uses HTTPS-first dual CORS, restarts only
the three affected pools, checks both schemes plus both gateway paths, and restores the originals
on failure. Its state file is restricted to
`C:\ProgramData\SonAero\deployment-state\https-application-config.json`; do not relocate it to a
web root or another application directory.

This release requires the application-config state file itself to have protected explicit
SYSTEM/Administrators ACLs before it is trusted. A `https-application-config.json` created by the
pre-hardening script is historical evidence only and cannot authorize `-Topology Pilot -Rollback`.
Do not loosen that check or copy the JSON to bypass it. The permanent-hostname runbook does not rely
on this legacy record: its Production transaction snapshots the currently active Pilot configuration
into a new protected rollback state.

## 6. Configure HTTPS warm start

Run the existing warm-start script on SON-IIS2 with the new scheme:

```powershell
& 'C:\SonAero\Configure-IisWarmStart.ps1' -Scheme https -WhatIf
& 'C:\SonAero\Configure-IisWarmStart.ps1' -Scheme https -Confirm:$false
```

Require `WHATIF_READY: no IIS features, settings, files, or scheduled tasks were changed.`, then
`WARM_START_CONFIGURED_AND_HEALTHY`.

## 7. Build one locked ZIP per pilot computer

On the admin workstation, retain only the public `WORKSTATION-PILOT-TRUST` directory. Use the exact
computer names recorded earlier. Preview and then build each package:

```powershell
$repo = 'C:\Users\USER\projects\non project folder\Project Tracker'
$trustBundle = 'C:\SonAeroPilotPki\WORKSTATION-PILOT-TRUST'

& "$repo\deployment\New-HubPilotWorkstationInstallerPackage.ps1" `
  -TrustBundleDirectory $trustBundle `
  -ExpectedComputerName 'PASTE_JORDAN_COMPUTER' `
  -ExpectedAccountName 'SON4L\jordan.greer' `
  -WhatIf

& "$repo\deployment\New-HubPilotWorkstationInstallerPackage.ps1" `
  -TrustBundleDirectory $trustBundle `
  -ExpectedComputerName 'PASTE_JORDAN_COMPUTER' `
  -ExpectedAccountName 'SON4L\jordan.greer' `
  -Confirm:$false

& "$repo\deployment\New-HubPilotWorkstationInstallerPackage.ps1" `
  -TrustBundleDirectory $trustBundle `
  -ExpectedComputerName 'PASTE_JOSH_COMPUTER' `
  -ExpectedAccountName 'SON4L\JoshGreer' `
  -Confirm:$false
```

Require `PILOT_WORKSTATION_PACKAGE_READY` and record each ZIP's SHA-256. Give each employee only
the ZIP locked to that employee/computer. Each employee signs in normally, chooses **Extract All**,
and double-clicks `Install Son-Aero Hub Pilot.cmd`. Two UAC approvals are intentional: trust is
installed first, then HTTPS health/identity is verified as the employee before the shortcut is
created. Require `SONAERO_HUB_PILOT_INSTALL_COMPLETE`.

## 8. Verify each real user's HTTPS access

On each employee computer, in the employee's normal PowerShell session, run the existing access
test with `-Scheme https` and expectations matching the Admin Hub assignment. Require
`HUB_USER_ACCESS_VERIFIED`. Then manually open all five HTTPS endpoints through the Hub and confirm
Jordan and Josh see only their assigned modules and data.

## 9. Enable and verify Web Push only after HTTPS trust succeeds

Run the existing `Configure-ProjectTrackerWebPush.ps1` on SON-IIS2 with
`-VerificationUri 'https://SON-IIS2:6135/api/push/public-key'`. Use `-WhatIf` first, then
`-GenerateKeys -VapidSubject 'mailto:APP_OWNER@SONAERO.COM' -Confirm:$false`, replacing the subject
with the real application-owner address. Never paste or record the private key. Require
`PROJECT_TRACKER_WEB_PUSH_CONFIGURED_AND_HEALTHY`.

On each pilot computer, open the HTTPS Project Tracker. Browser permission is scoped to the new
HTTPS origin, so each employee must approve the browser's one-time notification prompt. Test an
online mention and a closed-browser mention in both directions, click each desktop notification,
and confirm it opens the correct project. Do not call Web Push ready until all four tests pass.

## Pilot rollback

Run rollback in this order from elevated PowerShell:

1. Disable Web Push with `Configure-ProjectTrackerWebPush.ps1 -Disable` while HTTPS still works.
2. For a Pilot application-config transaction created by this hardened release, run
   `Configure-HubHttpsApplicationConfig.ps1 -Topology Pilot -Rollback -WhatIf`, then repeat with
   `-Topology Pilot -Rollback -Confirm:$false`. Stop and escalate if the state predates this
   hardening; that legacy file is not trusted rollback authority.
3. Retarget both shortcuts to `http://SON-IIS2:5140` with `Install-EmployeeHubShortcut.ps1`.
4. If `https-pilot-quality-extension.json` exists, run
   `Configure-HubHttpsPilotQualityExtension.ps1 -Rollback -WhatIf`, then repeat with
   `-Rollback -Confirm:$false` and require its four-site healthy marker.
5. On SON-IIS2: `Configure-HubHttpsPilot.ps1 -Rollback -WhatIf`, then repeat with
   `-Confirm:$false`.
6. On each named pilot workstation, run `Set-HubPilotWorkstationTrust.ps1` locally with its trust
   bundle, exact `-ExpectedComputerName`, `-Operation Remove`, preview first, then apply.
7. On SON-IIS2, run `Install-HubPilotServerCertificate.ps1 -Operation RemoveAll`, preview first,
   then apply. It refuses removal while the leaf is still bound.

If the QA-extension state file exists, the original pilot rollback securely validates its protected
transaction identity and permits retirement only after status is exactly `RolledBack` or
`AutomaticallyRolledBack`. An Applied, incomplete, malformed, or unprotected extension state is a
hard stop even when the live bindings happen to look like the older four-site topology.

The existing HTTP bindings remain the safety path throughout. A rollback must end with all five
HTTP health endpoints returning 200.

### Recover an interrupted or failed HTTPS binding transaction

Do not rerun apply when `Configure-HubHttpsPilot.ps1` reports an automatic rollback failure or
when its state is `Prepared`, `ApplyFailedRollbackPending`, `ManualRollbackPending`, or
`RollbackFailed`. Pull the corrected deployment script, then run recovery on SON-IIS2 from
elevated Windows PowerShell:

```powershell
$repo = 'C:\SonAero\src\SonAeroInternalHub'
git -C $repo pull --ff-only origin main

& "$repo\deployment\Configure-HubHttpsPilot.ps1" -Rollback -WhatIf
```

Require `WHATIF_READY_RECOVERY`. Then run:

```powershell
& "$repo\deployment\Configure-HubHttpsPilot.ps1" -Rollback -Confirm:$false
```

Require `HTTPS_PILOT_RECOVERED_ROLLED_BACK_AND_HTTP_HEALTHY`. Recovery permits an empty original
HTTPS binding set, but it refuses to remove bindings or a firewall rule unless they exactly match
this saved transaction. It restores the recorded baseline and verifies all five HTTP health
endpoints before allowing another apply attempt. If recovery reports drift or a health error, stop
and preserve `C:\ProgramData\SonAero\deployment-state\https-pilot.json` for diagnosis.

### Recover an interrupted HTTPS application-config transaction

Do not rerun application-config apply when its state is `Prepared`, `ApplyInProgress`,
`ApplyFailedRollbackPending`, `ManualRollbackPending`, or `RollbackFailed`. Pull the corrected
script, then preview recovery from elevated Windows PowerShell on SON-IIS2:

```powershell
$repo = 'C:\SonAero\src\SonAeroInternalHub'
git -C $repo pull --ff-only origin main
if ($LASTEXITCODE -ne 0) { throw 'Git pull failed; no IIS configuration was changed.' }

& "$repo\deployment\Configure-HubHttpsApplicationConfig.ps1" -Topology Pilot -Rollback -WhatIf
```

Require `WHATIF_READY_ROLLBACK`. Then run the same script with
`-Topology Pilot -Rollback -Confirm:$false` and require
`HTTPS_APPLICATION_CONFIG_ROLLED_BACK_AND_DUAL_SCHEME_HEALTHY`. Recovery validates the exact IIS
paths, the secured backup locations and hashes, and every active file hash before changing either
production file. A terminal rollback can be rerun safely and returns
`HTTPS_APPLICATION_CONFIG_ALREADY_ROLLED_BACK_AND_DUAL_SCHEME_HEALTHY` after verification.

## Pilot completion gate

- Both locked installers finish successfully on only their named computer/account.
- Both users pass exact role verification over HTTPS.
- All five direct HTTPS applications and the Portal gateway return 200 from both computers.
- Cross-module links and return-to-Hub links stay on HTTPS 61xx ports.
- Online and closed-browser mention notifications work in both directions.
- HTTP remains healthy and the documented rollback previews pass.
- The root private key exists only in two encrypted offline backups.

Do not expand trust beyond these two workstations. The later company rollout still requires a
managed enterprise certificate/trust distribution design and separate approval.
