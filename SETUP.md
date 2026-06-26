# Projects Local Setup

After cloning or pulling this project on a Windows machine, run this once from the project root:

```powershell
powershell -ExecutionPolicy Bypass -File .\Setup-Projects.ps1
```

This installs missing prerequisites with `winget`, then creates a desktop shortcut named `Projects` using the SON-AERO red icon. The shortcut runs `Start-Projects.ps1`, which starts the local web app at `http://localhost:5135` and opens it in the default browser.

Requirements for a fresh machine:

- .NET 8 SDK
- Node.js LTS with npm, only needed if the frontend has not already been built
