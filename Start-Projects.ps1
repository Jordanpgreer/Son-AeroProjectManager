$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$apiRoot = Join-Path $root 'ProjectTrackerApp\src\ProjectTracker.Api'
$projectFile = Join-Path $apiRoot 'ProjectTracker.Api.csproj'
$clientRoot = Join-Path $apiRoot 'ClientApp'
$wwwroot = Join-Path $apiRoot 'wwwroot'
$url = 'http://localhost:5135'

function Test-ProjectsApp {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri "$url/api/projects" -TimeoutSec 2
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Find-DotNet {
    $localSdk = Join-Path $env:LOCALAPPDATA 'CodexDotnetSdk8\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) { return $localSdk }

    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $programFiles = 'C:\Program Files\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $programFiles) { return $programFiles }

    throw 'The .NET 8 SDK is required to launch Projects. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this shortcut again.'
}

function Find-Npm {
    $command = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $command = Get-Command npm -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    throw 'Node.js/npm is required for the first local build. Install Node.js LTS from https://nodejs.org and run this shortcut again.'
}

function Ensure-Frontend {
    $index = Join-Path $wwwroot 'index.html'
    if (Test-Path -LiteralPath $index) { return }

    $npm = Find-Npm
    if (-not (Test-Path -LiteralPath (Join-Path $clientRoot 'node_modules'))) {
        & $npm install --prefix $clientRoot
    }

    & $npm run build --prefix $clientRoot

    $dist = Join-Path $clientRoot 'dist'
    if (Test-Path -LiteralPath $wwwroot) {
        Remove-Item -LiteralPath $wwwroot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
    Copy-Item -Path (Join-Path $dist '*') -Destination $wwwroot -Recurse -Force
}

if (Test-ProjectsApp) {
    Start-Process $url
    exit 0
}

$dotnet = Find-DotNet
Ensure-Frontend

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = $url

Start-Process -FilePath $dotnet -ArgumentList @('run', '--no-launch-profile', '--project', $projectFile) -WorkingDirectory $apiRoot -WindowStyle Hidden

for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    if (Test-ProjectsApp) {
        Start-Process $url
        exit 0
    }
}

Add-Type -AssemblyName PresentationFramework
[System.Windows.MessageBox]::Show("Projects started, but $url did not respond within 20 seconds. Try opening it manually in your browser.", 'Projects', 'OK', 'Warning') | Out-Null
