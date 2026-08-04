# First server deployment runbook

## Confirm the server roles

| Server | Address | Role |
|---|---:|---|
| SON-IIS2 | 10.50.10.244 | IIS application server; clone, build, and host the Hub here |
| SON-SQL2 | 10.50.10.242 | SQL Server plus backed-up Engineering drawing share |

Run these read-only checks in elevated PowerShell on the named server. They confirm—not change—the
current setup:

```powershell
# SON-IIS2
hostname
whoami
(Get-CimInstance Win32_ComputerSystem).Domain
Get-WindowsFeature Web-Server, Web-Windows-Auth

# SON-SQL2
hostname
whoami
Get-Service 'MSSQL*' -ErrorAction SilentlyContinue
Get-SmbShare
```

Do not deploy this application to a domain controller. Use DNS names instead of raw IP addresses
for employee-facing URLs so Integrated Windows Authentication works reliably.

`whoami` normally reports `SON4L\jordan.greer`. The Hub stores the backslash form. The Admin
Console also accepts `SON4L/jordan.greer` and converts it to the same account. A bare
`jordan.greer` is rejected so a same-named user from another domain cannot inherit permissions.

## 1. Prerequisites

On **SON-IIS2**, install:

- IIS with the Windows Authentication feature
- ASP.NET Core 8 Hosting Bundle
- Git
- .NET 8 SDK
- Node.js LTS with npm (needed to build the frontends)

If IIS was installed after the Hosting Bundle, repair/reinstall the Hosting Bundle. SON-SQL2 does
not need Git, Node, the repository, or the .NET SDK.

## 2. Clone the repository on SON-IIS2

Open PowerShell as administrator:

```powershell
New-Item -ItemType Directory -Force C:\SonAero\src
Set-Location C:\SonAero\src
git clone https://github.com/Jordanpgreer/Son-AeroProjectManager.git SonAeroInternalHub
Set-Location .\SonAeroInternalHub
git switch main
git pull --ff-only origin main
```

The source checkout is a build input. Never point IIS directly at it.

## 3. Cross-server application identity

The four IIS pools use `ApplicationPoolIdentity`. In a domain, those identities present the IIS
server's computer account when accessing network resources. SON-SQL2 therefore grants database and
share access to `SON4L\SON-IIS2$`. This is passwordless and avoids creating a shared service-account
credential while each IIS application remains locally isolated in its own pool.

## 4. Prepare SON-SQL2

Copy `deployment\Configure-SqlServer.ps1` to SON-SQL2 and run it from elevated PowerShell. It
validates the hostname and Windows administrator access before changing anything, exports a
registry backup, configures fixed TCP 1433, restricts the firewall rule to SON-IIS2, creates both
databases, grants `SON4L\SON-IIS2$`, and creates the controlled drawing share under
`C:\SonAero\Data`. If the current Windows identity is not already a SQL sysadmin, the script uses
SQL Server's restricted single-user recovery mode for the database work and always restores normal
multi-user mode; it does not create a permanent recovery login:

```powershell
powershell -ExecutionPolicy Bypass -File .\Configure-SqlServer.ps1 -Confirm
```

Then verify from SON-IIS2:

```powershell
Test-NetConnection SON-SQL2 -Port 1433
```

If the instance uses a different fixed port, update every Production connection string.

## 5. Initial employee URLs

The first deployment uses the domain server name and dedicated ports: Portal
`http://SON-IIS2:5140`, Project Tracker `http://SON-IIS2:5135`, Engineering
`http://SON-IIS2:5150`, and Estimating `http://SON-IIS2:5160`. Using the actual server name supports
Integrated Windows Authentication without custom alias SPNs. Internal aliases and HTTPS can be
added after the functional rollout.

## 6. Publish all four apps

From the repository root on SON-IIS2:

```powershell
powershell -ExecutionPolicy Bypass -File .\deployment\Publish-Hub.ps1 `
  -OutputRoot C:\SonAero\staging\hub `
  -ProjectTrackerUrl "/project-tracker-api"
```

The root-relative Project Tracker URL is compiled into the Portal Admin Console. It keeps Admin API
requests on the Portal origin so Windows-authenticated saves do not cross ports or require browser
CORS preflight. The output contains `Portal`, `ProjectTracker`, `EngineeringHub`, and
`EstimatingDashboard`.

Copy them into timestamped release folders, then deploy them to these stable IIS paths:

```text
C:\SonAero\sites\Portal
C:\SonAero\sites\ProjectTracker
C:\SonAero\sites\EngineeringHub
C:\SonAero\sites\EstimatingDashboard
```

Do not store databases, uploaded drawings, or live Production settings inside the repository.

## 7. Create Production settings

Copy the matching file from `deployment\templates` into each stable site folder and rename it
`appsettings.Production.json`:

| Site | Template | Values to finish |
|---|---|---|
| ProjectTracker | `project-tracker...json` | Confirm Portal origin `http://SON-IIS2:5140` |
| Portal | `portal...json` | Confirm the SON-IIS2 module URLs |
| EngineeringHub | `engineering-hub...json` | Confirm the `\\SON-SQL2\EngineeringDrawings$` share |
| EstimatingDashboard | `estimating-dashboard...json` | Confirm SQL port/instance |

All templates already target SON-SQL2. The Project Tracker template bootstraps
`SON4L\jordan.greer` as the first administrator. Change it only if a different account should
receive initial access. `Security.Admins` is authoritative for the first central Admin Console
account; `Portal.Admins` alone is insufficient.

## 8. Configure IIS on SON-IIS2

Install the official ASP.NET Core 8 Hosting Bundle after IIS. Then run the guarded setup script
after all four stable folders and Production settings exist:

```powershell
powershell -ExecutionPolicy Bypass -File .\deployment\Configure-IisServer.ps1 -Confirm
```

The script installs Windows Authentication and Application Initialization, creates four isolated
sites plus the dedicated `/project-tracker-api` application and pool, applies folder permissions, disables
Anonymous Authentication, enables Windows Authentication, reconciles the scoped firewall rule,
and does not report success until every root and gateway `/api/health` endpoint returns HTTP 200.

Start ProjectTracker first so it applies database migrations and seeds the initial administrator.
Then start EngineeringHub, EstimatingDashboard, and Portal.

## 9. Verify identity and roles

Test from a SON4L domain workstation—not through the Development desktop launcher:

1. Open every `/api/health` URL and confirm HTTP 200.
2. Open every `/api/me` URL and confirm `SON4L\jordan.greer`, not
   `DEV\ProjectTrackerAdmin`, is shown.
3. In the Portal Admin Console, add a user by pasting their `whoami` result.
4. Assign their Project Tracker groups and Engineering/Estimating Viewer, Editor, or Admin roles.
5. Verify Viewer can read but not edit; Editor can edit but not administer; inactive/unassigned
   accounts are denied.
6. Upload/download a test Engineering PDF, recycle the app pools, and confirm it persists.
7. Review IIS logs and Windows Event Viewer for authentication, SQL, or storage errors.

`/api/health` proves a process is running. Authenticated `/api/me` proves Windows identity and the
shared role database work.

For the exact post-install sequence, including default-deny deployment, warm start, automated role
verification, the employee shortcut, HTTPS prerequisites, and backup prerequisites, continue with
[production-rollout.md](production-rollout.md).

## 10. Backups, updates, and rollback

Back up both SQL databases, the Engineering drawing share, Production configuration, and TLS
bindings. Retain multiple restore points and perform a test restore.

For each update:

1. Back up both databases and the drawing share.
2. In the source checkout run `git pull --ff-only origin main`.
3. Publish into a new timestamped staging/release folder.
4. Smoke-test it, take each site offline with `app_offline.htm`, and switch/copy the binaries.
5. Bring up ProjectTracker first, then the other modules, and repeat the verification checklist.
6. Keep the previous binaries. A database migration can make a binary-only rollback unsafe, so the
   pre-update database backup is mandatory.
