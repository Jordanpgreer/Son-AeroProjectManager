# Quality Assurance

SON-AERO shipping-status and quality workflow built with an ASP.NET Core 8 host and React/Vite
client. The module recreates the operational fields from the Quality Shipping Status workbook in a
permission-controlled register without making the workbook the live data store.

The Dashboard shows the current user's open queue, overdue work, completed volume, and average
completion time. Authorized managers can also see queue statistics for other users. Shipping Status
defaults to open shipments, supports own/team/all scope and oldest/ship-date ordering, and keeps
shipped records available through the Past Shipments filter. Shipper numbers replace the previous
Sales Order label, and each record can contain multiple part lines with whole-number quantities,
per-unit pricing, and calculated currency totals.

Each user can save a personal Shipping Status register layout with a custom column order, widths,
and optional-column visibility. Status, Part Number, and Action can be moved or resized but are
always retained in the register. Preferences are stored by user in the Quality database so they
follow the account across browsers and workstations.

Every create, field edit, assignment, external synchronization, QA completion, and shipment
completion writes an immutable audit entry.
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

## Fulcrum shipment synchronization

The `quality-records` enterprise adapter performs read-only exact shipper-number matching against
Fulcrum. It pulls Ship By, customer, purchase order, part quantities, unit prices, and Fulcrum
status. A Fulcrum `shipped` status automatically moves the Arda record to Past Shipments. QA
Complete is an Arda-only action that changes the record to Ready to Ship and places it in the
Shipping group queue; it never pushes a Fulcrum status change.

The background sync interval defaults to five minutes and a new or edited shipper number is checked
immediately. Local development disables external synchronization. Production requires the protected
Fulcrum Public API credential from Admin Hub and should set
`QualityIntegration:FulcrumShipmentUrlTemplate` to the tenant's HTTPS shipment-detail route. The
template supports `{id}` and `{shipperNumber}` placeholders. If it is blank, synchronization still
works but Arda does not render a Fulcrum hyperlink.

SQL Server is the canonical design-time migration provider. Migration operations remain compatible
with SQLite for local development, and the Quality test suite generates the complete SQL Server
script offline to guard identity columns, native store types, migration discovery, and model-snapshot
drift. Before first production activation, provision `QualityAssurance` with the deployment SQL
tooling, confirm that no prior Quality migration was partially recorded, and validate the reviewed
idempotent script against a disposable SQL Server database. The application applies pending Quality
migrations during startup, so the IIS identity requires the database roles granted by
`deployment/Configure-SqlServer.ps1`.

For an existing pre-activation IIS installation whose current Quality binary cannot pass health against
SQL Server, deploy the corrected publish output with
`deployment/Deploy-QualityAssuranceRelease.ps1 -FirstActivation`. This bounded bootstrap switches
only the Quality site path, requires the candidate health endpoint to succeed, and restores the
prior path and pool state if the candidate fails. Activate the Portal card only after the script
reports `QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY`.

```powershell
dotnet run --project apps/quality-assurance/src/QualityAssurance.Api/QualityAssurance.Api.csproj
```
