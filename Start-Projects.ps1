$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$apiRoot = Join-Path $root 'ProjectTrackerApp\src\ProjectTracker.Api'
$clientRoot = Join-Path $apiRoot 'ClientApp'
$wwwroot = Join-Path $apiRoot 'wwwroot'
$buildStamp = Join-Path $root '.projects-build-stamp'
$stdoutLog = Join-Path $root 'projects-launch.out.log'
$stderrLog = Join-Path $root 'projects-launch.err.log'
$url = 'http://localhost:5135'

function Show-ProjectsMessage([string]$message, [string]$title = 'Projects', [string]$icon = 'Error') {
    try {
        Add-Type -AssemblyName PresentationFramework -ErrorAction Stop
        [System.Windows.MessageBox]::Show($message, $title, 'OK', $icon) | Out-Null
    }
    catch {
        Write-Host ''
        Write-Host $message -ForegroundColor Red
    }
}

function Test-ProjectsApp {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri "$url/api/projects" -TimeoutSec 2
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

    throw 'The .NET 8 SDK is required. Run Setup-Projects.ps1 once, or install the .NET 8 SDK and try again.'
}

function Find-Npm {
    foreach ($name in @('npm.cmd', 'npm')) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command) { return $command.Source }
    }
    throw 'Node.js/npm is required for the local frontend build. Run Setup-Projects.ps1 once, or install Node.js LTS and try again.'
}

function Get-SourceStamp {
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($git) {
        $commit = & $git.Source -C $root rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $commit) { return $commit.Trim() }
    }

    $sourceFiles = Get-ChildItem -Path (Join-Path $clientRoot 'src'), (Join-Path $clientRoot 'public') -File -Recurse -ErrorAction SilentlyContinue
    $packageFiles = Get-Item (Join-Path $clientRoot 'package.json'), (Join-Path $clientRoot 'package-lock.json') -ErrorAction SilentlyContinue
    return (($sourceFiles + $packageFiles | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum.Ticks).ToString()
}

function Ensure-Frontend {
    $index = Join-Path $wwwroot 'index.html'
    $sourceStamp = Get-SourceStamp
    $installedStamp = if (Test-Path -LiteralPath $buildStamp) { (Get-Content -LiteralPath $buildStamp -Raw).Trim() } else { '' }

    if ((Test-Path -LiteralPath $index) -and $installedStamp -eq $sourceStamp) { return }

    Write-Host 'Preparing the Projects interface...'
    $npm = Find-Npm
    if (-not (Test-Path -LiteralPath (Join-Path $clientRoot 'node_modules'))) {
        if (Test-Path -LiteralPath (Join-Path $clientRoot 'package-lock.json')) {
            & $npm ci --prefix $clientRoot
        }
        else {
            & $npm install --prefix $clientRoot
        }
        if ($LASTEXITCODE -ne 0) { throw 'npm dependency installation failed.' }
    }

    & $npm run build --prefix $clientRoot
    if ($LASTEXITCODE -ne 0) { throw 'The frontend build failed.' }

    $dist = Join-Path $clientRoot 'dist'
    if (-not (Test-Path -LiteralPath (Join-Path $dist 'index.html'))) {
        throw "Frontend output was not created at $dist."
    }

    if (Test-Path -LiteralPath $wwwroot) {
        $resolvedWwwroot = [System.IO.Path]::GetFullPath($wwwroot)
        $resolvedApiRoot = [System.IO.Path]::GetFullPath($apiRoot)
        if (-not $resolvedWwwroot.StartsWith($resolvedApiRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to replace a frontend folder outside the application directory.'
        }
        Remove-Item -LiteralPath $resolvedWwwroot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
    Copy-Item -Path (Join-Path $dist '*') -Destination $wwwroot -Recurse -Force
    Set-Content -LiteralPath $buildStamp -Value $sourceStamp -Encoding ASCII
}

function Get-LaunchFailureDetails {
    $details = @()
    if (Test-Path -LiteralPath $stderrLog) {
        $details += Get-Content -LiteralPath $stderrLog -Tail 18
    }
    if ($details.Count -eq 0 -and (Test-Path -LiteralPath $stdoutLog)) {
        $details += Get-Content -LiteralPath $stdoutLog -Tail 18
    }
    if ($details.Count -eq 0) {
        $details += 'No startup log was produced.'
    }
    return ($details -join [Environment]::NewLine)
}

try {
    Write-Host 'Starting SON-AERO Projects...'
    if (Test-ProjectsApp) {
        Start-Process $url
        exit 0
    }

    $dotnet = Find-DotNetSdk
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    Ensure-Frontend

    Remove-Item -LiteralPath $stdoutLog, $stderrLog -Force -ErrorAction SilentlyContinue
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $url

    Write-Host 'Starting the local service...'
    # The project path is represented by WorkingDirectory so clone paths containing spaces remain valid.
    $process = Start-Process `
        -FilePath $dotnet `
        -ArgumentList @('run', '--no-launch-profile') `
        -WorkingDirectory $apiRoot `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog `
        -WindowStyle Hidden `
        -PassThru

    for ($i = 0; $i -lt 120; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-ProjectsApp) {
            Start-Process $url
            exit 0
        }
        if ($process.HasExited) { break }
    }

    $failure = Get-LaunchFailureDetails
    throw "Projects could not start at $url.`n`n$failure`n`nSee projects-launch.err.log in the project folder for details."
}
catch {
    Show-ProjectsMessage $_.Exception.Message
    exit 1
}
