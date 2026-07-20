param(
    [string]$Configuration = "Release",
    [string]$OutputPath = ".\publish"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnetRoot = Join-Path $env:USERPROFILE ".dotnet"

if (Test-Path (Join-Path $dotnetRoot "dotnet.exe")) {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:PATH = "$dotnetRoot;$dotnetRoot\tools;$env:PATH"
}

Push-Location $root
try {
    dotnet publish ".\src\ProjectTracker.Api\ProjectTracker.Api.csproj" -c $Configuration -o $OutputPath
    Write-Host "Published ProjectTracker to $((Resolve-Path $OutputPath).Path)"
}
finally {
    Pop-Location
}

