# Local Setup — SON-AERO Internal Hub

After cloning or extracting the repository on a Windows machine, run this once from the
repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Setup-Hub.ps1
```

This installs any missing prerequisites with `winget` (.NET 8 SDK and Node.js LTS), then
creates a desktop shortcut named **"Arda"** using the transparent Arda icon. The shortcut
runs `scripts\Start-Hub.ps1`. Setup also registers the current user's `sonaero-folder`
protocol handler, and every Hub start refreshes that registration to the current checkout.

Controlled-folder links accept `S:\` paths and map drive-less paths stored in Fulcrum to
`S:\`. Estimating quote folders also support network paths beneath the Son-Aero estimating
quote share. The handler limits Explorer to those locations and rejects traversal, device paths,
extra query values, and nested URI schemes. A folder opens directly. A file opens Explorer with
that file selected.

The employee Hub installer registers the same handler for the Windows user who runs it. To
update the folder handler separately, run the following in a standard PowerShell window from
the extracted employee installer directory while signed in as that user.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-OpenFolderProtocol.ps1 -InstallForCurrentUser
```

## Launching

Double-click the **Arda** shortcut, or run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Start-Hub.ps1
```

The launcher starts:

- **Portal** at `http://localhost:5140` (opened in your default browser)
- **Project Tracker** at `http://localhost:5135`
- **Engineering Hub** at `http://localhost:5150`
- **Estimating Dashboard** at `http://localhost:5160`
- **Quality Assurance** at `http://localhost:5170`

It rebuilds a frontend only when its source has changed, skips any app that is already running,
waits for all applications to become healthy, and then opens the portal. If startup fails it
shows an error dialog and writes details to `logs\<app>.err.log`.

Administrative settings are centralized at
`http://localhost:5140/#/admin/access`. Engineering, Estimating, and Quality
Assurance have module-specific administration sections there.

## Requirements for a fresh machine

- .NET 8 SDK
- Node.js LTS with npm (needed the first time each frontend is built)

## Running one application at a time

See the [root README](../README.md#run-an-application-independently) for running an application
on its own.

## Branding assets

Frontend logos live in each app's `ClientApp/public/brand`. To refresh them from the canonical
source in `shared/branding/web`:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Sync-Branding.ps1
```

## Troubleshooting

| Symptom | Check |
|---|---|
| "The .NET 8 SDK is required" | Run `Setup-Hub.ps1`, or install the .NET 8 SDK and reopen the shell |
| "Node.js/npm is required" | Install Node.js LTS, then rerun |
| An app never becomes healthy | Read `logs\<app>.err.log` in the repo root |
| Port already in use | Another instance is running; the launcher reuses healthy instances automatically |
| Logos missing after a fresh clone | Run `scripts\Sync-Branding.ps1` |
| A controlled folder link is rejected | Confirm the path is beneath `S:\` or the Son-Aero estimating quote share and contains no traversal or URL scheme |
| A controlled folder is unavailable | Confirm access to the required drive or network share and check that the folder or file exists |
