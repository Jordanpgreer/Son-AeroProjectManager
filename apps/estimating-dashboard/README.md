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

See [Calculation contract](docs/calculation-contract.md) for the reviewed workbook mappings,
formula sequence, retained source quirks, and regression expectations.
