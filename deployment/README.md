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
paths if health verification fails. Run `Configure-PortalProjectTrackerGateway.ps1` once on SON-IIS2
before the first gateway-aware release.

The default publish is intentionally topology-neutral: the same immutable frontend bundle resolves
permanent hostnames to `https://hub.son4l.local`, HTTP endpoints to the 51xx topology, and retained
HTTPS pilot endpoints to the 61xx topology at runtime. Publish-time Hub/module origin overrides are
intentionally unavailable so a server environment variable cannot silently break that rollback path.
