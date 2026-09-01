[CmdletBinding()]
param(
    [string]$ShortcutScriptPath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ShortcutScriptPath)) {
    $ShortcutScriptPath = Join-Path $PSScriptRoot '..\..\scripts\Install-HubShortcut.ps1'
}
$shortcutScript = (Resolve-Path -LiteralPath $ShortcutScriptPath).Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $shortcutScript) '..'))
$launcher = Join-Path $repoRoot 'scripts\Start-Hub.vbs'
$expectedIcon = Join-Path $repoRoot 'shared\branding\arda-transparent.ico'
$expectedTarget = [IO.Path]::GetFullPath("$env:SystemRoot\System32\wscript.exe")

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('arda-shortcut-test-' + [Guid]::NewGuid().ToString('N'))
$desktop = Join-Path $testRoot 'Desktop'
[IO.Directory]::CreateDirectory($desktop) | Out-Null
try {
    $shell = New-Object -ComObject WScript.Shell
    $legacyPaths = @(
        (Join-Path $desktop 'Arda Hub.lnk'),
        (Join-Path $desktop 'SON-AERO Hub.lnk')
    )
    foreach ($legacyPath in $legacyPaths) {
        $legacy = $shell.CreateShortcut($legacyPath)
        $legacy.TargetPath = $expectedTarget
        $legacy.Arguments = "`"$launcher`""
        $legacy.WorkingDirectory = $repoRoot
        $legacy.Save()
    }
    $projectsPath = Join-Path $desktop 'Projects.lnk'
    $projects = $shell.CreateShortcut($projectsPath)
    $projects.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $projects.Arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$(Join-Path $repoRoot 'Start-Projects.ps1')`""
    $projects.WorkingDirectory = $repoRoot
    $projects.Save()

    & $shortcutScript -DesktopPath $desktop | Out-Null

    $ardaPath = Join-Path $desktop 'Arda.lnk'
    Assert-True (Test-Path -LiteralPath $ardaPath -PathType Leaf) `
        'The local installer did not create Arda.lnk.'
    foreach ($legacyPath in @($legacyPaths) + $projectsPath) {
        Assert-True (-not (Test-Path -LiteralPath $legacyPath -PathType Leaf)) `
            "The local installer did not remove its verified legacy shortcut: $legacyPath"
    }

    $arda = $shell.CreateShortcut($ardaPath)
    Assert-True ([IO.Path]::GetFullPath($arda.TargetPath) -ieq $expectedTarget) `
        'The Arda shortcut changed the existing wscript launcher target.'
    Assert-True ($arda.Arguments -ceq "`"$launcher`"") `
        'The Arda shortcut changed the quoted Start-Hub.vbs arguments.'
    Assert-True ([IO.Path]::GetFullPath($arda.WorkingDirectory).TrimEnd('\') -ieq $repoRoot.TrimEnd('\')) `
        'The Arda shortcut changed the repository working directory.'
    $actualIcon = ($arda.IconLocation -split ',')[0].Trim('"')
    Assert-True ([IO.Path]::GetFullPath($actualIcon) -ieq [IO.Path]::GetFullPath($expectedIcon)) `
        'The Arda shortcut does not use the official Arda Windows icon.'
    Assert-True ($arda.Description -ceq 'Open Arda applications') `
        'The Arda shortcut description is not application-branded.'

    foreach ($legacyPath in @($legacyPaths) + $projectsPath) {
        $unrelated = $shell.CreateShortcut($legacyPath)
        $unrelated.TargetPath = "$env:SystemRoot\System32\notepad.exe"
        $unrelated.WorkingDirectory = $env:SystemRoot
        $unrelated.Save()
    }
    & $shortcutScript -DesktopPath $desktop -WarningAction SilentlyContinue | Out-Null
    foreach ($legacyPath in @($legacyPaths) + $projectsPath) {
        Assert-True (Test-Path -LiteralPath $legacyPath -PathType Leaf) `
            "The local installer removed an unrelated same-named shortcut: $legacyPath"
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Output 'ARDA_HUB_SHORTCUT_TESTS_PASSED'
