<#
    Install-HubShortcut.ps1

    Creates an "Arda Hub" desktop shortcut that launches scripts\Start-Hub.vbs using the
    official Arda application icon. Works for any user on any checkout — every path is resolved relative to
    this script, so it does not depend on a specific username or absolute folder, and tolerates
    paths that contain spaces.
#>
[CmdletBinding()]
param(
    [string]$DesktopPath = ''
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$launcher = Join-Path $scriptDir 'Start-Hub.vbs'
$icon = Join-Path $repoRoot 'shared\branding\arda.ico'
$desktop = if ([string]::IsNullOrWhiteSpace($DesktopPath)) {
    [Environment]::GetFolderPath('Desktop')
} else {
    [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($DesktopPath))
}
if ([string]::IsNullOrWhiteSpace($desktop) -or
    -not (Test-Path -LiteralPath $desktop -PathType Container)) {
    throw "Desktop directory not found: $desktop"
}
$shortcutPath = Join-Path $desktop 'Arda Hub.lnk'
$legacyShortcutPath = Join-Path $desktop 'SON-AERO Hub.lnk'

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "Launcher script not found: $launcher"
}

if (-not (Test-Path -LiteralPath $icon)) {
    throw "Shortcut icon not found: $icon"
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = "$env:SystemRoot\System32\wscript.exe"
$shortcut.Arguments = "`"$launcher`""
$shortcut.WorkingDirectory = $repoRoot
$shortcut.IconLocation = $icon
$shortcut.Description = 'Open Arda applications'
$shortcut.Save()

# Remove only the legacy shortcut created by this repository. A same-named shortcut with a
# different target, arguments, or working directory is user-owned and must be preserved.
if (Test-Path -LiteralPath $legacyShortcutPath -PathType Leaf) {
    $legacyShortcut = $shell.CreateShortcut($legacyShortcutPath)
    $expectedTarget = [IO.Path]::GetFullPath("$env:SystemRoot\System32\wscript.exe")
    $legacyTarget = if ([string]::IsNullOrWhiteSpace($legacyShortcut.TargetPath)) {
        ''
    } else {
        [IO.Path]::GetFullPath($legacyShortcut.TargetPath)
    }
    $legacyWorkingDirectory = if ([string]::IsNullOrWhiteSpace($legacyShortcut.WorkingDirectory)) {
        ''
    } else {
        [IO.Path]::GetFullPath($legacyShortcut.WorkingDirectory).TrimEnd('\')
    }
    if ($legacyTarget -ieq $expectedTarget -and
        $legacyShortcut.Arguments -ceq "`"$launcher`"" -and
        $legacyWorkingDirectory -ieq ([IO.Path]::GetFullPath($repoRoot).TrimEnd('\'))) {
        Remove-Item -LiteralPath $legacyShortcutPath -Force
        Write-Host "Removed legacy desktop shortcut: $legacyShortcutPath"
    } else {
        Write-Warning "Preserved same-named legacy shortcut because it does not launch this checkout: $legacyShortcutPath"
    }
}

Write-Host "Created desktop shortcut: $shortcutPath"
