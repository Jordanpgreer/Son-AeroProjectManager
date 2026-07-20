# Adding a Future Application

The portal homepage is data-driven. Adding an application is a **configuration change** — you do
not edit the portal's React components.

## 1. Register the application

Add an entry to the `Portal:Applications` array in
`apps/portal/src/Portal.Api/appsettings.json`:

```json
{
  "Id": "quality-hub",
  "Name": "Quality Hub",
  "Description": "Nonconformance reporting, corrective actions, and AS9100 document control.",
  "Category": "Quality",
  "Icon": "shield-check",
  "Url": "http://localhost:5150",
  "Order": 20,
  "Status": "Active",
  "AllowedRoles": []
}
```

### Fields

| Field | Meaning |
|---|---|
| `Id` | Stable unique key |
| `Name` | Card title |
| `Description` | One or two sentences |
| `Category` | Groups the card and drives the category filter |
| `Icon` | Icon key resolved by the frontend (see below); unknown keys fall back to a generic glyph |
| `Url` | Absolute URL the **Open** button navigates to (leave empty for coming-soon) |
| `Order` | Sort order (ascending) |
| `Status` | `Active`, `ComingSoon`, or `Maintenance` |
| `AllowedRoles` | Roles allowed to see the card; empty = everyone |

### Icon keys

The frontend maps icon keys to [lucide](https://lucide.dev) icons in
`apps/portal/src/Portal.Api/ClientApp/src/App.tsx` (the `ICONS` map). Current keys:
`gantt-chart`, `shield-check`, `truck`, `settings`, `boxes`, `wrench`, `clock`, `layout-grid`.
To use a new icon, import it from `lucide-react` and add a line to that map.

## 2. Role visibility (optional)

`AllowedRoles` controls which cards a user sees. This is a **usability filter, not security** —
the target application must enforce its own authorization. Roles come from the portal's
`PortalUserService` (Development role in local mode; Windows account → `Portal:Admins` /
`Portal:Editors` lists otherwise).

## 3. Launch it with the hub (optional)

If the hub should start the app locally, add it to the `$apps` array in
`scripts/Start-Hub.ps1` with its `ApiRoot`, `Url`, and a `HealthPath` that returns HTTP 200.

## 4. Verify

Rebuild the portal frontend (or run `scripts/Start-Hub.ps1`) and confirm the new card appears,
respects its status, and opens the correct URL.
