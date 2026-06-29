# IIS + SQL Server Express Deployment

## Server Prerequisites

1. Install SQL Server Express.
2. Install SQL Server Management Studio.
3. Install the ASP.NET Core Hosting Bundle for .NET 8.
4. Create DNS or a host record for `project-tracker` pointing to the server.

## Database

Create the database:

```sql
CREATE DATABASE [ProjectTracker];
```

Give the IIS app pool identity database access:

```sql
CREATE LOGIN [IIS APPPOOL\ProjectTracker] FROM WINDOWS;

USE [ProjectTracker];
CREATE USER [IIS APPPOOL\ProjectTracker] FOR LOGIN [IIS APPPOOL\ProjectTracker];
ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\ProjectTracker];
ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\ProjectTracker];
ALTER ROLE db_ddladmin ADD MEMBER [IIS APPPOOL\ProjectTracker];
```

The app runs EF migrations on startup when `Database:AutoMigrate` is true.

## IIS Site

1. Publish the app with `.\deployment\publish.ps1`.
2. Copy the `publish` folder to the server, for example `C:\Sites\ProjectTracker`.
3. Create an app pool named `ProjectTracker`.
4. Set app pool `.NET CLR version` to `No Managed Code`.
5. Create an IIS site named `ProjectTracker` pointing to `C:\Sites\ProjectTracker`.
6. Bind host name `project-tracker` on port 80.
7. Enable Windows Authentication.
8. Disable Anonymous Authentication.

## Production Settings

Create `appsettings.Production.json` in the publish folder:

```json
{
  "Authentication": {
    "Mode": "Windows"
  },
  "Database": {
    "Provider": "SqlServer",
    "AutoMigrate": true
  },
  "ConnectionStrings": {
    "SqlServer": "Server=.\\SQLEXPRESS;Database=ProjectTracker;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
  },
  "Security": {
    "Admins": [ "DOMAIN\\your.admin" ],
    "Editors": [ "DOMAIN\\planner.one" ]
  }
}
```

The `Security` lists bootstrap initial accounts only. After the first startup, use **Settings → User Roles** in the application to move known Windows accounts between Admin, Edit, and View Only. Assignments are stored in SQL Server. Accounts not configured above appear as View Only after their first sign-in.

## Backups

Copy `ProjectTrackerApp\deployment\backup-project-tracker.sql` to the server, then schedule it with SQL Server Agent if available, or Windows Task Scheduler plus `sqlcmd`.

Example:

```powershell
sqlcmd -S .\SQLEXPRESS -E -i "C:\Scripts\backup-project-tracker.sql"
```
