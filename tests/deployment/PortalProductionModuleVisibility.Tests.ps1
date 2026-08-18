[CmdletBinding()]
param(
    [string]$ScriptPath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Configure-PortalProductionModuleVisibility.ps1'
}
if ($PSVersionTable.PSVersion.Major -ne 5) {
    throw "These compatibility tests must run under Windows PowerShell 5.1; current version is $($PSVersionTable.PSVersion)."
}

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $ScriptPath), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Visibility script has syntax errors: $($parseErrors.Message -join '; ')"
}
$source = Get-Content -LiteralPath $ScriptPath -Raw

foreach ($required in @(
    "[ValidateSet('SON-IIS2')]",
    "[ValidateSet('SonAeroPortal')]",
    "'engineering-hub', 'quality-assurance'",
    "'__production-disabled__'",
    'Assert-InteractiveAdministrator',
    'NT AUTHORITY\SYSTEM',
    'Restart-WebAppPool -Name $AppPoolName',
    'Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials',
    '[IO.File]::Replace($temporaryPath, $configurationPath, $backupPath)',
    '[IO.File]::Replace($restorePath, $configurationPath, $rollbackDisplacedPath)',
    "'project-tracker', 'admin-console'",
    'PORTAL_PRODUCTION_MODULES_HIDDEN_AND_VERIFIED',
    'PORTAL_PRODUCTION_MODULES_ALREADY_HIDDEN_AND_VERIFIED',
    'WHATIF_READY_PORTAL_PRODUCTION_MODULE_VISIBILITY'
)) {
    if (-not $source.Contains($required)) { throw "Visibility script is missing required guard: $required" }
}

$functionNames = @('Get-ApplicationMap', 'Get-NormalizedAllowedRoles',
    'Set-HiddenApplicationPolicy', 'Test-HiddenApplicationPolicy',
    'Assert-PortalCatalogVisibility')
$definitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -in $functionNames
}, $true))
if ($definitions.Count -ne $functionNames.Count) {
    throw 'Could not extract every visibility policy function for focused behavior tests.'
}
Invoke-Expression (($definitions | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine)

$configuration = @'
{
  "Portal": {
    "Applications": [
      { "Id": "project-tracker", "AllowedRoles": [] },
      { "Id": "engineering-hub", "AllowedRoles": ["Admin"] },
      { "Id": "estimating-dashboard", "AllowedRoles": [] },
      { "Id": "quality-assurance", "AllowedRoles": [] },
      { "Id": "admin-console", "AllowedRoles": [] }
    ]
  }
}
'@ | ConvertFrom-Json

Set-HiddenApplicationPolicy -Configuration $configuration `
    -ApplicationIds @('engineering-hub', 'quality-assurance') `
    -Sentinel '__production-disabled__'
if (-not (Test-HiddenApplicationPolicy -Configuration $configuration `
        -ApplicationIds @('engineering-hub', 'quality-assurance') `
        -Sentinel '__production-disabled__')) {
    throw 'The hidden-module policy was not recognized after it was applied.'
}
$map = Get-ApplicationMap -Configuration $configuration
foreach ($id in @('engineering-hub', 'quality-assurance')) {
    $roles = @(Get-NormalizedAllowedRoles -Application $map[$id] -ApplicationId $id)
    if ($roles.Count -ne 1 -or $roles[0] -cne '__production-disabled__') {
        throw "The exact hidden role policy was not applied to '$id'."
    }
}
foreach ($id in @('project-tracker', 'estimating-dashboard', 'admin-console')) {
    if (@(Get-NormalizedAllowedRoles -Application $map[$id] -ApplicationId $id).Count -ne 0) {
        throw "The visibility policy changed non-target application '$id'."
    }
}

$visibleCatalog = @(
    [pscustomobject]@{ id = 'project-tracker' },
    [pscustomobject]@{ id = 'estimating-dashboard' },
    [pscustomobject]@{ id = 'admin-console' }
)
Assert-PortalCatalogVisibility -Applications $visibleCatalog `
    -HiddenIds @('engineering-hub', 'quality-assurance') `
    -RequiredVisibleIds @('project-tracker', 'admin-console')
$invalidCatalogCases = @(
    [pscustomobject]@{ Applications = [object[]]@() },
    [pscustomobject]@{ Applications = [object[]]@([pscustomobject]@{ id = 'project-tracker' }) },
    [pscustomobject]@{ Applications = [object[]]@(
        [pscustomobject]@{ id = 'project-tracker' }
        [pscustomobject]@{ id = 'admin-console' }
        [pscustomobject]@{ id = 'engineering-hub' }
    ) }
)
foreach ($invalidCatalogCase in $invalidCatalogCases) {
    $failed = $false
    try {
        Assert-PortalCatalogVisibility -Applications $invalidCatalogCase.Applications `
            -HiddenIds @('engineering-hub', 'quality-assurance') `
            -RequiredVisibleIds @('project-tracker', 'admin-console')
    }
    catch { $failed = $true }
    if (-not $failed) { throw 'An empty, incomplete, or still-visible Portal catalog passed verification.' }
}

foreach ($invalidJson in @(
    '{"Portal":{"Applications":[{"Id":"engineering-hub","AllowedRoles":"Admin"},{"Id":"quality-assurance","AllowedRoles":[]}]}}',
    '{"Portal":{"Applications":[{"Id":"engineering-hub","AllowedRoles":[""]},{"Id":"quality-assurance","AllowedRoles":[]}]}}',
    '{"Portal":{"Applications":[{"Id":"engineering-hub","AllowedRoles":[]},{"Id":"engineering-hub","AllowedRoles":[]},{"Id":"quality-assurance","AllowedRoles":[]}]}}'
)) {
    $failed = $false
    try {
        $invalid = $invalidJson | ConvertFrom-Json
        Set-HiddenApplicationPolicy -Configuration $invalid `
            -ApplicationIds @('engineering-hub', 'quality-assurance') `
            -Sentinel '__production-disabled__'
    }
    catch { $failed = $true }
    if (-not $failed) { throw "An invalid Portal catalog was accepted: $invalidJson" }
}

$replaceTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('sonaero-portal-replace-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $replaceTestRoot -Force | Out-Null
try {
    $activePath = Join-Path $replaceTestRoot 'appsettings.Production.json'
    $preparedPath = Join-Path $replaceTestRoot 'prepared.tmp'
    $backupPath = Join-Path $replaceTestRoot 'prior.backup'
    [IO.File]::WriteAllText($activePath, 'prior')
    [IO.File]::WriteAllText($preparedPath, 'updated')
    [IO.File]::Replace($preparedPath, $activePath, $backupPath)
    if ([IO.File]::ReadAllText($activePath) -cne 'updated' -or
        [IO.File]::ReadAllText($backupPath) -cne 'prior') {
        throw 'Atomic apply did not install the update and retain the exact prior file.'
    }

    $restorePath = Join-Path $replaceTestRoot 'restore.tmp'
    $failedBackupPath = Join-Path $replaceTestRoot 'failed.backup'
    [IO.File]::WriteAllText($restorePath, 'prior')
    [IO.File]::Replace($restorePath, $activePath, $failedBackupPath)
    if ([IO.File]::ReadAllText($activePath) -cne 'prior' -or
        [IO.File]::ReadAllText($failedBackupPath) -cne 'updated') {
        throw 'Atomic rollback did not restore the prior file and retain the displaced update.'
    }
}
finally {
    Remove-Item -LiteralPath $replaceTestRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'PORTAL_PRODUCTION_MODULE_VISIBILITY_TESTS_PASSED'
