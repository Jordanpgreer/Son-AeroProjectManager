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
    'WHATIF_READY',
    'HUB_RELEASE_DEPLOYED_AND_HEALTHY',
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
    'Get-HealthResult',
    'Wait-ApplicationHealth',
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
