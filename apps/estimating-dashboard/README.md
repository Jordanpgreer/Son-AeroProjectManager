# Estimating Dashboard

## Start locally

From the repository root, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-EstimatingDashboard.ps1
```

The launcher reuses an already healthy instance and otherwise starts the compiled
dashboard directly at `http://localhost:5160`. Startup output is written under
`logs\estimating-dashboard.*.log`.

Standalone SON-AERO estimating workspace with an ASP.NET Core 8 host and React/Vite client.

## Local development

```powershell
cd src\EstimatingDashboard.Api\ClientApp
npm ci
npm run build
cd ..
dotnet run --launch-profile http
```

Open `http://localhost:5160`.

The application requires authentication. Local development uses the configured development
identity; production uses Windows Authentication.

## Scheduled Fulcrum quote log synchronization

Production enables an automatic Fulcrum quote synchronization every 30 minutes, aligned to the
top and bottom of each hour. The application does not call Fulcrum at startup. Each scheduled run
is claimed in the shared database, which prevents duplicate calls after an IIS recycle or when
more than one application process is active.

The **Quotes** dashboard routes the Fulcrum quote number, quote status, Estimating Rep custom
field, and RFQ Due Date custom field into each estimator's personal queue. Arda status, Arda notes,
an estimating-due-date override, changed-by, changed-at, and calculated status age are stored only
in Arda. Without an override, the estimating due date is calculated as one Monday-through-Thursday
business day before the RFQ due date and follows later RFQ-date changes automatically. Scheduled and
manual Fulcrum pulls never map or overwrite those Arda workflow fields.

An Arda administrator configures or rotates the token in **Admin Hub → API Keys** using the
reserved name **Fulcrum Public API**. The token is encrypted with Windows machine-level
protection before it is stored in the shared database; it is never returned by the API or shown
again in the browser. Do not store the token in an appsettings file or commit it to the
repository. The Hub and Estimating Dashboard must run on the same Windows application server so
both can use the protected value.

Son-Aero's ITAR tenant uses Fulcrum's `https://api.fulcrumpro.us/` API host. The application also
normalizes a legacy `api.fulcrumpro.com` setting to the ITAR host at runtime so preserved production
configuration cannot send the ITAR token to Fulcrum's standard public endpoint.

Saving confirms only that Arda stored the token. To verify that Fulcrum accepts it, use
**Test API connection** on the saved credential card. The test performs one read-only quote-list
request and records the result, HTTP status, time, and administrator; it does not import quotes
or change Fulcrum data.

The token requires Fulcrum's **View Quote** permission. The sync combines the quote-reporting endpoint with the quote
detail endpoint so customer/salesperson names, totals, statuses, and the quote custom fields used
by the Estimating Log are refreshed together. Existing Excel-imported values are retained when a
corresponding Fulcrum custom field is absent. Custom-field names can be overridden under
`FulcrumQuoteSync:CustomFields` when tenant labels differ from the defaults in `appsettings.json`.

See [Calculation contract](docs/calculation-contract.md) for the reviewed workbook mappings,
formula sequence, retained source quirks, and regression expectations.
