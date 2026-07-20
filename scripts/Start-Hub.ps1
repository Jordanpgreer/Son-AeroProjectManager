<#
    Start-Hub.ps1 — SON-AERO Internal Hub local launcher.

    Starts Project Tracker (http://localhost:5135) and the Portal (http://localhost:5140),
    rebuilding each frontend only when its source changed, waits for both to become healthy,
    then opens the portal homepage. Startup problems are shown in a dialog (not a blank
    window). All generated logs and build stamps are written under <repo>\logs (git-ignored).
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Resolve the repository root from this script's location so it works from any checkout,
# including paths that contain spaces.
$repoRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $repoRoot 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$portalUrl = 'http://localhost:5140'

$apps = @(
    [pscustomobject]@{
        Name       = 'Project Tracker'
        Key        = 'project-tracker'
        ApiRoot    = Join-Path $repoRoot 'apps\project-tracker\src\ProjectTracker.Api'
        Url        = 'http://localhost:5135'
        HealthPath = '/api/projects'
    },
    [pscustomobject]@{
        Name       = 'Portal'
        Key        = 'portal'
        ApiRoot    = Join-Path $repoRoot 'apps\portal\src\Portal.Api'
        Url        = $portalUrl
        HealthPath = '/api/health'
    }
)

$script:npm = $null

function Show-HubMessage([string]$message, [string]$title = 'SON-AERO Hub', [string]$icon = 'Error') {
    try {
        Add-Type -AssemblyName PresentationFramework -ErrorAction Stop
        [System.Windows.MessageBox]::Show($message, $title, 'OK', $icon) | Out-Null
    }
    catch {
        Write-Host ''
        Write-Host $message -ForegroundColor Red
    }
}

function Test-AppHealth($app) {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri ($app.Url + $app.HealthPath) -TimeoutSec 2
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Find-DotNetSdk {
    $candidates = @(
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
        (Join-Path $env:LOCALAPPDATA 'CodexDotnetSdk8\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe'
    )

    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($command) {
        $candidates = @($command.Source) + $candidates
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks -match '^8\.') {
            return $candidate
        }
    }

    throw 'The .NET 8 SDK is required. Run Setup-Hub.ps1 once, or install the .NET 8 SDK and try again.'
}

function Find-Npm {
    foreach ($name in @('npm.cmd', 'npm')) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command) { return $command.Source }
    }
    throw 'Node.js/npm is required for the local frontend build. Run Setup-Hub.ps1 once, or install Node.js LTS and try again.'
}

function Get-SourceStamp($clientRoot) {
    $items = @()
    foreach ($sub in @('src', 'public')) {
        $path = Join-Path $clientRoot $sub
        if (Test-Path -LiteralPath $path) {
            $items += Get-ChildItem -LiteralPath $path -File -Recurse -ErrorAction SilentlyContinue
        }
    }
    foreach ($file in @('package.json', 'package-lock.json', 'index.html', 'vite.config.ts')) {
        $path = Join-Path $clientRoot $file
        if (Test-Path -LiteralPath $path) { $items += Get-Item -LiteralPath $path }
    }
    if ($items.Count -eq 0) { return '0' }
    return (($items | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum.Ticks).ToString()
}

function Ensure-Frontend($app) {
    $clientRoot = Join-Path $app.ApiRoot 'ClientApp'
    $wwwroot = Join-Path $app.ApiRoot 'wwwroot'
    $index = Join-Path $wwwroot 'index.html'
    $stampFile = Join-Path $logDir ("{0}.build-stamp" -f $app.Key)

    $sourceStamp = Get-SourceStamp $clientRoot
    $installedStamp = if (Test-Path -LiteralPath $stampFile) { (Get-Content -LiteralPath $stampFile -Raw).Trim() } else { '' }

    if ((Test-Path -LiteralPath $index) -and $installedStamp -eq $sourceStamp) { return }

    Write-Host ("Preparing the {0} interface..." -f $app.Name)
    if (-not $script:npm) { $script:npm = Find-Npm }

    if (-not (Test-Path -LiteralPath (Join-Path $clientRoot 'node_modules'))) {
        if (Test-Path -LiteralPath (Join-Path $clientRoot 'package-lock.json')) {
            & $script:npm ci --prefix $clientRoot
        }
        else {
            & $script:npm install --prefix $clientRoot
        }
        if ($LASTEXITCODE -ne 0) { throw ("npm dependency installation failed for {0}." -f $app.Name) }
    }

    & $script:npm run build --prefix $clientRoot
    if ($LASTEXITCODE -ne 0) { throw ("The frontend build failed for {0}." -f $app.Name) }

    $dist = Join-Path $clientRoot 'dist'
    if (-not (Test-Path -LiteralPath (Join-Path $dist 'index.html'))) {
        throw ("Frontend output was not created for {0} at {1}." -f $app.Name, $dist)
    }

    $resolvedApiRoot = [System.IO.Path]::GetFullPath($app.ApiRoot)
    if (Test-Path -LiteralPath $wwwroot) {
        $resolvedWwwroot = [System.IO.Path]::GetFullPath($wwwroot)
        if (-not $resolvedWwwroot.StartsWith($resolvedApiRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to replace a frontend folder outside the application directory.'
        }
        Remove-Item -LiteralPath $resolvedWwwroot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
    Copy-Item -Path (Join-Path $dist '*') -Destination $wwwroot -Recurse -Force
    Set-Content -LiteralPath $stampFile -Value $sourceStamp -Encoding ASCII
}

function Get-LaunchFailureDetails($app) {
    $details = @()
    $errLog = Join-Path $logDir ("{0}.err.log" -f $app.Key)
    $outLog = Join-Path $logDir ("{0}.out.log" -f $app.Key)
    if (Test-Path -LiteralPath $errLog) { $details += Get-Content -LiteralPath $errLog -Tail 18 }
    if ($details.Count -eq 0 -and (Test-Path -LiteralPath $outLog)) { $details += Get-Content -LiteralPath $outLog -Tail 18 }
    if ($details.Count -eq 0) { $details += 'No startup log was produced.' }
    return ($details -join [Environment]::NewLine)
}

function Start-App($app, $dotnet) {
    if (Test-AppHealth $app) {
        Write-Host ("{0} is already running." -f $app.Name)
        return
    }

    Ensure-Frontend $app

    $outLog = Join-Path $logDir ("{0}.out.log" -f $app.Key)
    $errLog = Join-Path $logDir ("{0}.err.log" -f $app.Key)
    Remove-Item -LiteralPath $outLog, $errLog -Force -ErrorAction SilentlyContinue

    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $app.Url

    Write-Host ("Starting {0} at {1}..." -f $app.Name, $app.Url)
    # WorkingDirectory keeps clone paths containing spaces valid.
    $process = Start-Process `
        -FilePath $dotnet `
        -ArgumentList @('run', '--no-launch-profile') `
        -WorkingDirectory $app.ApiRoot `
        -RedirectStandardOutput $outLog `
        -RedirectStandardError $errLog `
        -WindowStyle Hidden `
        -PassThru

    for ($i = 0; $i -lt 240; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-AppHealth $app) {
            Write-Host ("{0} is ready." -f $app.Name)
            return
        }
        if ($process.HasExited) { break }
    }

    $failure = Get-LaunchFailureDetails $app
    throw ("{0} could not start at {1}.`n`n{2}`n`nSee logs\{3}.err.log for details." -f $app.Name, $app.Url, $failure, $app.Key)
}

try {
    Write-Host 'Starting SON-AERO Hub...'

    $dotnet = Find-DotNetSdk
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet

    foreach ($app in $apps) {
        Start-App $app $dotnet
    }

    Start-Process $portalUrl
    Write-Host 'SON-AERO Hub is running.'
    exit 0
}
catch {
    Show-HubMessage $_.Exception.Message
    exit 1
}
