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
  -ProjectTrackerUrl '/project-tracker-api' `
  -HubUrl 'https://SON-IIS2:6140' `
  -EngineeringHubUrl 'https://SON-IIS2:6150' `
  -EstimatingDashboardUrl 'https://SON-IIS2:6160'
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

Copy `Configure-HubHttpsApplicationConfig.ps1` to `C:\SonAero` and run on SON-IIS2:

```powershell
$configScript = 'C:\SonAero\Configure-HubHttpsApplicationConfig.ps1'
& $configScript -WhatIf
& $configScript -Confirm:$false
```

Require `WHATIF_READY`, then
`HTTPS_APPLICATION_CONFIG_APPLIED_AND_DUAL_SCHEME_GATEWAY_HEALTHY`. The transaction backs up the
two active production JSON files under a restricted ACL, uses HTTPS-first dual CORS, restarts only
the three affected pools, checks both schemes plus both gateway paths, and restores the originals
on failure.

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
`HUB_USER_ACCESS_VERIFIED`. Then manually open all four HTTPS endpoints through the Hub and confirm
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
2. On SON-IIS2: `Configure-HubHttpsApplicationConfig.ps1 -Rollback -WhatIf`, then repeat with
   `-Confirm:$false`.
3. Retarget both shortcuts to `http://SON-IIS2:5140` with `Install-EmployeeHubShortcut.ps1`.
4. On SON-IIS2: `Configure-HubHttpsPilot.ps1 -Rollback -WhatIf`, then repeat with
   `-Confirm:$false`.
5. On each named pilot workstation, run `Set-HubPilotWorkstationTrust.ps1` locally with its trust
   bundle, exact `-ExpectedComputerName`, `-Operation Remove`, preview first, then apply.
6. On SON-IIS2, run `Install-HubPilotServerCertificate.ps1 -Operation RemoveAll`, preview first,
   then apply. It refuses removal while the leaf is still bound.

The existing HTTP bindings remain the safety path throughout. A rollback must end with all four
HTTP health endpoints returning 200.

## Pilot completion gate

- Both locked installers finish successfully on only their named computer/account.
- Both users pass exact role verification over HTTPS.
- All four direct HTTPS applications and the Portal gateway return 200 from both computers.
- Cross-module links and return-to-Hub links stay on HTTPS 61xx ports.
- Online and closed-browser mention notifications work in both directions.
- HTTP remains healthy and the documented rollback previews pass.
- The root private key exists only in two encrypted offline backups.

Do not expand trust beyond these two workstations. The later company rollout still requires a
managed enterprise certificate/trust distribution design and separate approval.
