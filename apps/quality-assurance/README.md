# Quality Assurance

Admin-only SON-AERO quality workspace with an ASP.NET Core 8 host and React/Vite client.

The initial release contains the secured application shell and Dashboard only. Access is granted
through shared groups in the centralized Access screen. For now, only groups with the
`quality-assurance.view` permission can open the module, and that permission is assigned to the
Administrators group by default.

## Local development

The desktop Hub launcher starts the module at `http://localhost:5170` and opens it from the
application catalog. The service uses the shared Project Tracker development database for access
control.

```powershell
dotnet run --project apps/quality-assurance/src/QualityAssurance.Api/QualityAssurance.Api.csproj
```
