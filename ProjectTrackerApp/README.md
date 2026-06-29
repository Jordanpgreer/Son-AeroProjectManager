# Project Tracker App

Internal aerospace project tracker replacing `Project Tracker.xlsm`.

## What It Includes

- ASP.NET Core backend with React/TypeScript frontend.
- SQL Server Express production configuration.
- SQLite development configuration for local runs.
- Windows authentication support for IIS production.
- Admin/Editor/Viewer role model.
- Workbook import from the existing `Project Tracker.xlsm`.
- Portfolio dashboard, project task grid, Gantt timeline, holiday admin, import admin, and Excel/PDF exports.

## Local Development

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"
$env:PATH="$env:DOTNET_ROOT;$env:DOTNET_ROOT\tools;$env:PATH"
cd "C:\Users\USER\projects\non project folder\Project Tracker\ProjectTrackerApp"
dotnet test
cd "src\ProjectTracker.Api\ClientApp"
npm run build
cd ..
dotnet run --launch-profile http
```

Open `http://localhost:5135`.

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

After startup, Admins manage assignments from **Settings → User Roles**. Role changes are stored in the application database and take effect on the user's next request. New Windows accounts default to View Only after their first sign-in. The application prevents removal of the final Admin.

- **Admin:** all pages, imports, settings, role management, and project editing.
- **Edit:** Dashboard, Project Detail, Calendar, and Past Projects with project editing.
- **View Only:** Dashboard, Project Detail, Calendar, and Past Projects without edit controls.

## Publish

```powershell
cd "C:\Users\USER\projects\non project folder\Project Tracker\ProjectTrackerApp"
.\deployment\publish.ps1
```

The publish output is written to `ProjectTrackerApp\publish`.
