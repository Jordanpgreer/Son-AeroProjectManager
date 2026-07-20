<#
    Setup-Hub.ps1 — first-time dependency setup for the SON-AERO Internal Hub.

    Installs the .NET 8 SDK and Node.js LTS with winget if they are missing, then creates the
    "SON-AERO Hub" desktop shortcut. Run this once per machine.
#>
$ErrorActionPreference = 'Stop'

function Refresh-Path {
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machinePath;$userPath"
}

function Test-Command($name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

function Install-WingetPackage($id, $name) {
    if (-not (Test-Command winget.exe)) {
        throw "winget is required to install $name automatically. Install $name manually, then rerun this script."
    }

    Write-Host "Installing $name..."
    winget install --id $id --exact --silent --accept-package-agreements --accept-source-agreements
    Refresh-Path
}

Refresh-Path

if (-not (Test-Command dotnet.exe) -or -not ((dotnet --list-sdks) -match '^8\.')) {
    Install-WingetPackage 'Microsoft.DotNet.SDK.8' '.NET 8 SDK'
}

if (-not (Test-Command node.exe) -or -not (Test-Command npm.cmd)) {
    Install-WingetPackage 'OpenJS.NodeJS.LTS' 'Node.js LTS'
}

& (Join-Path $PSScriptRoot 'Install-HubShortcut.ps1')

Write-Host ''
Write-Host 'SON-AERO Hub setup complete. Use the "SON-AERO Hub" desktop shortcut to launch the hub.'
