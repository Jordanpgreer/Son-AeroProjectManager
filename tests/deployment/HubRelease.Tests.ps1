[CmdletBinding()]
param(
    [string]$ScriptPath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Deploy-HubRelease.ps1'
}
if ($PSVersionTable.PSVersion.Major -ne 5) {
    throw "These compatibility tests must run under Windows PowerShell 5.1; current version is $($PSVersionTable.PSVersion)."
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $ScriptPath), [ref]$tokens, [ref]$parseErrors
)
if ($parseErrors.Count -gt 0) {
    throw "Hub release script has syntax errors: $($parseErrors.Message -join '; ')"
}
$source = Get-Content -LiteralPath $ScriptPath -Raw

foreach ($required in @(
    '[switch]$RetainVerifiedQuality',
    'NT AUTHORITY\SYSTEM',
    'authorized SON4L domain user',
    'Test-PathContainmentOverlap',
    'PackageRoot and the release destination cannot contain one another.',
    'Release destination and active IIS path cannot contain one another:',
    'PackageRoot and active IIS path cannot contain one another:',
    'WHATIF_READY',
    'HUB_RELEASE_DEPLOYED_AND_HEALTHY',
    'WHATIF_READY_HUB_RELEASE_WITH_VERIFIED_QUALITY_RETAINED',
    'HUB_RELEASE_DEPLOYED_AND_HEALTHY_WITH_VERIFIED_QUALITY_RETAINED',
    'QualityAssuranceProductionConfiguration.psm1',
    'HubReleaseRetainedQuality.psm1',
    'Read-QualityProductionConfiguration',
    'approved in-process dotnet command and no application arguments',
    'Assert-QualitySanitizedApplicationManifestEqual',
    'Get-HubRetainedQualityBoundarySnapshot',
    'Assert-HubRetainedQualityBoundaryUnchanged',
    "-Phase 'pre-IIS-mutation verification'",
    "-Phase 'successful deployment verification'",
    "-Phase 'rollback verification'",
    'all previous IIS paths were restored healthy',
    'Last results:',
    'Get-HealthResult -Application $application',
    'Get-ProjectTrackerGatewayHealthResult',
    '$currentHealth = Get-HealthResult -Application $application',
    '$currentGatewayHealth = Get-ProjectTrackerGatewayHealthResult',
    'Wait-ApplicationHealth -Targets @($application) -TimeoutSeconds $HealthTimeoutSeconds'
)) {
    Assert-True $source.Contains($required) "Hub release script is missing required contract: $required"
}

foreach ($requiredPattern in @(
    '(?s)\$deploymentApplications\s*=\s*if\s*\(\$RetainVerifiedQuality\)\s*\{.*?Where-Object\s+Name\s+-NE\s+\$qualityApplication\.Name.*?\}\s*else\s*\{\s*@\(\$applications\)\s*\}',
    '(?s)if\s*\(\$RetainVerifiedQuality\)\s*\{\s*Write-Output\s+''WHATIF_READY_HUB_RELEASE_WITH_VERIFIED_QUALITY_RETAINED''\s*\}\s*else\s*\{\s*Write-Output\s+''WHATIF_READY''\s*\}',
    '(?s)\$successStatus\s*=\s*if\s*\(\$RetainVerifiedQuality\)\s*\{\s*''HUB_RELEASE_DEPLOYED_AND_HEALTHY_WITH_VERIFIED_QUALITY_RETAINED''\s*\}\s*else\s*\{\s*''HUB_RELEASE_DEPLOYED_AND_HEALTHY''\s*\}',
    '(?s)New-Item\s+-ItemType\s+Directory\s+-Path\s+\$releasePath.*?foreach\s*\(\$application\s+in\s+\$deploymentApplications\).*?Copy-SanitizedApplication.*?icacls\.exe\s+\$candidatePath',
    '(?s)function\s+Set-IisPhysicalPaths\s*\{.*?foreach\s*\(\$application\s+in\s+\$deploymentApplications\)',
    '(?s)function\s+Stop-HubApplications\s*\{.*?foreach\s*\(\$application\s+in\s+\$deploymentApplications\).*?\$poolNames\s*=\s*@\(\$deploymentApplications\.Name\)',
    '(?s)function\s+Start-HubApplications\s*\{.*?\$tracker\s*=\s*@\(\$deploymentApplications.*?\$remaining\s*=\s*@\(\$deploymentApplications'
)) {
    Assert-True ($source -match $requiredPattern) `
        "Hub release is missing a retained/full-mode scoping contract: $requiredPattern"
}

$qualityModuleAssignment = $source.IndexOf(
    '$qualityProductionConfigurationModule = Join-Path $PSScriptRoot ''QualityAssuranceProductionConfiguration.psm1''')
$qualityModuleImport = $source.IndexOf(
    'Import-Module $qualityProductionConfigurationModule -Force -ErrorAction Stop')
$conditionalRetainBlock = $source.IndexOf('if ($RetainVerifiedQuality)')
Assert-True ($qualityModuleAssignment -ge 0 -and $qualityModuleImport -gt $qualityModuleAssignment -and
    $qualityModuleImport -lt $conditionalRetainBlock) `
    'The full-Hub path does not import the Quality production validator unconditionally.'

$activeQualityValidation = $source.IndexOf(
    '[void](Read-QualityProductionConfiguration -Path $productionSettings)')
$whatIfGate = $source.IndexOf('if (-not $PSCmdlet.ShouldProcess(')
$candidateHash = $source.IndexOf(
    'Copied production settings hash mismatch for ''$($application.Name)''.')
$candidateQualityValidation = $source.IndexOf(
    '[void](Read-QualityProductionConfiguration -Path $candidateProductionSettings)')
$candidateAcl = $source.IndexOf('& icacls.exe $candidatePath')
Assert-True ($activeQualityValidation -ge 0 -and $activeQualityValidation -lt $whatIfGate) `
    'Normal full-Hub preflight does not fail closed on the active Quality Production configuration.'
Assert-True ($candidateHash -ge 0 -and $candidateQualityValidation -gt $candidateHash -and
    $candidateQualityValidation -lt $candidateAcl) `
    'Normal full-Hub candidate preparation does not validate copied Quality settings after its hash and before ACL/live mutation.'

$retainedManifestPreflight = $source.IndexOf(
    'Assert-QualitySanitizedApplicationManifestEqual', $source.IndexOf('$activeQualityPath ='))
$retainedSnapshot = $source.IndexOf(
    '$retainedQualitySnapshot = Get-HubRetainedQualityBoundarySnapshot')
Assert-True ($retainedManifestPreflight -ge 0 -and $retainedSnapshot -gt $retainedManifestPreflight -and
    $retainedSnapshot -lt $whatIfGate) `
    'Retained Quality manifest/configuration/boundary verification is not fail-closed before WhatIf approval.'
Assert-True (([regex]::Matches($source,
    '(?m)^\s*Assert-RetainedQualityPreserved\s+')).Count -eq 3) `
    'Retained Quality must be reverified immediately before IIS mutation, after success, and after rollback.'

$retainedModulePath = Join-Path (Split-Path -Parent $ScriptPath) 'HubReleaseRetainedQuality.psm1'
Assert-True (Test-Path -LiteralPath $retainedModulePath -PathType Leaf) `
    'The retained Quality read-only boundary module is missing.'
$retainedModuleSource = Get-Content -LiteralPath $retainedModulePath -Raw
foreach ($required in @(
    'Authentication__Mode',
    'ProductionConfigurationHash',
    'CriticalAcls',
    'Bindings',
    'PoolConfiguration',
    'AspNetCoreEnvironmentHash',
    'PoolEnvironmentHash',
    'MachineOverrideState',
    'QualityDatabase__Provider',
    'ConnectionStrings__QualityStore',
    'EnvironmentVariableTarget]::Machine',
    'AnonymousEnabled',
    'WindowsEnabled',
    'HealthStatus',
    'Changed fields:'
)) {
    Assert-True $retainedModuleSource.Contains($required) `
        "Retained Quality boundary snapshot omits required evidence '$required'."
}
foreach ($forbiddenMutation in @(
    'Stop-WebAppPool',
    'Start-WebAppPool',
    'Stop-Website',
    'Start-Website',
    'CommitChanges',
    'icacls.exe',
    'Set-Acl',
    'Copy-Item',
    'Move-Item',
    'Remove-Item'
)) {
    Assert-True (-not $retainedModuleSource.Contains($forbiddenMutation)) `
        "Retained Quality boundary module contains mutation command '$forbiddenMutation'."
}
Assert-True (-not $source.Contains(
    'Wait-ApplicationHealth -Targets $remaining -TimeoutSeconds $HealthTimeoutSeconds'
)) 'Hub release still health-gates the remaining applications as one overlapping startup group.'
Assert-True (-not $source.Contains('Test-HealthOnce')) `
    'Hub release still calls the removed Boolean health probe.'
Assert-True (-not $source.Contains('Test-ProjectTrackerGatewayHealthOnce')) `
    'Hub release still calls the removed Boolean gateway health probe.'

$rollbackStart = $source.IndexOf('$rollbackErrors =')
$rollbackStop = $source.IndexOf('try { Stop-HubApplications }', $rollbackStart)
$rollbackPaths = $source.IndexOf('try { Set-IisPhysicalPaths -PathsBySite $currentPaths }', $rollbackStop)
$rollbackStartApps = $source.IndexOf('try { Start-HubApplications }', $rollbackPaths)
Assert-True ($rollbackStart -ge 0 -and $rollbackStop -gt $rollbackStart -and
    $rollbackPaths -gt $rollbackStop -and $rollbackStartApps -gt $rollbackPaths) `
    'Rollback no longer stops candidates, restores every prior path, and verifies prior health in order.'

$functionNames = @(
    'Get-FullPath',
    'Test-PathContainmentOverlap',
    'Get-HealthResult',
    'Wait-ApplicationHealth',
    'Stop-HubApplications',
    'Start-HubApplications'
)
$definitions = @(
    $ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -in $functionNames
    }, $true)
)
Assert-True ($definitions.Count -eq $functionNames.Count) `
    'Could not extract the Hub release startup and health functions.'
Invoke-Expression (($definitions | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine)

$pathTestRoot = Join-Path ([IO.Path]::GetTempPath()) 'hub-release-containment-test'
Assert-True (Test-PathContainmentOverlap `
    -FirstPath $pathTestRoot -SecondPath (Join-Path $pathTestRoot 'child')) `
    'Hub release containment guard did not reject a nested destination.'
Assert-True (-not (Test-PathContainmentOverlap `
    -FirstPath $pathTestRoot -SecondPath ($pathTestRoot + '-sibling'))) `
    'Hub release containment guard rejected a non-overlapping sibling destination.'

$applications = @(
    [pscustomobject]@{ Name = 'ProjectTracker'; Port = 5135 },
    [pscustomobject]@{ Name = 'SonAeroPortal'; Port = 5140 },
    [pscustomobject]@{ Name = 'EngineeringHub'; Port = 5150 },
    [pscustomobject]@{ Name = 'EstimatingDashboard'; Port = 5160 },
    [pscustomobject]@{ Name = 'QualityAssurance'; Port = 5170 }
)
$projectTrackerGateway = [pscustomobject]@{
    Site = 'SonAeroPortal'
    Pool = 'ProjectTrackerAdminGateway'
}
$HealthTimeoutSeconds = 321
$script:calls = New-Object System.Collections.Generic.List[string]
$deploymentApplications = @($applications)

function Start-OneApplication {
    param([object]$Application)
    $script:calls.Add("start:$($Application.Name)")
}
function Wait-ApplicationHealth {
    param([object[]]$Targets, [int]$TimeoutSeconds)
    Assert-True ($Targets.Count -eq 1) 'A startup health gate received more than one application.'
    Assert-True ($TimeoutSeconds -eq 321) 'The configured application health timeout was not preserved.'
    $script:calls.Add("health:$($Targets[0].Name)")
}
function Request-IisState {
    param([string]$Kind, [string]$Name, [string]$State)
    $script:calls.Add("request:$Kind`:$Name`:$State")
}
function Wait-IisState {
    param([string]$Kind, [string[]]$Names, [string]$State)
    $script:calls.Add("wait:$Kind`:$($Names -join ',')`:$State")
}
function Wait-ProjectTrackerGatewayHealth {
    param([int]$TimeoutSeconds)
    Assert-True ($TimeoutSeconds -eq 321) 'The gateway health timeout was not preserved.'
    $script:calls.Add('health:ProjectTrackerGateway')
}

Start-HubApplications
$expectedCalls = @(
    'start:ProjectTracker',
    'health:ProjectTracker',
    'start:SonAeroPortal',
    'health:SonAeroPortal',
    'request:Pool:ProjectTrackerAdminGateway:Started',
    'wait:Pool:ProjectTrackerAdminGateway:Started',
    'health:ProjectTrackerGateway',
    'start:EngineeringHub',
    'health:EngineeringHub',
    'start:EstimatingDashboard',
    'health:EstimatingDashboard',
    'start:QualityAssurance',
    'health:QualityAssurance'
)
Assert-True (($script:calls -join '|') -ceq ($expectedCalls -join '|')) `
    "Hub applications were not started and verified serially. Actual: $($script:calls -join '|')"

# Retain mode keeps the verified Quality site and pool completely outside the start sequence,
# while preserving Project Tracker -> Portal -> gateway ordering for the other four modules.
$script:calls.Clear()
$deploymentApplications = @($applications | Where-Object Name -NE 'QualityAssurance')
Start-HubApplications
$expectedRetainedCalls = @($expectedCalls | Where-Object { $_ -notmatch 'QualityAssurance' })
Assert-True (($script:calls -join '|') -ceq ($expectedRetainedCalls -join '|')) `
    "Retain mode started or health-probed Quality, or changed gateway ordering. Actual: $($script:calls -join '|')"

$script:calls.Clear()
Stop-HubApplications
Assert-True (@($script:calls | Where-Object { $_ -match 'QualityAssurance' }).Count -eq 0) `
    'Retain mode stopped or waited on the verified Quality site or pool.'
Assert-True (($script:calls -join '|') -like '*request:Pool:ProjectTrackerAdminGateway:Stopped*') `
    'Retain mode stopped excluding Quality but failed to retain the Project Tracker gateway transaction.'

# The normal full mode remains a five-application transaction.
$script:calls.Clear()
$deploymentApplications = @($applications)
Stop-HubApplications
Assert-True (@($script:calls | Where-Object {
    $_ -ceq 'request:Site:QualityAssurance:Stopped' -or
    $_ -ceq 'request:Pool:QualityAssurance:Stopped'
}).Count -eq 2) 'Normal full mode no longer stops both the Quality site and pool.'

$script:calls.Clear()
function Wait-ProjectTrackerGatewayHealth {
    param([int]$TimeoutSeconds)
    $script:calls.Add('health:ProjectTrackerGateway')
    throw 'Simulated gateway cold-start failure.'
}
$gatewayFailure = ''
try { Start-HubApplications }
catch { $gatewayFailure = $_.Exception.Message }
Assert-True ($gatewayFailure -ceq 'Simulated gateway cold-start failure.') `
    'A gateway startup failure was not returned to the rollback transaction.'
Assert-True (-not ($script:calls -contains 'start:EngineeringHub') -and
    -not ($script:calls -contains 'start:EstimatingDashboard') -and
    -not ($script:calls -contains 'start:QualityAssurance')) `
    'A SQL-backed module started after the gateway failed its cold-start health gate.'

$script:calls.Clear()
function Wait-ProjectTrackerGatewayHealth {
    param([int]$TimeoutSeconds)
    $script:calls.Add('health:ProjectTrackerGateway')
}
function Wait-ApplicationHealth {
    param([object[]]$Targets, [int]$TimeoutSeconds)
    $name = [string]$Targets[0].Name
    $script:calls.Add("health:$name")
    if ($name -ceq 'EngineeringHub') { throw 'Simulated Engineering cold-start failure.' }
}
$failure = ''
try { Start-HubApplications }
catch { $failure = $_.Exception.Message }
Assert-True ($failure -ceq 'Simulated Engineering cold-start failure.') `
    'A serial startup failure was not returned to the rollback transaction.'
Assert-True (-not ($script:calls -contains 'start:EstimatingDashboard') -and
    -not ($script:calls -contains 'start:QualityAssurance')) `
    'A later application started after an earlier application failed its health gate.'

$waitDefinition = @($definitions | Where-Object Name -EQ 'Wait-ApplicationHealth')[0]
Invoke-Expression $waitDefinition.Extent.Text
function Get-HealthResult {
    param([object]$Application)
    [pscustomobject]@{
        Healthy = $false
        Detail = 'Simulated SQL connection timeout before migrations.'
    }
}
function Start-Sleep { param([int]$Milliseconds) }
$detailFailure = ''
try {
    Wait-ApplicationHealth `
        -Targets @([pscustomobject]@{ Name = 'QualityAssurance'; Port = 5170 }) `
        -TimeoutSeconds 0
}
catch { $detailFailure = $_.Exception.Message }
Assert-True ($detailFailure -like '*QualityAssurance*Simulated SQL connection timeout before migrations.*') `
    'The health timeout discarded the last application-specific failure detail.'

Write-Output 'HUB_RELEASE_TESTS_PASSED'
