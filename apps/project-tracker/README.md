# Project Tracker App

Internal aerospace project tracker replacing `Project Tracker.xlsm`.

## What It Includes

- ASP.NET Core backend with React/TypeScript frontend.
- SQL Server Express production configuration.
- SQLite development configuration for local runs.
- Windows authentication support for IIS production.
- Admin/Editor/Viewer role model.
- Workbook import from the existing `Project Tracker.xlsm`.
- Portfolio dashboard, project task grid, Gantt timeline, and Excel/PDF exports.
- Centralized Hub administration for access, work calendars, work centers, holidays, archived-project recovery, and workbook imports.

## Project quantities

Project Detail stores **Required Quantity** and **Job Quantity** as optional positive values. Users
with the Edit Project Quantities permission can enter either value manually or choose **Pull
Quantities from Fulcrum**. The pull resolves job quantity from the matched Fulcrum job's
`quantityToMake` and required quantity from its linked sales-order part line. When Fulcrum omits a
value, Project Tracker retains the current manual value and reports a warning instead of clearing
it. Each value records whether it came from Manual entry or Fulcrum; the provider interface and
source fields are ready for a future Acumatica implementation.

The Fulcrum token is read from the protected **Fulcrum Public API** credential in Admin Hub. It
must have permission to view jobs and sales orders. No token belongs in Project Tracker settings
or source control. Son-Aero's ITAR tenant uses `https://api.fulcrumpro.us/`; a preserved legacy
`api.fulcrumpro.com` value is normalized to the ITAR host before any request is sent.

## Local Development

Run these from the `apps/project-tracker` folder inside the hub repository:

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"
$env:PATH="$env:DOTNET_ROOT;$env:DOTNET_ROOT\tools;$env:PATH"
# from apps/project-tracker
dotnet test
cd "src\ProjectTracker.Api\ClientApp"
npm run build
cd ..
dotnet run --launch-profile http
```

Open `http://localhost:5135`.

To run the full hub (Project Tracker + Portal) together, use `scripts\Start-Hub.ps1` at the repository root instead. See the [root README](../../README.md).
Shared users, groups, and module permissions are available at `http://localhost:5140/#/admin/access`.

Development mode uses `project-tracker-dev.db` and auto-imports the existing workbook when the database is empty.

## Production Defaults

- IIS site name: `ProjectTracker`
- Local URL target: `http://project-tracker`
- Database: `ProjectTracker` on `.\\SQLEXPRESS`
- Authentication: Windows Authentication
- Anonymous Authentication: disabled
- Initial Admin and Editor accounts are bootstrapped from `appsettings.Production.json` or IIS environment variables:

```json
{
  "Security": {
    "Admins": [ "DOMAIN\\josh.greer" ],
    "Editors": [ "DOMAIN\\planner.one", "DOMAIN\\planner.two" ]
  }
}
```

After startup, administrators manage assignments from **Hub Admin > Project Tracker > Access**.
Role changes are stored in the Project Tracker database and take effect on the user's next
request. New Windows accounts default to View Only after their first sign-in. The application
prevents removal of the final administrator.

The Hub Admin client calls the Project Tracker administration API with the signed-in user's
credentials. Configure the exact production Hub origin in IIS or the process environment:

```powershell
$env:Cors__HubOrigins__0='https://hub.example.internal'
```

Wildcard origins are rejected because credentialed administration requests are enabled.

- **Admin:** all pages, imports, settings, role management, and project editing.
- **Edit:** Dashboard, Project Detail, Calendar, and Past Projects with project editing.
- **View Only:** Dashboard, Project Detail, Calendar, and Past Projects without edit controls.

## Publish

```powershell
# from apps/project-tracker
.\deployment\publish.ps1
```

The publish output is written to `apps\project-tracker\publish`.
