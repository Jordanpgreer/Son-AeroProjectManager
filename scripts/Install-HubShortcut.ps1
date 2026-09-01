<#
    Install-HubShortcut.ps1

    Creates an "Arda" desktop shortcut that launches scripts\Start-Hub.vbs using the
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
$icon = Join-Path $repoRoot 'shared\branding\arda-transparent.ico'
$desktop = if ([string]::IsNullOrWhiteSpace($DesktopPath)) {
    [Environment]::GetFolderPath('Desktop')
} else {
    [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($DesktopPath))
}
if ([string]::IsNullOrWhiteSpace($desktop) -or
    -not (Test-Path -LiteralPath $desktop -PathType Container)) {
    throw "Desktop directory not found: $desktop"
}
$shortcutPath = Join-Path $desktop 'Arda.lnk'
$expectedWscript = [IO.Path]::GetFullPath("$env:SystemRoot\System32\wscript.exe")
$expectedPowerShell = [IO.Path]::GetFullPath("$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe")
$legacyProjectsLauncher = Join-Path $repoRoot 'Start-Projects.ps1'
$legacyShortcutDefinitions = @(
    [pscustomobject]@{
        Path = Join-Path $desktop 'Arda Hub.lnk'
        Target = $expectedWscript
        Arguments = "`"$launcher`""
    },
    [pscustomobject]@{
        Path = Join-Path $desktop 'SON-AERO Hub.lnk'
        Target = $expectedWscript
        Arguments = "`"$launcher`""
    },
    [pscustomobject]@{
        Path = Join-Path $desktop 'Projects.lnk'
        Target = $expectedPowerShell
        Arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$legacyProjectsLauncher`""
    }
)

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

# Remove only legacy shortcuts created by this repository. Same-named shortcuts with a
# different target, arguments, or working directory are user-owned and must be preserved.
foreach ($legacyDefinition in $legacyShortcutDefinitions) {
    if (Test-Path -LiteralPath $legacyDefinition.Path -PathType Leaf) {
        $legacyShortcut = $shell.CreateShortcut($legacyDefinition.Path)
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
        if ($legacyTarget -ieq $legacyDefinition.Target -and
            $legacyShortcut.Arguments -ceq $legacyDefinition.Arguments -and
            $legacyWorkingDirectory -ieq ([IO.Path]::GetFullPath($repoRoot).TrimEnd('\'))) {
            Remove-Item -LiteralPath $legacyDefinition.Path -Force
            Write-Host "Removed legacy desktop shortcut: $($legacyDefinition.Path)"
        } else {
            Write-Warning "Preserved same-named legacy shortcut because it does not launch this checkout: $($legacyDefinition.Path)"
        }
    }
}

Write-Host "Created desktop shortcut: $shortcutPath"
