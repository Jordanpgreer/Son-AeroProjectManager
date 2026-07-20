<#
    Install-HubShortcut.ps1

    Creates a "SON-AERO Hub" desktop shortcut that launches scripts\Start-Hub.ps1 using the
    red SON-AERO icon. Works for any user on any checkout — every path is resolved relative to
    this script, so it does not depend on a specific username or absolute folder, and tolerates
    paths that contain spaces.
#>
$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$launcher = Join-Path $scriptDir 'Start-Hub.ps1'
$icon = Join-Path $repoRoot 'shared\branding\son-aero.ico'
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'SON-AERO Hub.lnk'

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "Launcher script not found: $launcher"
}

if (-not (Test-Path -LiteralPath $icon)) {
    throw "Shortcut icon not found: $icon"
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$shortcut.Arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$launcher`""
$shortcut.WorkingDirectory = $repoRoot
$shortcut.IconLocation = $icon
$shortcut.Description = 'Launch the SON-AERO Internal Hub'
$shortcut.Save()

Write-Host "Created desktop shortcut: $shortcutPath"
