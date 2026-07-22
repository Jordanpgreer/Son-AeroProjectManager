# Engineering Hub

Standalone engineering module for SON-AERO's internal hub.

## Current scope

- Admin-only during testing.
- Separate application boundary from Project Tracker.
- Initial module pages:
  - Drawing and document control
  - Tooling management
  - Compound and test-data management

## Local development

```powershell
cd apps\engineering-hub\src\EngineeringHub.Api\ClientApp
npm install
npm run build
cd ..
dotnet run --launch-profile http
```

Open `http://localhost:5150`.

## Drawing file storage

Drawing PDFs and original source files are not stored in the application database. Production uses a UNC network share configured with `DrawingStorage__RootPath`, for example:

```powershell
$env:DrawingStorage__RootPath = '\\fileserver\Engineering\Controlled Drawings'
```

The application stores only the relative path, size, type, and SHA-256 hash. Every upload creates a new customer/drawing/revision package and never overwrites an existing package. Users view and download files through authenticated application endpoints, so they do not need direct access to the share.

Run the application service under a dedicated domain identity with Modify access to the share. Ordinary users should not have write access to the share, or they could bypass the application's revision and audit controls. The local `engineering-files` directory is enabled only by the development configuration.
