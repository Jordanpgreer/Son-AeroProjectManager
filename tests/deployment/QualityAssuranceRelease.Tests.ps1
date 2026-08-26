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
    'Set-QualityPhysicalPath -Path $CandidatePath',
    'Set-QualityPhysicalPath -Path $PriorPath',
    'Wait-QualityHealth -TimeoutSeconds $HealthTimeoutSeconds',
    'WHATIF_READY_QUALITY_ASSURANCE_RELEASE',
    'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY'
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

$firstActivationGuard = $source.IndexOf(
    "Assert-QualityActivationState -PoolState `$priorPoolState -Healthy `$currentHealthy")
$whatIfGate = $source.IndexOf('if (-not $PSCmdlet.ShouldProcess(')
$releaseCreation = $source.IndexOf('New-Item -ItemType Directory -Path $releaseRootPath -Force')
$candidateCopy = $source.IndexOf('Copy-SanitizedApplication -Source $sourcePath -Destination $releasePath')
$cutover = $source.IndexOf('Invoke-QualityIisSwitch -CurrentPath $currentPath -CandidatePath $releasePath')
if ($firstActivationGuard -lt 0 -or $whatIfGate -lt 0 -or $releaseCreation -lt 0 -or
    $candidateCopy -lt 0 -or $cutover -lt 0 -or
    $firstActivationGuard -gt $whatIfGate -or $whatIfGate -gt $releaseCreation -or
    $releaseCreation -gt $candidateCopy -or $candidateCopy -gt $cutover) {
    throw 'Quality first-activation, WhatIf, candidate preparation, and cutover ordering is unsafe.'
}

$functionNames = @(
    'Get-FullPath',
    'Test-PathContainmentOverlap',
    'Read-QualityProductionConfiguration',
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
function Request-QualityPoolState {
    param([string]$State, [int]$TimeoutSeconds = 120)
    $script:requestStateCalls++
    if ($script:requestStateCalls -eq 1) {
        throw 'Simulated partial stop followed by timeout.'
    }
}
function Restore-PriorQualityRuntime {
    param([string]$PriorPath, [string]$PriorPoolState, [bool]$PriorWasHealthy)
    $script:restoreWasCalled = $true
    $script:restoredPath = $PriorPath
    $script:restoredPoolState = $PriorPoolState
}
function Set-QualityPhysicalPath { param([string]$Path) }
function Assert-QualityPhysicalPath { param([string]$ExpectedPath) }
function Wait-QualityHealth { param([int]$TimeoutSeconds) }
$HealthTimeoutSeconds = 1
$partialStopFailure = $null
try {
    Invoke-QualityIisSwitch -CurrentPath 'C:\prior-quality' `
        -CandidatePath 'C:\candidate-quality' -PriorPoolState Started -PriorWasHealthy $false
}
catch { $partialStopFailure = $_.Exception.Message }
if (-not $script:restoreWasCalled -or $script:restoredPath -cne 'C:\prior-quality' -or
    $script:restoredPoolState -cne 'Started' -or
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

    $validConfiguration = Join-Path $testRoot 'valid.json'
    @'
{
  "Authentication": { "Mode": "Windows" },
  "Database": { "Provider": "SqlServer" },
  "QualityDatabase": { "Provider": "SqlServer" },
  "ConnectionStrings": { "QualityStore": "Server=SON-SQL2;Database=QualityAssurance;Trusted_Connection=True" }
}
'@ | Set-Content -LiteralPath $validConfiguration -Encoding UTF8
    [void](Read-QualityProductionConfiguration -Path $validConfiguration)

    foreach ($invalidJson in @(
        '{"Authentication":{"Mode":"Development"},"Database":{"Provider":"SqlServer"},"QualityDatabase":{"Provider":"SqlServer"},"ConnectionStrings":{"QualityStore":"Server=SON-SQL2;Database=QualityAssurance"}}',
        '{"Authentication":{"Mode":"Windows"},"Database":{"Provider":"Sqlite"},"QualityDatabase":{"Provider":"SqlServer"},"ConnectionStrings":{"QualityStore":"Server=SON-SQL2;Database=QualityAssurance"}}',
        '{"Authentication":{"Mode":"Windows"},"Database":{"Provider":"SqlServer"},"QualityDatabase":{"Provider":"SqlServer"},"ConnectionStrings":{"QualityStore":"Server=SON-SQL2;Database=WrongDatabase"}}'
    )) {
        $invalidPath = Join-Path $testRoot ([Guid]::NewGuid().ToString('N') + '.json')
        $invalidJson | Set-Content -LiteralPath $invalidPath -Encoding UTF8
        $failed = $false
        try { [void](Read-QualityProductionConfiguration -Path $invalidPath) }
        catch { $failed = $true }
        if (-not $failed) { throw "Invalid Quality Production settings were accepted: $invalidJson" }
    }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'QUALITY_ASSURANCE_RELEASE_TESTS_PASSED'
