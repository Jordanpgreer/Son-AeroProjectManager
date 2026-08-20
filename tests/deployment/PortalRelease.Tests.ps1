[CmdletBinding()]
param([string]$ScriptPath = '')

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Deploy-PortalRelease.ps1'
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

$resolvedScript = (Resolve-Path $ScriptPath).Path
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $resolvedScript, [ref]$tokens, [ref]$parseErrors
)
if ($parseErrors.Count -gt 0) {
    throw "Portal release script has syntax errors: $($parseErrors.Message -join '; ')"
}
$source = Get-Content -LiteralPath $resolvedScript -Raw
$lineCount = @(Get-Content -LiteralPath $resolvedScript).Count
Assert-True ($lineCount -lt 500) "Portal release script must stay below 500 lines; found $lineCount."

foreach ($required in @(
    '[CmdletBinding(SupportsShouldProcess, ConfirmImpact = ''High'')]',
    "`$siteName = 'SonAeroPortal'",
    "`$poolName = 'SonAeroPortal'",
    "`$gatewayPath = '/project-tracker-api'",
    "`$gatewayPoolName = 'ProjectTrackerAdminGateway'",
    "`$mainDll = 'Portal.Api.dll'",
    "[string]`$ReleaseRoot = 'C:\SonAero\releases\portal'",
    'WHATIF_READY_PORTAL_RELEASE',
    'PORTAL_RELEASE_DEPLOYED_AND_HEALTHY'
)) {
    Assert-True $source.Contains($required) "Portal release script is missing required contract: $required"
}

$identityDefinitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq 'Assert-DeploymentIdentity'
}, $true))
Assert-True ($identityDefinitions.Count -eq 1) 'Could not inspect the deployment identity guard.'
$identityText = $identityDefinitions[0].Extent.Text
Assert-True ($identityText -match '\$identity\.Name\s+-notlike\s+''SON4L\\\*''' -and
    $identityText -match '\$identity\.IsSystem' -and
    $identityText -match '''NT AUTHORITY\\SYSTEM''' -and
    $identityText -match 'WindowsBuiltInRole\]::Administrator') `
    'Deployment identity validation does not require an elevated interactive SON4L identity.'

foreach ($forbidden in @(
    'QualityAssurance',
    'EngineeringHub',
    'EstimatingDashboard',
    'ProjectTracker.Api.dll',
    'ProjectTracker''',
    '*:5135:',
    '*:5150:',
    '*:5160:',
    '*:5170:'
)) {
    Assert-True (-not $source.Contains($forbidden)) `
        "Portal-only release contains an out-of-scope application reference: $forbidden"
}

$successMarkers = @([regex]::Matches($source, "Write-Output\s+'(?<Marker>[A-Z][A-Z0-9_]+)'"))
$markerNames = @($successMarkers | ForEach-Object { $_.Groups['Marker'].Value } | Select-Object -Unique)
Assert-True ($markerNames.Count -eq 2 -and
    $markerNames -contains 'WHATIF_READY_PORTAL_RELEASE' -and
    $markerNames -contains 'PORTAL_RELEASE_DEPLOYED_AND_HEALTHY') `
    'Portal release emits an unexpected success marker or omits a required marker.'

$shouldProcessStatements = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text -match '\$PSCmdlet\.ShouldProcess\('
}, $true))
Assert-True ($shouldProcessStatements.Count -eq 1) 'Portal release must have exactly one ShouldProcess gate.'
$shouldProcessText = $shouldProcessStatements[0].Extent.Text
Assert-True ($shouldProcessText -match 'WHATIF_READY_PORTAL_RELEASE' -and $shouldProcessText -match '\breturn\b') `
    'WhatIf does not return immediately with its readiness marker.'
$shouldProcessEnd = $shouldProcessStatements[0].Extent.EndOffset
$firstApplyMutation = @($ast.EndBlock.Statements | Where-Object {
    $_.Extent.StartOffset -gt $shouldProcessEnd -and
        $_.Extent.Text -match 'New-Item|Copy-Item|icacls|Invoke-PortalIisSwitch'
} | Sort-Object { $_.Extent.StartOffset } | Select-Object -First 1)
Assert-True ($firstApplyMutation.Count -eq 1 -and $firstApplyMutation[0].Extent.StartOffset -gt $shouldProcessEnd) `
    'No inspectable apply mutation was found after the WhatIf return.'

Assert-True ([regex]::Matches($source, 'Test-Path -LiteralPath \$releasePath').Count -ge 2 -and
    $source -match 'already exists and will not be overwritten' -and
    $source -notmatch 'Remove-Item\s+[^\r\n]*\$releasePath') `
    'The immutable release destination is not protected from overwrite or removal.'
$candidateCopyIndex = $source.LastIndexOf('Copy-SanitizedApplication -Source $sourcePath -Destination $releasePath')
$settingsCopyIndex = $source.IndexOf('Copy-Item -LiteralPath $currentProductionSettings -Destination $candidateProductionSettings')
$currentHashIndex = $source.IndexOf('(Get-FileHash -Algorithm SHA256 -LiteralPath $currentProductionSettings).Hash', $settingsCopyIndex)
$candidateHashIndex = $source.IndexOf('(Get-FileHash -Algorithm SHA256 -LiteralPath $candidateProductionSettings).Hash', $currentHashIndex + 1)
$switchIndex = $source.IndexOf('Invoke-PortalIisSwitch -CurrentPortalPath', $candidateHashIndex)
Assert-True ($candidateCopyIndex -ge 0 -and
    $settingsCopyIndex -gt $candidateCopyIndex -and
    $currentHashIndex -gt $settingsCopyIndex -and
    $candidateHashIndex -gt $currentHashIndex -and
    $switchIndex -gt $candidateHashIndex -and
    $source -match 'Copied Portal production settings hash mismatch') `
    'Active Portal production settings are not copied and hash-verified before IIS cutover.'
Assert-True ($source -match 'appsettings\.Development\*\.json' -and
    $source -match 'Development configuration was found') `
    'Candidate sanitization does not reject development configuration.'

$pathOverlapDefinitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq 'Test-PathContainmentOverlap'
}, $true))
Assert-True ($pathOverlapDefinitions.Count -eq 1) 'Could not inspect the active-IIS-path containment guard.'
Invoke-Expression $pathOverlapDefinitions[0].Extent.Text
function Get-FullPath { param([string]$Path); return [IO.Path]::GetFullPath($Path).TrimEnd('\') }
Assert-True (Test-PathContainmentOverlap -FirstPath 'C:\active\portal\candidate' -SecondPath 'C:\active\portal') `
    'A candidate inside an active IIS path was not rejected.'
Assert-True (Test-PathContainmentOverlap -FirstPath 'C:\active\portal' -SecondPath 'C:\active\portal\current') `
    'A candidate containing an active IIS path was not rejected.'
Assert-True (-not (Test-PathContainmentOverlap -FirstPath 'C:\releases\portal\new' -SecondPath 'C:\releases\portal\old')) `
    'Sibling immutable releases must remain eligible.'
Assert-True ($source -match 'foreach \(\$activeApplicationPath in \$boundary\.AllApplicationPaths\)' -and
    $source -match 'Test-PathContainmentOverlap -FirstPath \$releasePath -SecondPath \$activeApplicationPath' -and
    $source -match 'overlaps active IIS path') `
    'Every active IIS application path must be checked before Portal candidate creation.'
$activePathGuardIndex = $source.IndexOf('foreach ($activeApplicationPath in $boundary.AllApplicationPaths)')
$whatIfGateIndex = $source.IndexOf('if (-not $PSCmdlet.ShouldProcess(')
Assert-True ($activePathGuardIndex -ge 0 -and $whatIfGateIndex -gt $activePathGuardIndex) `
    'Active IIS path overlap must fail during preflight before the WhatIf/apply boundary.'

$setPathDefinitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq 'Set-PortalRootPhysicalPath'
}, $true))
Assert-True ($setPathDefinitions.Count -eq 1) 'Could not inspect the Portal root path setter.'
$setPathText = $setPathDefinitions[0].Extent.Text
Assert-True ([regex]::Matches($setPathText, '\.PhysicalPath\s*=').Count -eq 1 -and
    [regex]::Matches($setPathText, 'CommitChanges\(\)').Count -eq 1 -and
    $setPathText -match "Applications\['/'\]" -and
    $setPathText -notmatch '\$gatewayPath|ProjectTrackerAdminGateway|ApplicationPoolName\s*=') `
    'IIS commit is not limited to the Portal root virtual directory physical path.'
Assert-True ($source -notmatch '(?i)(Set-ItemProperty|Set-WebConfigurationProperty|appcmd(?:\.exe)?)\b' -and
    $source -notmatch 'ApplicationPoolName\s*=') `
    'Portal release can mutate IIS properties outside the one root path assignment.'

Assert-True ($source -notmatch '\bStop-Website\b|\bStart-Website\b') `
    'Portal release must never stop or start the Portal site.'
$poolCommands = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -in @('Stop-WebAppPool', 'Start-WebAppPool')
}, $true))
Assert-True ($poolCommands.Count -eq 2) 'Expected exactly one root-pool stop and one root-pool start command site.'
foreach ($command in $poolCommands) {
    Assert-True ($command.Extent.Text -match '-Name\s+\$poolName' -and
        $command.Extent.Text -notmatch '\$gatewayPoolName') `
        'An application-pool mutation is not hard-bound to the Portal root pool.'
}

$boundaryDefinitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq 'Get-PortalIisBoundary'
}, $true))
Assert-True ($boundaryDefinitions.Count -eq 1) 'Could not inspect the Portal IIS boundary reader.'
$boundaryText = $boundaryDefinitions[0].Extent.Text
Assert-True ($boundaryText -match 'rootApplication\.ApplicationPoolName\s+-ine\s+\$poolName' -and
    $boundaryText -match 'gatewayApplication\.ApplicationPoolName\s+-ine\s+\$gatewayPoolName' -and
    $boundaryText -match '\$rootAnonymous\s+-or\s+-not\s+\$rootWindows' -and
    $boundaryText -match '\$gatewayAnonymous\s+-or\s+-not\s+\$gatewayWindows' -and
    $boundaryText -match 'Anonymous=False and Windows=True') `
    'Portal/gateway pool assignment and root/gateway authentication are not enforced.'
Assert-True ($source -match 'GatewayPath -ine \(Get-FullPath -Path \$ExpectedGatewayPath\)' -and
    $source -match 'Portal gateway path changed') `
    'The unchanged gateway physical path is not verified around cutover and rollback.'

$switchDefinitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq 'Invoke-PortalIisSwitch'
}, $true))
Assert-True ($switchDefinitions.Count -eq 1) 'Could not inspect the Portal cutover transaction.'
$switchText = $switchDefinitions[0].Extent.Text
Assert-True ($switchText -match 'Request-PortalPoolState -State Stopped' -and
    $switchText -match 'gateway during root cutover' -and
    $switchText -match 'Set-PortalRootPhysicalPath -PortalPath \$CandidatePath' -and
    $switchText -match 'Set-PortalRootPhysicalPath -PortalPath \$CurrentPortalPath' -and
    $switchText -match 'Wait-PortalAndGatewayHealth' -and
    $switchText -match 'prior Portal root path was restored healthy') `
    'Cutover does not prove gateway continuity and exact prior-path rollback.'

# Exercise the cutover orchestration with mocks: first success, then a failed
# first path commit whose second path commit must restore the prior root.
Invoke-Expression $switchDefinitions[0].Extent.Text
$script:HealthTimeoutSeconds = 30
$script:events = New-Object System.Collections.Generic.List[string]
function Request-PortalPoolState { param([string]$State); $script:events.Add("pool:$State") }
function Assert-NonTargetRuntimeStarted { $script:events.Add('nontarget:started') }
function Wait-EndpointHealth { param($Label, $Uri, $TimeoutSeconds); $script:events.Add('gateway:healthy') }
function Set-PortalRootPhysicalPath { param($PortalPath); $script:events.Add("path:$PortalPath") }
function Assert-PortalPhysicalPaths { param($ExpectedPortalPath, $ExpectedGatewayPath); $script:events.Add("assert:$ExpectedPortalPath|$ExpectedGatewayPath") }
function Wait-PortalAndGatewayHealth { param($TimeoutSeconds); $script:events.Add('both:healthy') }
Invoke-PortalIisSwitch -CurrentPortalPath 'C:\active\portal' `
    -CurrentGatewayPath 'C:\active\tracker' -CandidatePath 'C:\release\portal'
Assert-True ($script:events -contains 'path:C:\release\portal' -and
    $script:events -contains 'assert:C:\release\portal|C:\active\tracker' -and
    $script:events -contains 'both:healthy') `
    'Mock successful cutover did not switch only Portal while verifying the unchanged gateway.'

$script:events.Clear()
$script:setAttempts = 0
function Set-PortalRootPhysicalPath {
    param($PortalPath)
    $script:setAttempts++
    $script:events.Add("path:$PortalPath")
    if ($script:setAttempts -eq 1) { throw 'simulated commit failure' }
}
$failure = $null
try {
    Invoke-PortalIisSwitch -CurrentPortalPath 'C:\active\portal' `
        -CurrentGatewayPath 'C:\active\tracker' -CandidatePath 'C:\release\portal'
}
catch { $failure = $_.Exception.Message }
Assert-True ($script:setAttempts -eq 2 -and
    $script:events -contains 'path:C:\active\portal' -and
    $failure -match 'prior Portal root path was restored healthy' -and
    $failure -match 'failed candidate was retained') `
    'Mock failed cutover did not restore the exact prior Portal path and report retained-candidate rollback.'

Write-Output 'PORTAL_RELEASE_TESTS_PASSED'
