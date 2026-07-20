# Deployment (Implemented Later)

Server deployment for the SON-AERO Internal Hub is **intentionally not configured yet**. During
this phase everything runs locally (see the [root README](../README.md)).

When deployment work begins, this folder will hold the hub-level server configuration. The
following are explicitly deferred and must **not** be set up until development is complete:

- IIS sites for the portal and each application
- Production SQL Server databases
- Internal DNS names
- HTTPS certificates
- Server backup jobs

Project Tracker already has standalone IIS + SQL Server notes that will feed into this work:
[apps/project-tracker/docs/iis-sqlserver-deployment.md](../apps/project-tracker/docs/iis-sqlserver-deployment.md).

Each application keeps its own `deployment/` folder (for example
`apps/project-tracker/deployment/publish.ps1`) for app-specific publishing.
