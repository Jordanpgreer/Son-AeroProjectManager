# Permanent managed-certificate HTTPS on SON-IIS2

This runbook configures the IT-issued certificate for five hostname-based IIS bindings on the
standard HTTPS port. It is separate from the two-person private-CA pilot.

| IIS site | Permanent URL |
|---|---|
| `SonAeroPortal` | `https://hub.son4l.local` |
| `ProjectTracker` | `https://projects.hub.son4l.local` |
| `EngineeringHub` | `https://engineering.hub.son4l.local` |
| `EstimatingDashboard` | `https://estimating.hub.son4l.local` |
| `QualityAssurance` | `https://quality.hub.son4l.local` |

All five bindings use TCP 443, SNI, and the same managed wildcard certificate. The transaction
does not remove or repurpose HTTP ports `5135`-`5170`, pilot HTTPS ports `6135`-`6170`, firewall
rules, application files, warm-start tasks, or employee shortcuts.

## Before the maintenance window

Confirm all of these external prerequisites:

1. The leaf certificate is in `Cert:\LocalMachine\My` on `SON-IIS2`, has its private key, Server
   Authentication EKU, an exact SAN for `hub.son4l.local`, and `*.hub.son4l.local`.
2. The managed certificate chain and revocation endpoints work from `SON-IIS2` and representative
   domain workstations.
3. The five DNS names resolve only to `10.50.10.244`.
4. Domain workstations can reach TCP 443 on `SON-IIS2`. These scripts deliberately do not change
   Windows Firewall or network firewall policy.
5. Windows Authentication aliases are ready. Query the identity that owns HTTP on the server and
   have the domain administrator verify the five `HTTP/<hostname>` SPNs are unique. Do not run
   `setspn -S` against a guessed identity. DNS and a certificate alone do not create SPNs.
6. Keep the old HTTP URLs, `SON-IIS2:61xx` pilot URLs, and shortcuts in service until workstation
   validation is complete. The production transaction treats all ten retained bindings as required
   rollback surfaces and refuses to start if any one is missing or unhealthy.

## 1. Pull the release and identify the certificate

Run in **elevated Windows PowerShell 5.1 on the SON-IIS2 remote desktop**:

```powershell
$ErrorActionPreference = 'Stop'
$repo = 'C:\SonAero\src\SonAeroInternalHub'

git -C $repo fetch --prune origin
if ($LASTEXITCODE -ne 0) { throw 'Git fetch failed; IIS was not changed.' }
git -C $repo pull --ff-only origin main
if ($LASTEXITCODE -ne 0) { throw 'Git pull failed; IIS was not changed.' }
if (@(git -C $repo status --porcelain).Count -ne 0) { throw 'Server checkout is dirty. Stop and inspect it.' }

$releaseId = (git -C $repo rev-parse --short HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($releaseId)) { throw 'Could not determine the release commit.' }
$packageRoot = "C:\SonAero\staging\hub-$releaseId"
if (Test-Path -LiteralPath $packageRoot) { throw "Staging path already exists; inspect it: $packageRoot" }

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
if ($LASTEXITCODE -ne 0) { throw 'Release deployment failed; review its automatic rollback result.' }

Get-ChildItem Cert:\LocalMachine\My |
  Where-Object { $_.HasPrivateKey } |
  Select-Object Subject, Issuer, Thumbprint, NotAfter |
  Format-Table -AutoSize
```

Copy the 40-character certificate-store thumbprint shown for the IT-issued `hub.son4l.local`
certificate. A certificate-file SHA-256 is not the binding thumbprint.

The release apply must end with `HUB_RELEASE_DEPLOYED_AND_HEALTHY`. It must be healthy on the
retained HTTP endpoints before binding or application configuration changes; do not point
shortcuts at the new hostnames yet.

## 2. Secure and complete the retained pilot rollback surface once

Before any pilot rollback/retirement or permanent binding work, migrate the already-deployed legacy
pilot state from its inherited ACL. The authentic legacy transaction contains the original four
pilot sites only; Quality Assurance was added later and is not recorded in that state. Run both
commands elevated on SON-IIS2 after pulling this release:

```powershell
& "$repo\deployment\Configure-HubHttpsPilot.ps1" -MigrateLegacyStateProtection -WhatIf
& "$repo\deployment\Configure-HubHttpsPilot.ps1" -MigrateLegacyStateProtection -Confirm:$false
```

Require `WHATIF_READY_HTTPS_PILOT_STATE_PROTECTION_MIGRATION`, then
`HTTPS_PILOT_STATE_PROTECTION_MIGRATED` (or the idempotent
`HTTPS_PILOT_STATE_PROTECTION_ALREADY_CURRENT`). The mode accepts only the exact deployed
`https-pilot.json` v1 Applied state with an empty prior 61xx baseline, corroborates it against the
live certificate, original four pilot bindings, restricted firewall/remotes, retained HTTP bindings,
and health, and changes only SYSTEM/Administrators ACL protection. It never changes state content,
IIS, or firewall configuration. Stop on any mismatch; do not manually copy or bless the JSON.

After that migration succeeds, preview and apply the one-time Quality Assurance compatibility
transaction using its defaults:

```powershell
& "$repo\deployment\Configure-HubHttpsPilotQualityExtension.ps1" -WhatIf
& "$repo\deployment\Configure-HubHttpsPilotQualityExtension.ps1" -Confirm:$false
```

Require `WHATIF_READY_HTTPS_PILOT_QA_EXTENSION`, then
`HTTPS_PILOT_QA_EXTENSION_APPLIED_AND_FIVE_SITE_HEALTHY`. This transaction adds only the missing
Quality Assurance binding `*:6170:` and expands the exact restricted pilot firewall rule to include
TCP 6170 with the same recorded remote-address scope. It preserves every HTTP binding, all four
existing 61xx pilot bindings, and every permanent TCP 443 binding. Its distinct protected rollback
state is `C:\ProgramData\SonAero\deployment-state\https-pilot-quality-extension.json`.

Do not continue to production readiness until both the legacy protection migration and the Quality
Assurance compatibility transaction have completed with their exact success markers.

The pilot, compatibility, and permanent binding scripts share one global transaction lock, so their
state checks, apply, rollback, and this migration cannot overlap.

### Recover an interrupted QA extension

If an extension apply reports that state persistence and automatic rollback both failed, stop and
pull the corrected release before doing anything else. Do not rerun apply, edit JSON, or change IIS
bindings/firewall manually. A durable `Prepared` state with the exact five-site live topology is
recovered by first returning to the recorded four-site baseline:

```powershell
git -C $repo pull --ff-only origin main
& "$repo\deployment\Configure-HubHttpsPilotQualityExtension.ps1" -Rollback -WhatIf
& "$repo\deployment\Configure-HubHttpsPilotQualityExtension.ps1" -Rollback -Confirm:$false
```

Require `WHATIF_READY_HTTPS_PILOT_QA_EXTENSION_ROLLBACK`, then
`HTTPS_PILOT_QA_EXTENSION_ROLLED_BACK_AND_FOUR_SITE_HEALTHY`. Only after that exact recovery marker
may the normal QA extension preview/apply pair above be run again. The rollback owns only QA 6170
and the firewall's fifth port; it preserves all HTTP, original four 61xx, and permanent 443 bindings.

## 3. Run the read-only readiness audit

```powershell
$thumbprint = 'PASTE_40_CHARACTER_CERTIFICATE_THUMBPRINT'

& "$repo\deployment\Test-HubProductionHttpsReadiness.ps1" `
  -CertificateThumbprint $thumbprint `
  -ExpectedServerAddress '10.50.10.244'
```

Before bindings exist, the last line must be `PRODUCTION_HTTPS_PREREQUISITES_READY`. The audit is
strict: it fails on an untrusted/revoked/unreachable chain, wrong DNS, certificate mismatch,
catch-all or wrong-site TCP 443 conflicts, and missing or unhealthy HTTP or 61xx rollback
endpoints. It makes no changes.

## 4. Preview the transaction

```powershell
& "$repo\deployment\Configure-HubProductionHttps.ps1" `
  -CertificateThumbprint $thumbprint `
  -ExpectedServerAddress '10.50.10.244' `
  -WhatIf
```

The last line must begin with `WHATIF_READY_PRODUCTION_HTTPS`. The preview writes no transaction
state and changes no IIS binding.

## 5. Apply during the maintenance window

```powershell
& "$repo\deployment\Configure-HubProductionHttps.ps1" `
  -CertificateThumbprint $thumbprint `
  -ExpectedServerAddress '10.50.10.244' `
  -Confirm:$false
```

Required final line:

```text
PRODUCTION_HTTPS_CONFIGURED_AND_DUAL_SCHEME_HEALTHY
```

The script snapshots the exact five target-host bindings, guards every unrelated HTTP/pilot/IIS
binding against drift, creates/reconciles the five SNI bindings, and performs credentialed health
checks over the permanent HTTPS hostnames, existing HTTP ports, and retained `SON-IIS2:61xx` pilot
URLs. If any step fails, it restores the exact prior target-host state and verifies both rollback
surfaces. Do not manually edit IIS after a failure; first read the rollback message and the state file at
`C:\ProgramData\SonAero\deployment-state\https-production-hostnames.json`.

All HTTPS transaction state files are confined directly beneath that deployment-state directory,
which must be an ordinary (non-reparse) path. State is trusted only when its protected ACL grants
FullControl exclusively to `SYSTEM` and `BUILTIN\Administrators`; legacy or manually copied state
with broader/inherited permissions is refused until an administrator inspects and secures it.

The command is idempotent. Repeating it against the same healthy certificate and bindings ends
with `PRODUCTION_HTTPS_ALREADY_CONFIGURED_AND_DUAL_SCHEME_HEALTHY`.

For a later managed-certificate renewal, do not overwrite the applied transaction state or edit
bindings manually. Validate the replacement thumbprint with the readiness audit, run the documented
rollback, and then apply the replacement thumbprint as a new transaction. The binding preflight
allows this controlled old-to-new certificate reconciliation.

## 6. Enable browser preflight, then switch application configuration

Only after the shared-port bindings are healthy, run the following elevated as a domain identity
already authorized to view Project Tracker. Configure the direct site so an anonymous browser
`OPTIONS` preflight can reach ASP.NET Core while protected APIs still challenge with Windows
Authentication. The repair rejects `NT AUTHORITY\SYSTEM`; do not run it from N-Central System
Shell. The same-origin Portal gateway remains Windows-only:

```powershell
& "$repo\deployment\Configure-ProjectTrackerCorsAuthentication.ps1" -WhatIf
& "$repo\deployment\Configure-ProjectTrackerCorsAuthentication.ps1" -Confirm:$false
```

The preview must end with `WHATIF_READY_PROJECT_TRACKER_CORS_AUTHENTICATION`. The apply must end
with `PROJECT_TRACKER_CORS_AUTHENTICATION_CONFIGURED_AND_VERIFIED` (or
`PROJECT_TRACKER_CORS_AUTHENTICATION_ALREADY_CONFIGURED_AND_VERIFIED`). The script proves that
anonymous preflight succeeds for every approved Portal origin already present in the active
Project Tracker `Cors.HubOrigins` array. It rejects missing, empty, wildcard, duplicate, or unknown
origin configuration rather than weakening CORS. Independently, it proves that anonymous `/api/me` is
denied with 401 on both retained direct bindings and that credentialed `/api/me` returns an
`accountName` exactly matching the current `WindowsIdentity`. It also proves the gateway still has
Anonymous disabled and Windows enabled. If configuration or verification fails after mutation, it
restores and independently verifies the prior direct-site IIS state. The following application-config
transaction adds the permanent origin and then verifies that newly installed origin inside its own
rollback boundary.

This authentication boundary is topology-neutral and must remain in place during production
rollback because both retained Portal origins also use browser preflight. Do not reverse it when
removing the port-443 bindings.

Then preview and apply the separate application-config transaction. Its dedicated state file
captures the active retained configuration without consuming the older Pilot state. A pre-hardening
Pilot application-config state with inherited ACLs remains untrusted historical evidence, not an
available rollback record:

```powershell
$applicationConfigState = 'C:\ProgramData\SonAero\deployment-state\https-production-application-config.json'

& "$repo\deployment\Configure-HubHttpsApplicationConfig.ps1" `
  -Topology Production `
  -StatePath $applicationConfigState `
  -WhatIf

& "$repo\deployment\Configure-HubHttpsApplicationConfig.ps1" `
  -Topology Production `
  -StatePath $applicationConfigState `
  -Confirm:$false

# Required post-check; this is read-only because the transaction is already applied.
& "$repo\deployment\Configure-HubHttpsApplicationConfig.ps1" `
  -Topology Production `
  -StatePath $applicationConfigState `
  -WhatIf
```

The preview must end with `WHATIF_READY`. The apply must end with
`HTTPS_APPLICATION_CONFIG_APPLIED_AND_DUAL_SCHEME_GATEWAY_HEALTHY` (or
`HTTPS_APPLICATION_CONFIG_ALREADY_APPLIED_AND_RETAINED_ENDPOINTS_HEALTHY` on an idempotent
rerun). This transaction changes the Portal cards
to the five permanent hostnames and keeps Project Tracker CORS origins in this order:

1. `https://hub.son4l.local`
2. `https://SON-IIS2:6140`
3. `http://SON-IIS2:5140`

It validates permanent HTTPS, retained 61xx HTTPS, retained HTTP, and the Portal Project Tracker
gateway on both permanent and pilot paths. It also repeats the anonymous-preflight, anonymous-401,
and credentialed identity boundary checks. If it fails, it restores the prior application config;
the healthy 443 bindings remain in place until you deliberately run the binding rollback.
After a successful apply, the required post-check ends with
`HTTPS_APPLICATION_CONFIG_ALREADY_APPLIED_AND_RETAINED_ENDPOINTS_HEALTHY`. Any apply result that
reports restored configuration or an automatic rollback failure is a stop condition: capture the
exact output and state path, and do not rerun the apply blindly.

## 7. Validate from Jordan and Josh's domain workstations

On each workstation, while signed in as the real employee, verify there is no certificate warning
and that each application identifies the employee correctly:

```powershell
$urls = @(
  'https://hub.son4l.local/api/health',
  'https://projects.hub.son4l.local/api/health',
  'https://engineering.hub.son4l.local/api/health',
  'https://estimating.hub.son4l.local/api/health',
  'https://quality.hub.son4l.local/api/health',
  'https://hub.son4l.local/project-tracker-api/api/health'
)

foreach ($url in $urls) {
  $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $url -TimeoutSec 20
  [pscustomobject]@{ Url = $url; StatusCode = $response.StatusCode }
}
```

Also open `https://hub.son4l.local` in Edge, enter every module, use the logo to return to the Hub,
and verify the expected permissions. The HTTP and 61xx paths must remain live throughout this
stabilization period. Configure warm start for the permanent hostnames from elevated Windows
PowerShell 5.1 on `SON-IIS2`:

```powershell
& "$repo\deployment\Configure-IisWarmStart.ps1" -Scheme https -PermanentHttps -WhatIf
& "$repo\deployment\Configure-IisWarmStart.ps1" -Scheme https -PermanentHttps -Confirm:$false
```

The apply run must end with `WARM_START_CONFIGURED_AND_HEALTHY`; it also preserves
`-PermanentHttps` in the Local System startup-recovery task. The older `-Scheme https` command
without `-PermanentHttps` deliberately remains the `SON-IIS2:61xx` pilot profile.

Then run the complete role test interactively as each pilot employee using the same expectations
already assigned in Hub Admin, but add `-Scheme https -PermanentHttps`:

```powershell
& 'C:\Temp\Test-HubUserAccess.ps1' `
  -Scheme https `
  -PermanentHttps `
  -ExpectedAccountName 'SON4L\firstname.lastname' `
  -ExpectedPortalRole Viewer `
  -ExpectedPortalModuleRoles @{
    engineering = 'NoAccess'
    estimating = 'NoAccess'
    'quality-assurance' = 'NoAccess'
  } `
  -ExpectedProjectTrackerAccess NoAccess `
  -ExpectedEngineeringRole NoAccess `
  -ExpectedEstimatingRole NoAccess `
  -ExpectedQualityAssuranceRole NoAccess
```

The test must end with `HUB_USER_ACCESS_VERIFIED`. Finally build and distribute a fresh employee
ZIP; it now defaults to `https://hub.son4l.local/` and records that origin inside the package.
Do not replace Jordan's or Josh's installed shortcut until both users pass the full six-surface
role test.

Before shortcut replacement, verify the same Web Push public key is enabled on the permanent,
pilot, and HTTP Project Tracker surfaces:

```powershell
$pushUrls = @(
  'https://projects.hub.son4l.local/api/push/public-key',
  'https://SON-IIS2:6135/api/push/public-key',
  'http://SON-IIS2:5135/api/push/public-key'
)
$pushResults = @($pushUrls | ForEach-Object {
  $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $_ -TimeoutSec 20
  $payload = $response.Content | ConvertFrom-Json
  [pscustomobject]@{ Uri = $_; StatusCode = $response.StatusCode; Enabled = [bool]$payload.enabled; PublicKey = [string]$payload.publicKey }
})
$pushResults | Format-Table Uri, StatusCode, Enabled -AutoSize
if (@($pushResults | Where-Object { $_.StatusCode -ne 200 -or -not $_.Enabled -or [string]::IsNullOrWhiteSpace($_.PublicKey) }).Count -gt 0 -or
    @($pushResults.PublicKey | Sort-Object -Unique).Count -ne 1) {
  throw 'Web Push is not enabled with one consistent public key on all retained surfaces.'
}
```

The public-key check does not migrate a browser subscription: Push API subscriptions are scoped
to their origin. Before replacing shortcuts, Jordan and Josh must each open
`https://projects.hub.son4l.local`, enable notifications (or unsubscribe and subscribe again if the
UI already shows an older subscription), and complete this delivery test on the permanent origin:

1. Close the Project Tracker browser window.
2. From the other pilot account, create a real `@mention` that targets the test user.
3. Confirm the Windows notification arrives for the closed browser.
4. Click the notification and confirm it opens the intended Project Tracker record under
   `https://projects.hub.son4l.local`, not a `SON-IIS2` URL.

Record a pass for both Jordan and Josh. A matching public key without both delivery/click-through
passes is not sufficient to replace their shortcuts.

Then build the employee package:

```powershell
& "$repo\deployment\New-EmployeeHubInstallerPackage.ps1" -Confirm:$false
```

To produce an intentionally retained HTTP pilot package, supply
`-HubUri 'http://SON-IIS2:5140'`. A binding change alone does not update application settings,
warm start, role tests, web push, or already-installed shortcuts.

## Manual rollback

Rollback is ordered because there are separate application-config and binding transactions. First
restore the pilot Portal URLs/CORS while all bindings are still reachable:

```powershell
$applicationConfigState = 'C:\ProgramData\SonAero\deployment-state\https-production-application-config.json'
& "$repo\deployment\Configure-HubHttpsApplicationConfig.ps1" `
  -Topology Production -Rollback -StatePath $applicationConfigState -WhatIf
& "$repo\deployment\Configure-HubHttpsApplicationConfig.ps1" `
  -Topology Production -Rollback -StatePath $applicationConfigState -Confirm:$false
```

That apply must end with `HTTPS_APPLICATION_CONFIG_ROLLED_BACK_AND_DUAL_SCHEME_HEALTHY`. Before 443
is removed, Jordan and Josh must each open `https://projects.hub.son4l.local` and disable
notifications/unsubscribe for that permanent origin. Confirm that their retained pilot-origin
subscriptions are still enabled; do not disable those. This prevents duplicate notifications whose
click target points at a hostname that is about to be removed.

Next, restore warm start to the retained 61xx pilot profile while both HTTPS topologies are still
reachable:

```powershell
& "$repo\deployment\Configure-IisWarmStart.ps1" -Scheme https -WhatIf
& "$repo\deployment\Configure-IisWarmStart.ps1" -Scheme https -Confirm:$false
```

The apply run must end with `WARM_START_CONFIGURED_AND_HEALTHY` and must replace the startup task's
permanent-hostname arguments with the 61xx profile.

If Jordan's or Josh's permanent shortcut was installed, build an approved retained package and run
it as each user before removing 443:

```powershell
& "$repo\deployment\New-EmployeeHubInstallerPackage.ps1" `
  -HubUri 'https://SON-IIS2:6140' `
  -Confirm:$false
```

After installing that package, each user must open the desktop shortcut and confirm the Hub and all
authorized modules work through the retained 61xx topology. An HTTP rollback package using
`-HubUri 'http://SON-IIS2:5140'` is also approved when the pilot certificate is unavailable. Do not
remove the 443 bindings while either installed shortcut still targets `hub.son4l.local`.

Then preview and restore the exact prior shared-port target bindings:

```powershell
& "$repo\deployment\Configure-HubProductionHttps.ps1" -Rollback -WhatIf
& "$repo\deployment\Configure-HubProductionHttps.ps1" -Rollback -Confirm:$false
```

Required final line:
`PRODUCTION_HTTPS_ROLLED_BACK_AND_RETAINED_HTTP_PILOT_HTTPS_HEALTHY`. Binding rollback restores
only the five recorded production-host bindings and refuses to overwrite unrelated or target-host
drift. It never removes HTTP or 61xx bindings.

The release is topology-neutral: permanent hostnames return to `https://hub.son4l.local`, retained
HTTP entry points return to the Hub on port 5140, and retained non-production HTTPS entry points
return to the Hub on port 6140. The configuration and binding rollback above therefore restores a
functional pilot UI without rebuilding or redeploying the release. Redeploy a known-good previous
package with the immutable `Deploy-HubRelease.ps1` procedure only when the application release
itself is also suspected.

### Later pilot retirement or full pilot rollback

This is a separate operation from permanent TCP 443 rollback. Never run these commands merely to
roll back the permanent hostname transaction: `Configure-HubProductionHttps.ps1 -Rollback`
deliberately preserves every 61xx binding.

When the pilot itself is eventually retired or fully rolled back, reverse the compatibility
transaction first, then the original four-site pilot transaction:

```powershell
& "$repo\deployment\Configure-HubHttpsPilotQualityExtension.ps1" -Rollback -WhatIf
& "$repo\deployment\Configure-HubHttpsPilotQualityExtension.ps1" -Rollback -Confirm:$false

& "$repo\deployment\Configure-HubHttpsPilot.ps1" -Rollback -WhatIf
& "$repo\deployment\Configure-HubHttpsPilot.ps1" -Rollback -Confirm:$false
```

Require `WHATIF_READY_HTTPS_PILOT_QA_EXTENSION_ROLLBACK`, then
`HTTPS_PILOT_QA_EXTENSION_ROLLED_BACK_AND_FOUR_SITE_HEALTHY` before starting the original pilot
rollback (the idempotent result is
`HTTPS_PILOT_QA_EXTENSION_ALREADY_ROLLED_BACK_AND_FOUR_SITE_HEALTHY`). Reversing the order would
leave the original transaction unable to restore its exact four-site firewall and binding baseline.
