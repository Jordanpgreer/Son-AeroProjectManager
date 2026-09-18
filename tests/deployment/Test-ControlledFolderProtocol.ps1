[CmdletBinding()]
param([string]$ModulePath = '')

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ModulePath)) {
    $ModulePath = Join-Path $PSScriptRoot '..\..\scripts\ControlledFolder.Protocol.psm1'
}
$resolvedModulePath = (Resolve-Path -LiteralPath $ModulePath).Path
Import-Module $resolvedModulePath -Force -ErrorAction Stop

function Assert-Equal {
    param([string]$Actual, [string]$Expected, [string]$Message)
    if (-not $Actual.Equals($Expected, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Message Expected '$Expected'; received '$Actual'."
    }
}

function Assert-Rejected {
    param([string]$Uri, [string]$Message)
    try {
        $result = Resolve-SonAeroControlledPath -Uri $Uri
        throw "$Message Request unexpectedly resolved to '$result'."
    }
    catch {
        if ($_.Exception.Message.StartsWith($Message)) { throw }
    }
}

$allowed = @(
    @('sonaero-folder://open?path=Engineering%5CProgram%20A', 'S:\Engineering\Program A'),
    @('sonaero-folder://open?path=%5CEngineering%5CDrawings', 'S:\Engineering\Drawings'),
    @('sonaero-folder://open?path=S%3A%5CEngineering%5CDrawings', 'S:\Engineering\Drawings'),
    @('SONAERO-FOLDER://OPEN?path=Quotes%2F2026%2FQ-1001', 'S:\Quotes\2026\Q-1001'),
    @('sonaero-folder://open?path=Programs%5CA%2BB', 'S:\Programs\A+B')
)
foreach ($case in $allowed) {
    Assert-Equal `
        (Resolve-SonAeroControlledPath -Uri $case[0]) `
        ([System.IO.Path]::GetFullPath($case[1])) `
        "Approved controlled path did not normalize correctly."
}

$rejected = @(
    'https://open?path=Engineering%5CDrawings',
    'sonaero-folder://delete?path=Engineering%5CDrawings',
    'sonaero-folder://user@open?path=Engineering%5CDrawings',
    'sonaero-folder://open:80?path=Engineering%5CDrawings',
    'sonaero-folder://open/extra?path=Engineering%5CDrawings',
    'sonaero-folder://open?path=C%3A%5CWindows',
    'sonaero-folder://open?path=C%3AWindows',
    'sonaero-folder://open?path=S%3AEngineering%5CDrawings',
    'sonaero-folder://open?path=%5C%5Cserver%5Cshare%5Cfolder',
    'sonaero-folder://open?path=%5C%5C%3F%5CC%3A%5CWindows',
    'sonaero-folder://open?path=Engineering%5C..%5CPayroll',
    'sonaero-folder://open?path=S%3A%5CEngineering%5C..%5CPayroll',
    'sonaero-folder://open?path=file%3A%2F%2F%2Fetc',
    'sonaero-folder://open?path=https%3A%2F%2Fexample.com',
    'sonaero-folder://open?path=Engineering%5Cdrawing.pdf%3Asecret',
    'sonaero-folder://open?path=Engineering%5C.%5CDrawings',
    'sonaero-folder://open?path=Engineering%5CDrawings&path=S%3A%5CPayroll',
    'sonaero-folder://open?path=Engineering%5CDrawings&other=value',
    'sonaero-folder://open?path=',
    'sonaero-folder://open?path=Engineering%5CDrawings%23fragment#ignored'
)
foreach ($uri in $rejected) {
    Assert-Rejected -Uri $uri -Message 'Unsafe controlled-folder URI was accepted.'
}

$folderTarget = New-SonAeroControlledExplorerTarget `
    -TargetPath 'S:\Engineering\Drawings' `
    -IsContainer
Assert-Equal $folderTarget.Kind 'Folder' 'Folder targets must open directly.'
Assert-Equal $folderTarget.TargetPath 'S:\Engineering\Drawings' 'Folder target path changed.'
Assert-Equal `
    $folderTarget.ExplorerArguments[0] `
    '"S:\Engineering\Drawings"' `
    'Folder Explorer arguments changed.'

$fileTarget = New-SonAeroControlledExplorerTarget `
    -TargetPath 'S:\Engineering\Drawings\drawing.pdf' `
    -ContainingFolder 'S:\Engineering\Drawings'
Assert-Equal $fileTarget.Kind 'File' 'File targets must be selected in Explorer.'
Assert-Equal $fileTarget.TargetPath 'S:\Engineering\Drawings\drawing.pdf' 'File target path changed.'
Assert-Equal `
    $fileTarget.ExplorerArguments[0] `
    '/select,"S:\Engineering\Drawings\drawing.pdf"' `
    'File Explorer selection arguments changed.'

try {
    New-SonAeroControlledExplorerTarget `
        -TargetPath 'S:\Engineering\Drawings\drawing.pdf' `
        -ContainingFolder 'C:\Windows' | Out-Null
    throw 'A file with an uncontrolled containing folder was accepted.'
}
catch {
    if ($_.Exception.Message -eq 'A file with an uncontrolled containing folder was accepted.') {
        throw
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$handlerSource = Get-Content -LiteralPath (Join-Path $repoRoot 'scripts\Open-ControlledFolder.ps1') -Raw
$moduleSource = Get-Content -LiteralPath (Join-Path $repoRoot 'scripts\ControlledFolder.Protocol.psm1') -Raw
$installerSource = Get-Content -LiteralPath (Join-Path $repoRoot 'scripts\Install-OpenFolderProtocol.ps1') -Raw
$setupSource = Get-Content -LiteralPath (Join-Path $repoRoot 'scripts\Setup-Hub.ps1') -Raw
$startSource = Get-Content -LiteralPath (Join-Path $repoRoot 'scripts\Start-Hub.ps1') -Raw

if (-not $handlerSource.Contains('Resolve-SonAeroControlledPath')) {
    throw 'The installed handler does not use the controlled S:\ path resolver.'
}
if (-not $handlerSource.Contains('Get-SonAeroControlledOpenTarget')) {
    throw 'The installed handler does not distinguish folder and file targets.'
}
if (-not $moduleSource.Contains('/select,')) {
    throw 'The handler does not select a requested file in its containing folder.'
}
if (-not $installerSource.Contains('ControlledFolder.Protocol.psm1')) {
    throw 'Protocol registration does not verify the controlled-folder validation module.'
}
if (-not $setupSource.Contains('Install-OpenFolderProtocol.ps1')) {
    throw 'First-time Hub setup no longer registers the controlled-folder protocol.'
}
if (-not $startSource.Contains('Install-OpenFolderProtocol.ps1')) {
    throw 'Hub startup no longer refreshes the controlled-folder protocol registration.'
}

Write-Output 'CONTROLLED_FOLDER_PROTOCOL_TESTS_PASSED'
