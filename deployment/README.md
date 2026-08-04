# SON-AERO Hub server deployment

The assigned production servers are:

- **SON-IIS2** (`10.50.10.244`): application server for IIS and all four web applications.
- **SON-SQL2** (`10.50.10.242`): SQL Server and controlled Engineering drawing storage.

Do **not** run `scripts/Start-Hub.ps1` as the production host. That launcher intentionally uses
Development authentication and localhost URLs. Production uses IIS, Windows Authentication, SQL
Server, and each employee's real `SON4L\firstname.lastname` domain identity.

Follow [server-deployment.md](server-deployment.md) for the first installation. For the current
post-install rollout (warm start, role verification, employee shortcut, HTTPS readiness, and
backup readiness), follow [production-rollout.md](production-rollout.md) in order. Example
Production settings are in [`templates`](templates). No passwords, live secrets, or certificates
belong in Git.

To build all four applications from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\deployment\Publish-Hub.ps1 `
  -ProjectTrackerUrl "/project-tracker-api"
```

Artifacts are written under `deployment\artifacts\hub` and are ignored by Git. Point IIS at
separate site directories, never at the repository or staging directory. For production updates,
use `Deploy-HubRelease.ps1`; it stages an immutable release, preserves Production settings, checks
all four applications plus the same-origin Project Tracker gateway, and rolls IIS back to the prior
paths if health verification fails. Run `Configure-PortalProjectTrackerGateway.ps1` once on SON-IIS2
before the first gateway-aware release.
