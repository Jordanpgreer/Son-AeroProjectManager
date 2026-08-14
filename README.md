# SON-AERO Internal Hub

A monorepo hosting SON-AERO's internal business applications behind a single application
**portal**. Project Tracker, Engineering Hub, Estimating Dashboard, and Quality Assurance run as isolated
applications registered with the Portal launcher.

Local development uses the desktop launcher. Production deployment is prepared for SON-IIS2 and
SON-SQL2; see [Server deployment](#server-deployment).

## Repository structure

```
SonAeroInternalHub/
├── apps/
│   ├── project-tracker/        # existing aerospace program tracker (ASP.NET Core + React)
│   │   ├── src/ tests/ docs/ deployment/
│   │   └── ProjectTrackerApp.sln
│   ├── engineering-hub/        # engineering drawing and document control
│   │   ├── src/ tests/
│   │   └── README.md
│   ├── estimating-dashboard/   # estimating workspace
│   ├── quality-assurance/      # shipping status and quality workflow
│   └── portal/                 # internal application launcher
│       ├── src/ tests/
│       └── PortalApp.sln
├── shared/
│   └── branding/               # canonical SON-AERO logos + design tokens
├── data/
│   └── project-tracker/
│       ├── legacy-workbooks/   # source/backup Excel workbooks (import source)
│       └── backups/            # local DB backups
├── scripts/                    # Start-Hub, Setup-Hub, Install-HubShortcut, Sync-Branding
├── deployment/                 # production publish script, templates, and server runbook
├── docs/                       # hub documentation
├── SonAeroInternalHub.sln      # root solution (both app backends + tests)
└── README.md
```

## Port assignments

| Application | URL | Notes |
|---|---|---|
| Portal | http://localhost:5140 | Hub homepage / launcher |
| Project Tracker | http://localhost:5135 | First registered application |
| Engineering Hub | http://localhost:5150 | Admin-only engineering module under test |
| Estimating Dashboard | http://localhost:5160 | Quote calculations and estimating-rate reference |
| Quality Assurance | http://localhost:5170 | Permission-controlled Shipping Status workflow |

## Prerequisites

- **.NET 8 SDK**
- **Node.js LTS** (with npm) — for building the React frontends
- Windows (the launcher and shortcut scripts are PowerShell)

First-time setup on a fresh machine (installs missing prerequisites via winget, creates the
desktop shortcut, and registers Engineering controlled-folder links for the current Windows user):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Setup-Hub.ps1
```

## Start the full hub

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Start-Hub.ps1
```

`Start-Hub.ps1`:

1. Resolves the repo root reliably (paths with spaces supported).
2. Refreshes the per-user `sonaero-folder` handler used by Engineering **Open folder** actions.
3. Locates the .NET 8 SDK and Node.js/npm.
4. Rebuilds each frontend only when its source changed.
5. Starts Project Tracker on **5135**, Engineering Hub on **5150**, Estimating Dashboard on **5160**, Quality Assurance on **5170**, and the Portal on **5140** (skips any already running).
6. Waits for each to report healthy, then opens the portal homepage.
7. On failure it shows an error dialog and writes `logs\<app>.err.log` (never a blank window).

Generated logs and build stamps live under `logs\` (git-ignored).

The desktop shortcut **"SON-AERO Hub"** (created by setup) runs the same launcher with the red
SON-AERO icon and works after any user clones/extracts the repo to a path with spaces.

## Run an application independently

**Project Tracker only:**

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"
cd apps\project-tracker\src\ProjectTracker.Api\ClientApp
npm install; npm run build
cd ..
dotnet run --launch-profile http        # http://localhost:5135
```

**Portal only:**

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"
cd apps\portal\src\Portal.Api\ClientApp
npm install; npm run build
cd ..
dotnet run --launch-profile http        # http://localhost:5140
```

**Engineering Hub only:**

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"
cd apps\engineering-hub\src\EngineeringHub.Api\ClientApp
npm install; npm run build
cd ..
dotnet run --launch-profile http        # http://localhost:5150
```

**Estimating Dashboard only:**

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"
cd apps\estimating-dashboard\src\EstimatingDashboard.Api\ClientApp
npm install; npm run build
cd ..
dotnet run --launch-profile http        # http://localhost:5160
```

**Quality Assurance only:**

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"
cd apps\quality-assurance\src\QualityAssurance.Api\ClientApp
npm install; npm run build
cd ..
dotnet run --launch-profile http        # http://localhost:5170
```

During frontend development you can also run the Vite dev server (`npm run dev`) in any
`ClientApp`; it proxies `/api` to the app's backend port.

## Adding a future application

The portal reads its catalog from configuration — no portal frontend changes are required to
add an application. See [docs/adding-an-application.md](docs/adding-an-application.md). In short:

1. Add the app under `apps/<new-app>/` (or point at an existing service).
2. Add an entry to `Portal:Applications` in
   `apps/portal/src/Portal.Api/appsettings.json` (id, name, description, category, icon, url,
   order, status, allowedRoles).
3. Add it to `scripts/Start-Hub.ps1` if the hub should launch it locally.

## Authentication approach

Both backends use the same model:

- **Production:** Windows Authentication (Negotiate) identifies the current user.
- **Local development:** a development authentication handler supplies a stub user so the apps
  run without a domain.

The portal shows the current user and role and filters application cards by role. **Hiding a
card is a usability convenience, not a security boundary** — each application still enforces its
own authorization independently. Project Tracker's `Users` table is the shared role authority for
the hub, so changes made in **Settings → User Roles** are reflected by both the tracker and portal.
The `Portal:Admins` and `Portal:Editors` configuration lists remain bootstrap fallbacks for a new
deployment or a temporarily unavailable role store.

## Server deployment

Production is designed for **SON-IIS2** as the IIS application server and **SON-SQL2** as the SQL
Server/data server. Do not use the Development desktop launcher as the server host; publish the
five applications and run them under IIS with Windows Authentication.

Follow [deployment/server-deployment.md](deployment/server-deployment.md) for the exact first
installation, `SON4L\firstname.lastname` role setup, validation, update, backup, and rollback steps.

## Documentation

- [docs/local-setup.md](docs/local-setup.md) — detailed local setup and troubleshooting
- [docs/adding-an-application.md](docs/adding-an-application.md) — register a new application
- [deployment/server-deployment.md](deployment/server-deployment.md) — SON-IIS2/SON-SQL2 production deployment
- [apps/project-tracker/README.md](apps/project-tracker/README.md) — Project Tracker specifics
- [shared/branding/README.md](shared/branding/README.md) — brand assets and token sync
