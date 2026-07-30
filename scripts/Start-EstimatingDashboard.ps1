<#
    Starts the Estimating Dashboard without cmd.exe/start or privileged process queries.
    The script is safe to run repeatedly: a healthy existing instance is reused.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Prevent Start-Process from failing when a managed shell supplies duplicate
# Windows Path variables with different casing.
function Normalize-ProcessPathEnvironment {
    $pathKeys = @(
        [Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Process).Keys |
            Where-Object { $_ -ieq 'Path' }
    )
    if ($pathKeys.Count -lt 2) { return }

    $pathValue = [Environment]::GetEnvironmentVariable(
        'Path',
        [EnvironmentVariableTarget]::Process
    )
    [Environment]::SetEnvironmentVariable(
        'PATH',
        $null,
        [EnvironmentVariableTarget]::Process
    )
    [Environment]::SetEnvironmentVariable(
        'Path',
        $pathValue,
        [EnvironmentVariableTarget]::Process
    )
}

Normalize-ProcessPathEnvironment

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiRoot = Join-Path $repoRoot 'apps\estimating-dashboard\src\EstimatingDashboard.Api'
$projectFile = Join-Path $apiRoot 'EstimatingDashboard.Api.csproj'
$appDll = Join-Path $apiRoot 'bin\Debug\net8.0\EstimatingDashboard.Api.dll'
$logDir = Join-Path $repoRoot 'logs'
$appUrl = 'http://localhost:5160'
$healthUrl = "$appUrl/api/health"

function Test-EstimatingHealth {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $healthUrl -TimeoutSec 2
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Find-DotNet {
    $candidates = @()
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($command) {
        $candidates += $command.Source
    }
    $candidates += @(
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
        (Join-Path $env:LOCALAPPDATA 'CodexDotnetSdk8\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe'
    )

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw '.NET 8 could not be found. Run scripts\Setup-Hub.ps1, then try again.'
}

if (Test-EstimatingHealth) {
    Write-Host "READY $appUrl"
    exit 0
}

if (-not (Test-Path -LiteralPath $projectFile)) {
    throw "Estimating Dashboard project not found at $projectFile"
}

$dotnet = Find-DotNet
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

if (-not (Test-Path -LiteralPath $appDll)) {
    Write-Host 'Building the Estimating Dashboard backend...'
    & $dotnet build $projectFile --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'The Estimating Dashboard backend build failed.'
    }
}

$outLog = Join-Path $logDir 'estimating-dashboard.out.log'
$errLog = Join-Path $logDir 'estimating-dashboard.err.log'

$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$previousUrls = $env:ASPNETCORE_URLS
try {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $appUrl

    $process = Start-Process `
        -FilePath $dotnet `
        -ArgumentList @($appDll) `
        -WorkingDirectory $apiRoot `
        -RedirectStandardOutput $outLog `
        -RedirectStandardError $errLog `
        -WindowStyle Hidden `
        -PassThru
}
finally {
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    $env:ASPNETCORE_URLS = $previousUrls
}

for ($attempt = 0; $attempt -lt 60; $attempt++) {
    Start-Sleep -Milliseconds 250
    if (Test-EstimatingHealth) {
        Write-Host ("READY {0} PID {1}" -f $appUrl, $process.Id)
        exit 0
    }
    if ($process.HasExited) {
        break
    }
}

$details = if (Test-Path -LiteralPath $errLog) {
    (Get-Content -LiteralPath $errLog -Tail 20) -join [Environment]::NewLine
}
else {
    'No error log was produced.'
}

throw "Estimating Dashboard did not become ready. See $errLog.`n$details"
