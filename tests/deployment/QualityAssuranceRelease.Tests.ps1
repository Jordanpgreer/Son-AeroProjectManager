[CmdletBinding()]
param(
    [string]$ScriptPath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Deploy-QualityAssuranceRelease.ps1'
}
if ($PSVersionTable.PSVersion.Major -ne 5) {
    throw "These compatibility tests must run under Windows PowerShell 5.1; current version is $($PSVersionTable.PSVersion)."
}

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $ScriptPath), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Quality release script has syntax errors: $($parseErrors.Message -join '; ')"
}
$source = Get-Content -LiteralPath $ScriptPath -Raw

foreach ($required in @(
    '[switch]$FirstActivation',
    '[switch]$RepairMissingProductionDatabaseSettings',
    '[switch]$UseServerLocalSqlite',
    "[ValidateSet('SON-IIS2')]",
    "`$siteName = 'QualityAssurance'",
    "`$poolName = 'QualityAssurance'",
    "`$mainDll = 'QualityAssurance.Api.dll'",
    "`$healthUri = 'https://quality.hub.son4l.local/api/health'",
    "`$httpPort = 5170",
    'NT AUTHORITY\SYSTEM',
    'appsettings.Development*.json',
    'appsettings.Production.json',
    'Get-FileHash -Algorithm SHA256',
    'IIS AppPool\$poolName',
    'Restore-PriorQualityRuntime',
    'QualityAssuranceProductionConfiguration.psm1',
    'Import-Module $configurationModule -Force -ErrorAction Stop',
    'New-QualityProductionDatabaseConfigurationRepair',
    'New-QualityServerLocalSqliteConfiguration',
    'Assert-QualitySqliteDataPathBoundary',
    'Initialize-QualitySqliteDataDirectory',
    'C:\ProgramData\SonAero\deployment-state\quality-assurance-data',
    '[IO.FileMode]::CreateNew',
    'Security.AccessControl.DirectorySecurity',
    'Security.AccessControl.FileSecurity',
    'S-1-5-32-544',
    'S-1-5-18',
    'Get-QualitySanitizedApplicationManifest',
    'Assert-QualitySanitizedApplicationManifestEqual',
    'Authentication__Mode',
    'Database__Provider',
    'QualityDatabase__StorageMode',
    'QualityDatabase:StorageMode',
    'ConnectionStrings__ModuleAccessStore',
    'ConnectionStrings__QualityStore',
    'SQLCONNSTR_QualityStore',
    'ASPNETCORE_ENVIRONMENT',
    'DOTNET_ENVIRONMENT',
    "GetChildElement('applicationPoolDefaults')",
    'approved in-process dotnet command and no application arguments',
    'Set-QualityPhysicalPath -Path $CandidatePath',
    'Set-QualityPhysicalPath -Path $PriorPath',
    'Wait-QualityHealth -TimeoutSeconds $HealthTimeoutSeconds',
    'WHATIF_READY_QUALITY_ASSURANCE_RELEASE',
    'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY',
    'WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_PRODUCTION_DATABASE_SETTINGS_REPAIRED',
    'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_PRODUCTION_DATABASE_SETTINGS_REPAIRED',
    'WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_SERVER_LOCAL_SQLITE',
    'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SERVER_LOCAL_SQLITE',
    'ProcessModel.MaxProcesses -ne 1',
    'ProcessModelIdentityType]::ApplicationPoolIdentity',
    'Set-QualityDisallowOverlappingRotation -Enabled $true',
    '-PriorDisallowOverlappingRotation $priorDisallowOverlappingRotation',
    '-FirstActivation and -RepairMissingProductionDatabaseSettings are mutually exclusive.',
    '-UseServerLocalSqlite is mutually exclusive with -FirstActivation and -RepairMissingProductionDatabaseSettings.'
)) {
    if (-not $source.Contains($required)) {
        throw "Quality release script is missing required guard: $required"
    }
}

foreach ($forbidden in @(
    'ProjectTracker.Api.dll',
    'Portal.Api.dll',
    'EngineeringHub.Api.dll',
    'EstimatingDashboard.Api.dll',
    'Stop-Website',
    'Start-Website'
)) {
    if ($source.Contains($forbidden)) {
        throw "Quality-only release script contains an out-of-scope application mutation: $forbidden"
    }
}

$manifestPreflight = $source.IndexOf(
    '[void](Get-QualitySanitizedApplicationManifest -Root $sourcePath)')
$productionHash = $source.IndexOf(
    '$currentProductionHash = (Get-FileHash -LiteralPath $currentProductionSettings -Algorithm SHA256).Hash')
$repairPlan = $source.IndexOf('$repairPlan = if ($RepairMissingProductionDatabaseSettings)')
$sqlitePlan = $source.IndexOf('$sqlitePlan = if ($UseServerLocalSqlite)')
$sqlitePathPreflight = $source.IndexOf('-Path $sqlitePlan.DataDirectory -RequireEmpty')
$firstActivationGuard = $source.IndexOf(
    "Assert-QualityActivationState -PoolState `$priorPoolState -Healthy `$currentHealthy")
$whatIfGate = $source.IndexOf('if (-not $PSCmdlet.ShouldProcess(')
$releaseCreation = $source.IndexOf('New-Item -ItemType Directory -Path $releaseRootPath -Force')
$candidateCopy = $source.IndexOf('Copy-SanitizedApplication -Source $sourcePath -Destination $releasePath')
$candidateConfiguration = $source.IndexOf(
    '[void](Read-QualityProductionConfiguration -Path $candidateProductionSettings)')
$candidateManifest = $source.IndexOf(
    'Assert-QualitySanitizedApplicationManifestEqual -SourceRoot $sourcePath -CandidateRoot $releasePath')
$sqliteInitialization = $source.IndexOf(
    '[void](Initialize-QualitySqliteDataDirectory -Path $sqlitePlan.DataDirectory)')
$activeHashRecheck = $source.IndexOf(
    '(Get-FileHash -LiteralPath $currentProductionSettings -Algorithm SHA256).Hash -ne $currentProductionHash')
$cutover = $source.IndexOf('Invoke-QualityIisSwitch -CurrentPath $currentPath -CandidatePath $releasePath')
if ($manifestPreflight -lt 0 -or $productionHash -lt 0 -or $repairPlan -lt 0 -or
    $sqlitePlan -lt 0 -or $sqlitePathPreflight -lt 0 -or
    $firstActivationGuard -lt 0 -or $whatIfGate -lt 0 -or $releaseCreation -lt 0 -or
    $candidateCopy -lt 0 -or $candidateConfiguration -lt 0 -or $candidateManifest -lt 0 -or
    $sqliteInitialization -lt 0 -or $activeHashRecheck -lt 0 -or $cutover -lt 0 -or
    $manifestPreflight -gt $productionHash -or $productionHash -gt $repairPlan -or
    $repairPlan -gt $sqlitePlan -or $sqlitePlan -gt $sqlitePathPreflight -or
    $sqlitePathPreflight -gt $firstActivationGuard -or
    $firstActivationGuard -gt $whatIfGate -or
    $whatIfGate -gt $releaseCreation -or $releaseCreation -gt $candidateCopy -or
    $candidateCopy -gt $candidateConfiguration -or
    $candidateConfiguration -gt $candidateManifest -or
    $candidateManifest -gt $sqliteInitialization -or
    $sqliteInitialization -gt $activeHashRecheck -or $activeHashRecheck -gt $cutover) {
    throw 'Quality configuration, WhatIf, immutable candidate, and cutover ordering is unsafe.'
}

foreach ($requiredPattern in @(
    '(?s)if\s*\(\$FirstActivation\s+-and\s+\$RepairMissingProductionDatabaseSettings\)\s*\{\s*throw\s+''-FirstActivation and -RepairMissingProductionDatabaseSettings are mutually exclusive\.''\s*\}',
    '(?s)if\s*\(\$UseServerLocalSqlite\s+-and\s*\(\$FirstActivation\s+-or\s+\$RepairMissingProductionDatabaseSettings\)\)\s*\{\s*throw\s+''-UseServerLocalSqlite is mutually exclusive with -FirstActivation and -RepairMissingProductionDatabaseSettings\.''\s*\}',
    '(?s)\$repairPlan\s*=\s*if\s*\(\$RepairMissingProductionDatabaseSettings\).*?New-QualityProductionDatabaseConfigurationRepair\s+-ActivePath\s+\$currentProductionSettings\s+`?\s*-TemplatePath\s+\$productionTemplate',
    '(?s)\$sqlitePlan\s*=\s*if\s*\(\$UseServerLocalSqlite\).*?New-QualityServerLocalSqliteConfiguration\s+-ActivePath\s+\$currentProductionSettings',
    '(?s)if\s*\(\$RepairMissingProductionDatabaseSettings\)\s*\{\s*\[IO\.File\]::WriteAllBytes\(\$candidateProductionSettings,\s*\[byte\[\]\]\$repairPlan\.Utf8Bytes\)',
    '(?s)elseif\s*\(\$UseServerLocalSqlite\)\s*\{\s*\[IO\.File\]::WriteAllBytes\(\$candidateProductionSettings,\s*\[byte\[\]\]\$sqlitePlan\.Utf8Bytes\)',
    '(?s)else\s*\{\s*Copy-Item\s+-LiteralPath\s+\$currentProductionSettings\s+-Destination\s+\$candidateProductionSettings.*?Copied Quality Production settings hash mismatch',
    '(?s)if\s*\(\$RepairMissingProductionDatabaseSettings\)\s*\{\s*Write-Output\s+''WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_PRODUCTION_DATABASE_SETTINGS_REPAIRED''\s*\}\s*elseif\s*\(\$UseServerLocalSqlite\)\s*\{\s*Write-Output\s+''WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_SERVER_LOCAL_SQLITE''\s*\}\s*else\s*\{\s*Write-Output\s+''WHATIF_READY_QUALITY_ASSURANCE_RELEASE''\s*\}',
    '(?s)if\s*\(\$RepairMissingProductionDatabaseSettings\)\s*\{\s*Write-Output\s+''QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_PRODUCTION_DATABASE_SETTINGS_REPAIRED''\s*\}\s*elseif\s*\(\$UseServerLocalSqlite\)\s*\{\s*Write-Output\s+''QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SERVER_LOCAL_SQLITE''\s*\}\s*else\s*\{\s*Write-Output\s+''QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY''\s*\}'
)) {
    if ($source -notmatch $requiredPattern) {
        throw "Quality release script is missing a bounded repair/normal-mode branch: $requiredPattern"
    }
}
if (([regex]::Matches(
        $source,
        '-FirstActivationRequested\s+\(\[bool\]\$FirstActivation\)')).Count -ne 2) {
    throw 'Repair mode must retain normal healthy-current preflight and cutover semantics; only FirstActivation may admit an unhealthy current release.'
}
if ($source -match '(?s)WriteAllBytes\s*\(\s*\$currentProductionSettings' -or
    $source -match '(?s)Set-Content\s+[^\r\n]*\$currentProductionSettings' -or
    $source -match '(?s)Out-File\s+[^\r\n]*\$currentProductionSettings') {
    throw 'Quality repair mode can write the active Production configuration instead of only the offline candidate.'
}
if ($source -match '(?i)Remove-Item[^\r\n]*(?:quality-assurance\.db|sqlitePlan\.Data)') {
    throw 'Quality SQLite transition can delete the persistent data file or directory.'
}

$functionNames = @(
    'Get-FullPath',
    'Test-PathContainmentOverlap',
    'Assert-ValidWebConfig',
    'Assert-QualityActivationState',
    'Invoke-QualityIisSwitch'
)
$definitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -in $functionNames
}, $true))
if ($definitions.Count -ne $functionNames.Count) {
    throw 'Could not extract the Quality release path/configuration guards.'
}
Invoke-Expression (($definitions | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine)

Assert-QualityActivationState -PoolState Started -Healthy $true `
    -FirstActivationRequested $false -Phase 'test'
Assert-QualityActivationState -PoolState Started -Healthy $false `
    -FirstActivationRequested $true -Phase 'test'
foreach ($invalidState in @(
    [pscustomobject]@{ PoolState = 'Started'; Healthy = $true; FirstActivation = $true },
    [pscustomobject]@{ PoolState = 'Started'; Healthy = $false; FirstActivation = $false },
    [pscustomobject]@{ PoolState = 'Stopped'; Healthy = $false; FirstActivation = $true },
    [pscustomobject]@{ PoolState = 'Stopped'; Healthy = $true; FirstActivation = $false }
)) {
    $failed = $false
    try {
        Assert-QualityActivationState -PoolState $invalidState.PoolState `
            -Healthy $invalidState.Healthy `
            -FirstActivationRequested $invalidState.FirstActivation -Phase 'test'
    }
    catch { $failed = $true }
    if (-not $failed) {
        throw "Unsafe Quality activation state was accepted: $($invalidState | ConvertTo-Json -Compress)"
    }
}

$script:requestStateCalls = 0
$script:restoreWasCalled = $false
$script:restoredPath = $null
$script:restoredPoolState = $null
$script:restoredDisallowOverlappingRotation = $null
function Request-QualityPoolState {
    param([string]$State, [int]$TimeoutSeconds = 120)
    $script:requestStateCalls++
    if ($script:requestStateCalls -eq 1) {
        throw 'Simulated partial stop followed by timeout.'
    }
}
function Restore-PriorQualityRuntime {
    param(
        [string]$PriorPath,
        [string]$PriorPoolState,
        [bool]$PriorWasHealthy,
        [bool]$PriorDisallowOverlappingRotation
    )
    $script:restoreWasCalled = $true
    $script:restoredPath = $PriorPath
    $script:restoredPoolState = $PriorPoolState
    $script:restoredDisallowOverlappingRotation = $PriorDisallowOverlappingRotation
}
function Set-QualityPhysicalPath { param([string]$Path) }
function Assert-QualityPhysicalPath { param([string]$ExpectedPath) }
function Wait-QualityHealth { param([int]$TimeoutSeconds) }
$HealthTimeoutSeconds = 1
$partialStopFailure = $null
try {
    Invoke-QualityIisSwitch -CurrentPath 'C:\prior-quality' `
        -CandidatePath 'C:\candidate-quality' -PriorPoolState Started -PriorWasHealthy $false `
        -PriorDisallowOverlappingRotation $false
}
catch { $partialStopFailure = $_.Exception.Message }
if (-not $script:restoreWasCalled -or $script:restoredPath -cne 'C:\prior-quality' -or
    $script:restoredPoolState -cne 'Started' -or
    $script:restoredDisallowOverlappingRotation -ne $false -or
    $partialStopFailure -notlike '*exact prior IIS path and pool state were restored*') {
    throw 'A partial initial pool-stop failure did not invoke and report exact prior-state restoration.'
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('quality-release-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
try {
    $root = Join-Path $testRoot 'root'
    $child = Join-Path $root 'child'
    $sibling = Join-Path $testRoot 'sibling'
    if (-not (Test-PathContainmentOverlap -FirstPath $root -SecondPath $child)) {
        throw 'Nested release paths were not rejected as overlapping.'
    }
    if (Test-PathContainmentOverlap -FirstPath $root -SecondPath $sibling) {
        throw 'Sibling release paths were incorrectly treated as overlapping.'
    }

    $safeWebConfig = Join-Path $testRoot 'safe.web.config'
    $unsafeWebConfig = Join-Path $testRoot 'unsafe.web.config'
    [IO.File]::WriteAllText($safeWebConfig, @'
<configuration><system.webServer><aspNetCore processPath="dotnet" arguments=".\QualityAssurance.Api.dll" hostingModel="inprocess" /></system.webServer></configuration>
'@)
    Assert-ValidWebConfig -Path $safeWebConfig -ExpectedMainDll 'QualityAssurance.Api.dll'
    [IO.File]::WriteAllText($unsafeWebConfig, @'
<configuration><system.webServer><aspNetCore processPath="dotnet" arguments=".\QualityAssurance.Api.dll --ConnectionStrings:QualityStore=Server=attacker" hostingModel="inprocess" /></system.webServer></configuration>
'@)
    $unsafeArgumentsAccepted = $false
    try {
        Assert-ValidWebConfig -Path $unsafeWebConfig -ExpectedMainDll 'QualityAssurance.Api.dll'
        $unsafeArgumentsAccepted = $true
    }
    catch {}
    if ($unsafeArgumentsAccepted) {
        throw 'Quality web.config accepted command-line configuration overrides.'
    }

}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'QUALITY_ASSURANCE_RELEASE_TESTS_PASSED'
