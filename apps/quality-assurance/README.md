# Quality Assurance

SON-AERO shipping-status and quality workflow built with an ASP.NET Core 8 host and React/Vite
client. The module recreates the operational fields from the Quality Shipping Status workbook in a
permission-controlled register without making the workbook the live data store.

The Dashboard shows the current user's open queue, overdue work, completed volume, and average
completion time. Authorized managers can also see queue statistics for other users. Shipping Status
defaults to open shipments, supports own/team/all scope and oldest/ship-date ordering, and keeps
shipped records available through the Past Shipments filter.

Each user can save a personal Shipping Status register layout with a custom column order, widths,
and optional-column visibility. Status, Part Number, and Action can be moved or resized but are
always retained in the register. Preferences are stored by user in the Quality database so they
follow the account across browsers and workstations.

Every create, field edit, assignment, and shipment completion writes an immutable audit entry.
Assignments can target a shared group or a user in that group. Quality administrators manage
customer or task-type routing rules from the central Admin Console, including specific-owner and
least-loaded assignment modes.

Access is granted through shared groups in the central Access screen. Quality permissions cover
module entry, queue scope, team statistics, assignment actions, completion, audit history, routing
rules, and separate view/edit access for every Shipping Status field. The Administrators group is
seeded with all Quality permissions; other groups receive only the permissions selected for them.

## Local development

The desktop Hub launcher starts the module at `http://localhost:5170` and opens it from the
application catalog. The service uses the shared Project Tracker development database for users,
groups, and permissions, plus `quality-assurance-dev.db` for shipments, assignment rules, and audit
history. Production uses the separate `QualityAssurance` SQL Server database configured by
`ConnectionStrings:QualityStore`.

```powershell
dotnet run --project apps/quality-assurance/src/QualityAssurance.Api/QualityAssurance.Api.csproj
```
