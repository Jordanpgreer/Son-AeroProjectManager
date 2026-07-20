# SON-AERO Internal Hub

A monorepo hosting SON-AERO's internal business applications behind a single application
**portal**. Today it contains the existing **Project Tracker** and a new **Portal** launcher;
new applications are added under `apps/` and registered with the portal.

Everything runs locally during development. IIS / server deployment is **not** configured yet
(see [Server deployment (later)](#server-deployment-later)).

## Repository structure

```
SonAeroInternalHub/
├── apps/
│   ├── project-tracker/        # existing aerospace program tracker (ASP.NET Core + React)
│   │   ├── src/ tests/ docs/ deployment/
│   │   └── ProjectTrackerApp.sln
│   └── portal/                 # new internal application launcher
│       ├── src/ tests/
│       └── PortalApp.sln
├── shared/
│   └── branding/               # canonical SON-AERO logos + design tokens
├── data/
│   └── project-tracker/
│       ├── legacy-workbooks/   # source/backup Excel workbooks (import source)
│       └── backups/            # local DB backups
├── scripts/                    # Start-Hub, Setup-Hub, Install-HubShortcut, Sync-Branding
├── deployment/                 # server deployment notes (implemented later)
├── docs/                       # hub documentation
├── SonAeroInternalHub.sln      # root solution (both app backends + tests)
└── README.md
```

## Port assignments

| Application | URL | Notes |
|---|---|---|
| Portal | http://localhost:5140 | Hub homepage / launcher |
| Project Tracker | http://localhost:5135 | First registered application |

## Prerequisites

- **.NET 8 SDK**
- **Node.js LTS** (with npm) — for building the React frontends
- Windows (the launcher and shortcut scripts are PowerShell)

First-time setup on a fresh machine (installs missing prerequisites via winget and creates the
desktop shortcut):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Setup-Hub.ps1
```

## Start the full hub

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Start-Hub.ps1
```

`Start-Hub.ps1`:

1. Resolves the repo root reliably (paths with spaces supported).
2. Locates the .NET 8 SDK and Node.js/npm.
3. Rebuilds each frontend only when its source changed.
4. Starts Project Tracker on **5135** and the Portal on **5140** (skips any already running).
5. Waits for both to report healthy, then opens the portal homepage.
6. On failure it shows an error dialog and writes `logs\<app>.err.log` (never a blank window).

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

During frontend development you can also run the Vite dev server (`npm run dev`) in either
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

## Server deployment (later)

The following are **out of scope until development is complete** and are not configured here:
IIS sites, production SQL Server databases, internal DNS, HTTPS certificates, and server backup
jobs. Project Tracker's existing IIS/SQL notes remain at
[apps/project-tracker/docs/iis-sqlserver-deployment.md](apps/project-tracker/docs/iis-sqlserver-deployment.md)
for when that work begins.

## Documentation

- [docs/local-setup.md](docs/local-setup.md) — detailed local setup and troubleshooting
- [docs/adding-an-application.md](docs/adding-an-application.md) — register a new application
- [apps/project-tracker/README.md](apps/project-tracker/README.md) — Project Tracker specifics
- [shared/branding/README.md](shared/branding/README.md) — brand assets and token sync
