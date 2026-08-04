# SON-AERO Hub server deployment

The assigned production servers are:

- **SON-IIS2** (`10.50.10.244`): application server for IIS and all four web applications.
- **SON-SQL2** (`10.50.10.242`): SQL Server and controlled Engineering drawing storage.

Do **not** run `scripts/Start-Hub.ps1` as the production host. That launcher intentionally uses
Development authentication and localhost URLs. Production uses IIS, Windows Authentication, SQL
Server, and each employee's real `SON4L\firstname.lastname` domain identity.

Follow [server-deployment.md](server-deployment.md) for the first installation, verification,
updates, backups, and rollback. Example Production settings are in [`templates`](templates). No
passwords, live secrets, or certificates belong in Git.

To build all four applications from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\deployment\Publish-Hub.ps1 `
  -ProjectTrackerUrl "http://SON-IIS2:5135"
```

Artifacts are written under `deployment\artifacts\hub` and are ignored by Git. Point IIS at
separate site directories, never at the repository or staging directory.
