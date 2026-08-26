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
    "[ValidateSet('https://engineering.hub.son4l.local/api/health')]",
    "[ValidateSet('https://quality.hub.son4l.local/api/health')]",
    "`$activatedApplicationIds = @('engineering-hub', 'quality-assurance')",
    'Assert-InteractiveAdministrator',
    'NT AUTHORITY\SYSTEM',
    "`$identity.Name -notlike 'SON4L\*'",
    'Restart-WebAppPool -Name $AppPoolName',
    'Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials',
    '[IO.File]::Replace($temporaryPath, $configurationPath, $backupPath)',
    '[IO.File]::Replace($restorePath, $configurationPath, $rollbackDisplacedPath)',
    "'engineering-hub'",
    "'estimating-dashboard'",
    "'quality-assurance'",
    'PORTAL_PRODUCTION_MODULE_POLICY_APPLIED_AND_VERIFIED',
    'PORTAL_PRODUCTION_MODULE_POLICY_ALREADY_APPLIED_AND_VERIFIED',
    'WHATIF_READY_PORTAL_PRODUCTION_MODULE_VISIBILITY'
)) {
    if (-not $source.Contains($required)) { throw "Visibility script is missing required guard: $required" }
}

$functionNames = @('Get-ApplicationMap', 'Get-NormalizedAllowedRoles',
    'Set-VisibleApplicationPolicy', 'Test-VisibleApplicationPolicy',
    'Assert-PortalCatalogVisibility', 'ConvertFrom-PortalApplicationsJson',
    'Wait-ForModuleHealth')
$definitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -in $functionNames
}, $true))
if ($definitions.Count -ne $functionNames.Count) {
    throw 'Could not extract every visibility policy function for focused behavior tests.'
}
Invoke-Expression (($definitions | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine)

$engineeringHealthIndex = $source.IndexOf(
    "Wait-ForModuleHealth -ModuleName 'Engineering Hub' -Uri `$EngineeringHealthUri")
$qualityHealthIndex = $source.IndexOf(
    "Wait-ForModuleHealth -ModuleName 'Quality Assurance' -Uri `$QualityHealthUri")
$configurationReadIndex = $source.IndexOf('$originalBytes = [IO.File]::ReadAllBytes($configurationPath)')
if ($engineeringHealthIndex -lt 0 -or $qualityHealthIndex -lt 0 -or
    $configurationReadIndex -lt 0 -or
    $engineeringHealthIndex -gt $qualityHealthIndex -or
    $qualityHealthIndex -gt $configurationReadIndex) {
    throw 'Engineering and Quality health are not both required before the Portal configuration transaction begins.'
}

$configuration = @'
{
  "Portal": {
    "Applications": [
      { "Id": "project-tracker", "AllowedRoles": [] },
      { "Id": "engineering-hub", "AllowedRoles": ["Admin"] },
      { "Id": "estimating-dashboard", "AllowedRoles": [] },
      { "Id": "quality-assurance", "AllowedRoles": ["__production-disabled__"] },
      { "Id": "admin-console", "AllowedRoles": [] }
    ]
  }
}
'@ | ConvertFrom-Json

Set-VisibleApplicationPolicy -Configuration $configuration `
    -ApplicationIds @('engineering-hub', 'quality-assurance')
if (-not (Test-VisibleApplicationPolicy -Configuration $configuration `
        -ApplicationIds @('engineering-hub', 'quality-assurance'))) {
    throw 'The activated-module policy was not recognized after it was applied.'
}
$map = Get-ApplicationMap -Configuration $configuration
foreach ($id in @('project-tracker', 'engineering-hub', 'estimating-dashboard', 'quality-assurance', 'admin-console')) {
    if (@(Get-NormalizedAllowedRoles -Application $map[$id] -ApplicationId $id).Count -ne 0) {
        throw "The visibility policy did not leave '$id' visible."
    }
}

$visibleCatalog = @(
    [pscustomobject]@{ id = 'project-tracker' },
    [pscustomobject]@{ id = 'engineering-hub' },
    [pscustomobject]@{ id = 'estimating-dashboard' },
    [pscustomobject]@{ id = 'quality-assurance' },
    [pscustomobject]@{ id = 'admin-console' }
)
Assert-PortalCatalogVisibility -Applications $visibleCatalog `
    -RequiredVisibleIds @('project-tracker', 'engineering-hub', 'estimating-dashboard', 'quality-assurance', 'admin-console')

$windowsPowerShellJson = '[{"id":"project-tracker","name":"Project Tracker"},{"id":"engineering-hub","name":"Engineering Hub"},{"id":"estimating-dashboard","name":"Estimating Dashboard"},{"id":"quality-assurance","name":"Quality Assurance"},{"id":"admin-console","name":"Admin Console"}]'
$parsedApplications = @(ConvertFrom-PortalApplicationsJson -Json $windowsPowerShellJson)
if ($parsedApplications.Count -ne 5 -or
    $parsedApplications[0].id -cne 'project-tracker' -or
    $parsedApplications[1].id -cne 'engineering-hub' -or
    $parsedApplications[2].id -cne 'estimating-dashboard' -or
    $parsedApplications[3].id -cne 'quality-assurance' -or
    $parsedApplications[4].id -cne 'admin-console') {
    throw 'Windows PowerShell 5.1 JSON-array parsing did not return five independent applications.'
}
Assert-PortalCatalogVisibility -Applications $parsedApplications `
    -RequiredVisibleIds @('project-tracker', 'engineering-hub', 'estimating-dashboard', 'quality-assurance', 'admin-console')
$invalidCatalogCases = @(
    [pscustomobject]@{ Applications = [object[]]@() },
    [pscustomobject]@{ Applications = [object[]]@([pscustomobject]@{ id = 'project-tracker' }) },
    [pscustomobject]@{ Applications = [object[]]@(
        [pscustomobject]@{ id = 'project-tracker' }
        [pscustomobject]@{ id = 'engineering-hub' }
        [pscustomobject]@{ id = 'estimating-dashboard' }
        [pscustomobject]@{ id = 'admin-console' }
    ) }
)
foreach ($invalidCatalogCase in $invalidCatalogCases) {
    $failed = $false
    try {
        Assert-PortalCatalogVisibility -Applications $invalidCatalogCase.Applications `
            -RequiredVisibleIds @('project-tracker', 'engineering-hub', 'estimating-dashboard', 'quality-assurance', 'admin-console')
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
        Set-VisibleApplicationPolicy -Configuration $invalid `
            -ApplicationIds @('engineering-hub', 'quality-assurance')
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
