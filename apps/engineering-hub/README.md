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

## Drawing file storage and Design Authorities

Drawing PDFs and original source files are not stored in the application database. Production starts with a UNC network share configured through `DrawingStorage__RootPath`, for example:

```powershell
$env:DrawingStorage__RootPath = '\\fileserver\Engineering\Controlled Drawings'
```

Administrators can verify or change this root from **Hub Admin > Engineering > File Storage**. The saved setting is shared by Portal and Engineering Hub and overrides the deployment default without requiring an application restart. Use the UNC path behind a mapped drive such as `Q:` because IIS and Windows services do not reliably receive interactive drive mappings.

Each immediate child folder is an approved Design Authority. Creating a Design Authority in settings creates that folder; drawing create/edit forms only accept indexed folders. Every upload creates a new authority/drawing/revision package and never overwrites an existing package. The database stores only relative paths, size, type, and SHA-256 hash. When the active root changes, prior roots remain read fallbacks for existing packages while new uploads use the active root.

Run both the Engineering Hub and Portal application pools under identities with Modify access to the share because Engineering Hub writes drawing packages and Portal indexes/creates authority folders. Ordinary users should not have write access to the share, or they could bypass revision and audit controls. The local `engineering-files` directory is enabled only by the development configuration.
