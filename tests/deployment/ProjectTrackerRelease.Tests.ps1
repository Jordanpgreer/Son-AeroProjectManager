[CmdletBinding()]
param(
    [string]$ScriptPath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Deploy-ProjectTrackerRelease.ps1'
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

function Get-TestableFunctions {
    param(
        [Parameter(Mandatory = $true)][Management.Automation.Language.Ast]$Ast,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    $definitions = @(
        $Ast.FindAll({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -in $Names
        }, $true) | ForEach-Object { $_.Extent.Text }
    )
    if ($definitions.Count -ne $Names.Count) {
        throw "Expected $($Names.Count) testable functions but found $($definitions.Count)."
    }
    return [scriptblock]::Create(($definitions -join [Environment]::NewLine))
}

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $ScriptPath), [ref]$tokens, [ref]$parseErrors
)
if ($parseErrors.Count -gt 0) {
    throw "Project Tracker release script has syntax errors: $($parseErrors.Message -join '; ')"
}
$source = Get-Content -LiteralPath $ScriptPath -Raw

$identityDefinitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq 'Assert-DeploymentIdentity'
}, $true))
Assert-True ($identityDefinitions.Count -eq 1) 'Could not inspect the deployment identity guard.'
$identityText = $identityDefinitions[0].Extent.Text
Assert-True ($identityText -match '\$identity\.Name\s+-notlike\s+''SON4L\\\*''' -and
    $identityText -match 'authorized SON4L domain user' -and
    $identityText -match '\$identity\.IsSystem' -and
    $identityText -match '''NT AUTHORITY\\SYSTEM''') `
    'Deployment identity validation does not reject non-SON4L accounts and Local System.'

# This transaction must be narrow by construction: one direct Project Tracker site and its
# existing Portal gateway, never the other four root applications from Deploy-HubRelease.ps1.
foreach ($required in @(
    '[CmdletBinding(SupportsShouldProcess, ConfirmImpact = ''High'')]',
    "`$directSiteName = 'ProjectTracker'",
    "`$directPoolName = 'ProjectTracker'",
    "`$gatewaySiteName = 'SonAeroPortal'",
    "`$gatewayPath = '/project-tracker-api'",
    "`$gatewayPoolName = 'ProjectTrackerAdminGateway'",
    "`$mainDll = 'ProjectTracker.Api.dll'",
    'WHATIF_READY_PROJECT_TRACKER_RELEASE',
    'PROJECT_TRACKER_RELEASE_DEPLOYED_AND_HEALTHY'
)) {
    Assert-True $source.Contains($required) "Project Tracker release script is missing required contract: $required"
}
foreach ($forbidden in @(
    'QualityAssurance',
    'EngineeringHub',
    'EstimatingDashboard',
    'QualityAssurance.Api.dll',
    'EngineeringHub.Api.dll',
    'EstimatingDashboard.Api.dll',
    '*:5150:',
    '*:5160:',
    '*:5170:'
)) {
    Assert-True (-not $source.Contains($forbidden)) `
        "Project Tracker-only release script contains an out-of-scope application reference: $forbidden"
}

$successMarkers = @([regex]::Matches($source, "Write-Output\s+'(?<Marker>[A-Z][A-Z0-9_]+)'"))
$markerNames = @($successMarkers | ForEach-Object { $_.Groups['Marker'].Value } | Select-Object -Unique)
Assert-True ($markerNames.Count -eq 2 -and
    $markerNames -contains 'WHATIF_READY_PROJECT_TRACKER_RELEASE' -and
    $markerNames -contains 'PROJECT_TRACKER_RELEASE_DEPLOYED_AND_HEALTHY') `
    'Project Tracker release script emits an unexpected success marker or omits a required exact marker.'

# WhatIf may perform read-only preflight, but the ShouldProcess false branch must emit its marker
# and return before the release directory, ACL, or any IIS state is mutated.
$shouldProcessStatements = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text -match '\$PSCmdlet\.ShouldProcess\('
}, $true))
Assert-True ($shouldProcessStatements.Count -eq 1) `
    'Project Tracker release script must have exactly one inspectable ShouldProcess gate.'
$shouldProcessText = $shouldProcessStatements[0].Extent.Text
Assert-True ($shouldProcessText -match 'WHATIF_READY_PROJECT_TRACKER_RELEASE' -and
    $shouldProcessText -match '\breturn\b') `
    'The WhatIf branch does not return immediately with its exact readiness marker.'
$shouldProcessEnd = $shouldProcessStatements[0].Extent.EndOffset
$topLevelMutation = @($ast.EndBlock.Statements | Where-Object {
    $_.Extent.StartOffset -gt $shouldProcessEnd -and
    $_.Extent.Text -match 'New-Item|Copy-Item|icacls|Stop-|Start-|CommitChanges|Set-Iis'
} | Sort-Object { $_.Extent.StartOffset } | Select-Object -First 1)
Assert-True ($topLevelMutation.Count -eq 1) `
    'No post-ShouldProcess apply transaction could be identified.'
Assert-True ($topLevelMutation[0].Extent.StartOffset -gt $shouldProcessEnd) `
    'A release or IIS mutation can execute before the ShouldProcess WhatIf return.'

# Immutable release safeguards and exact production configuration carry-forward.
Assert-True ([regex]::Matches($source, 'Test-Path -LiteralPath \$releasePath').Count -ge 2) `
    'The immutable release destination is not guarded both before and after preflight.'
Assert-True ($source -match 'Release destination already exists and will not be overwritten' -and
    $source -notmatch 'Remove-Item\s+[^\r\n]*\$releasePath') `
    'The immutable release destination can be overwritten or removed.'
$candidateCopyIndex = $source.LastIndexOf('Copy-SanitizedApplication -Source $sourcePath -Destination $releasePath')
$settingsCopyIndex = $source.IndexOf('Copy-Item -LiteralPath $currentProductionSettings -Destination $candidateProductionSettings')
$currentHashIndex = $source.IndexOf('(Get-FileHash -Algorithm SHA256 -LiteralPath $currentProductionSettings).Hash', $settingsCopyIndex)
$candidateHashIndex = $source.IndexOf('(Get-FileHash -Algorithm SHA256 -LiteralPath $candidateProductionSettings).Hash', $currentHashIndex + 1)
$iisTouchedIndex = $source.IndexOf('Invoke-ProjectTrackerIisSwitch', $candidateHashIndex)
Assert-True ($candidateCopyIndex -ge 0 -and
    $settingsCopyIndex -gt $candidateCopyIndex -and
    $currentHashIndex -gt $settingsCopyIndex -and
    $candidateHashIndex -gt $currentHashIndex -and
    $iisTouchedIndex -gt $candidateHashIndex -and
    $source -match 'Copied production settings hash mismatch') `
    'The active Project Tracker Production settings are not copied and hash-verified before IIS is touched.'

# The one IIS commit may change only the direct root virtual directory and the existing gateway.
$setPathDefinitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Extent.Text -match 'CommitChanges\(\)' -and
        $node.Extent.Text -match 'VirtualDirectories'
}, $true))
Assert-True ($setPathDefinitions.Count -eq 1) `
    'Expected exactly one IIS physical-path commit function.'
$setPathText = $setPathDefinitions[0].Extent.Text
Assert-True ([regex]::Matches($setPathText, '\.PhysicalPath\s*=').Count -eq 2 -and
    [regex]::Matches($setPathText, 'CommitChanges\(\)').Count -eq 1 -and
    $setPathText -match 'Sites\[\$directSiteName\]' -and
    $setPathText -match 'Sites\[\$gatewaySiteName\]' -and
    $setPathText -match 'Applications\[\$gatewayPath\]') `
    'IIS commit is not limited to the direct Project Tracker root and Portal gateway paths.'

$runtimeDefinitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -in @('Stop-ProjectTrackerRuntime', 'Start-ProjectTrackerRuntime')
}, $true))
Assert-True ($runtimeDefinitions.Count -eq 2) `
    'Could not inspect both scoped Project Tracker runtime control functions.'
$stopRuntimeText = @($runtimeDefinitions | Where-Object Name -EQ 'Stop-ProjectTrackerRuntime')[0].Extent.Text
$startRuntimeText = @($runtimeDefinitions | Where-Object Name -EQ 'Start-ProjectTrackerRuntime')[0].Extent.Text
Assert-True ([regex]::Matches($stopRuntimeText, 'Request-IisState').Count -eq 3 -and
    $stopRuntimeText -match '\$gatewayPoolName' -and
    $stopRuntimeText -match '\$directSiteName' -and
    $stopRuntimeText -match '\$directPoolName' -and
    $stopRuntimeText -notmatch '\$gatewaySiteName') `
    'Scoped stop must affect only the direct site/pool and dedicated gateway pool, never the Portal root site.'
Assert-True ([regex]::Matches($startRuntimeText, 'Request-IisState').Count -eq 3 -and
    [regex]::Matches($startRuntimeText, 'Wait-EndpointHealth').Count -eq 2 -and
    $startRuntimeText -match 'localhost:\$directPort/api/health' -and
    $startRuntimeText -match 'localhost:\$gatewayPort\$gatewayPath/api/health' -and
    $startRuntimeText -notmatch '\$gatewaySiteName') `
    'Scoped start must verify only the direct and gateway health boundaries without restarting the Portal root site.'
Assert-True ($source -match 'Direct Project Tracker authentication must remain Anonymous=True and Windows=True' -and
    $source -match 'Portal gateway authentication must remain Anonymous=False and Windows=True' -and
    $source -match '-not \$directAnonymous -or -not \$directWindows' -and
    $source -match '\$gatewayAnonymous -or -not \$gatewayWindows') `
    'Release preflight does not preserve the direct True/True and gateway False/True authentication boundary.'

# Candidate preparation can take long enough for another deployment to move either IIS path.
# Re-read both paths after every candidate/ACL operation and reject drift before entering the switch.
$aclPrepIndex = $source.LastIndexOf('& icacls.exe $releasePath')
$cutoverReadIndex = $source.IndexOf('$cutoverBoundary = Get-ProjectTrackerIisBoundary', $aclPrepIndex)
$cutoverMismatchIndex = $source.IndexOf('$cutoverBoundary.DirectPath -ine $currentDirectPath', $cutoverReadIndex)
$cutoverGatewayMismatchIndex = $source.IndexOf('$cutoverBoundary.GatewayPath -ine $currentGatewayPath', $cutoverMismatchIndex)
$cutoverThrowIndex = $source.IndexOf('IIS paths changed during candidate preparation', $cutoverGatewayMismatchIndex)
$invokeSwitchIndex = $source.LastIndexOf('Invoke-ProjectTrackerIisSwitch -CurrentDirectPath $currentDirectPath')
Assert-True ($aclPrepIndex -ge 0 -and $cutoverReadIndex -gt $aclPrepIndex -and
    $cutoverMismatchIndex -gt $cutoverReadIndex -and
    $cutoverGatewayMismatchIndex -gt $cutoverMismatchIndex -and
    $cutoverThrowIndex -gt $cutoverGatewayMismatchIndex -and
    $invokeSwitchIndex -gt $cutoverThrowIndex) `
    'The immediate pre-switch IIS boundary re-read is missing, incomplete, or ordered before candidate/ACL preparation.'

$cutoverStatements = @($ast.EndBlock.Statements | Where-Object {
    $_.Extent.Text -match '^\$cutoverBoundary\s*=' -or
    $_.Extent.Text -match 'IIS paths changed during candidate preparation' -or
    $_.Extent.Text -match '^Invoke-ProjectTrackerIisSwitch\s'
} | Sort-Object { $_.Extent.StartOffset })
Assert-True ($cutoverStatements.Count -eq 3) `
    'Could not extract the complete immediate cutover-boundary guard for behavior testing.'
$cutoverGuard = [scriptblock]::Create(($cutoverStatements.Extent.Text -join [Environment]::NewLine))
$script:cutoverDirectPath = ''
$script:cutoverGatewayPath = ''
$script:cutoverSwitchCalls = 0
function Get-ProjectTrackerIisBoundary {
    return [pscustomobject]@{
        DirectPath = $script:cutoverDirectPath
        GatewayPath = $script:cutoverGatewayPath
    }
}
function Invoke-ProjectTrackerIisSwitch {
    param([string]$CurrentDirectPath, [string]$CurrentGatewayPath, [string]$CandidatePath)
    $script:cutoverSwitchCalls++
}
$currentDirectPath = 'C:\releases\active-direct'
$currentGatewayPath = 'C:\releases\active-gateway'
$releasePath = 'C:\releases\candidate'
try {
    foreach ($drift in @('direct', 'gateway')) {
        $script:cutoverDirectPath = $currentDirectPath
        $script:cutoverGatewayPath = $currentGatewayPath
        if ($drift -ceq 'direct') { $script:cutoverDirectPath = 'C:\releases\unexpected-direct' }
        else { $script:cutoverGatewayPath = 'C:\releases\unexpected-gateway' }
        $script:cutoverSwitchCalls = 0
        $rejection = ''
        try { & $cutoverGuard }
        catch { $rejection = $_.Exception.Message }
        Assert-True ($rejection -like '*IIS paths changed during candidate preparation*' -and
            $script:cutoverSwitchCalls -eq 0) `
            "A $drift path drift was accepted or reached the IIS switch: $rejection"
    }

    $script:cutoverDirectPath = $currentDirectPath
    $script:cutoverGatewayPath = $currentGatewayPath
    $script:cutoverSwitchCalls = 0
    & $cutoverGuard
    Assert-True ($script:cutoverSwitchCalls -eq 1) `
        'An unchanged immediate IIS boundary did not enter the switch exactly once.'
}
finally {
    Remove-Item -LiteralPath Function:\Get-ProjectTrackerIisBoundary -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath Function:\Invoke-ProjectTrackerIisSwitch -Force -ErrorAction SilentlyContinue
}

# The catch path must attempt a complete prior-path restore after any live IIS switch failure,
# including failures raised by path apply/CommitChanges and post-start health verification.
$switchDefinitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq 'Invoke-ProjectTrackerIisSwitch'
}, $true))
Assert-True ($switchDefinitions.Count -eq 1) 'Could not identify the release apply/rollback transaction.'
$transactionText = $switchDefinitions[0].Extent.Text
$switchAttemptIndex = $transactionText.IndexOf('$switchAttempted = $true')
$applyIndex = $transactionText.IndexOf('Set-ProjectTrackerPhysicalPaths -DirectPath $CandidatePath -GatewayPhysicalPath $CandidatePath')
$startIndex = $transactionText.IndexOf('Start-ProjectTrackerRuntime')
$catchIndex = $transactionText.IndexOf('catch')
$rollbackStopIndex = $transactionText.IndexOf('Stop-ProjectTrackerRuntime', $catchIndex)
$rollbackPathIndex = $transactionText.IndexOf('Set-ProjectTrackerPhysicalPaths -DirectPath $CurrentDirectPath', $catchIndex)
$rollbackAssertIndex = $transactionText.IndexOf('Assert-ProjectTrackerPhysicalPaths -ExpectedDirectPath $CurrentDirectPath', $catchIndex)
$rollbackStartIndex = $transactionText.IndexOf('Start-ProjectTrackerRuntime', $catchIndex)
$rollbackFinalAssertIndex = $transactionText.IndexOf('Assert-ProjectTrackerPhysicalPaths -ExpectedDirectPath $CurrentDirectPath', $rollbackAssertIndex + 1)
Assert-True ($switchAttemptIndex -ge 0 -and $applyIndex -gt $switchAttemptIndex -and
    $startIndex -gt $applyIndex -and $catchIndex -gt $startIndex -and
    $rollbackStopIndex -gt $catchIndex -and $rollbackPathIndex -gt $rollbackStopIndex -and
    $rollbackAssertIndex -gt $rollbackPathIndex -and $rollbackStartIndex -gt $rollbackAssertIndex -and
    $rollbackFinalAssertIndex -gt $rollbackStartIndex) `
    'Apply, CommitChanges, or health failures are not followed by ordered stop/path-restore/start-health rollback.'
Assert-True ($transactionText -match 'both prior IIS paths were restored healthy' -and
    $transactionText -match 'Rollback also reported') `
    'Rollback success and rollback-failure outcomes are not distinguished.'

# Execute the actual switch helper with controlled failures. Distinct prior direct and gateway
# paths prove that rollback does not assume they are interchangeable or restore only one side.
. (Get-TestableFunctions -Ast $ast -Names @('Invoke-ProjectTrackerIisSwitch'))
$script:switchScenario = ''
$script:switchEvents = @()
$script:activeDirectPath = ''
$script:activeGatewayPath = ''
$script:candidatePath = 'C:\releases\candidate'
$script:priorDirectPath = 'C:\releases\prior-direct'
$script:priorGatewayPath = 'C:\releases\prior-gateway'
$script:scenarioFailureRaised = $false

function Stop-ProjectTrackerRuntime {
    $script:switchEvents += 'stop'
}
function Set-ProjectTrackerPhysicalPaths {
    param([string]$DirectPath, [string]$GatewayPhysicalPath)
    $script:switchEvents += "set:$DirectPath|$GatewayPhysicalPath"
    if ($DirectPath -ceq $script:candidatePath -and -not $script:scenarioFailureRaised) {
        if ($script:switchScenario -ceq 'apply') {
            $script:scenarioFailureRaised = $true
            throw 'Simulated path apply failure.'
        }
        if ($script:switchScenario -ceq 'commit') {
            # Model a partial IIS commit: direct changed, gateway did not, and CommitChanges threw.
            $script:activeDirectPath = $DirectPath
            $script:scenarioFailureRaised = $true
            throw 'Simulated partial CommitChanges failure.'
        }
    }
    $script:activeDirectPath = $DirectPath
    $script:activeGatewayPath = $GatewayPhysicalPath
}
function Assert-ProjectTrackerPhysicalPaths {
    param([string]$ExpectedDirectPath, [string]$ExpectedGatewayPath)
    $script:switchEvents += "assert:$ExpectedDirectPath|$ExpectedGatewayPath"
    if ($script:activeDirectPath -cne $ExpectedDirectPath -or
        $script:activeGatewayPath -cne $ExpectedGatewayPath) {
        throw 'Simulated physical path verification failure.'
    }
}
function Start-ProjectTrackerRuntime {
    $script:switchEvents += 'start-health'
    if ($script:switchScenario -ceq 'health' -and
        $script:activeDirectPath -ceq $script:candidatePath -and
        -not $script:scenarioFailureRaised) {
        $script:scenarioFailureRaised = $true
        throw 'Simulated candidate health failure.'
    }
}

foreach ($scenario in @('apply', 'commit', 'health')) {
    $script:switchScenario = $scenario
    $script:switchEvents = @()
    $script:activeDirectPath = $script:priorDirectPath
    $script:activeGatewayPath = $script:priorGatewayPath
    $script:scenarioFailureRaised = $false
    $failure = ''
    try {
        Invoke-ProjectTrackerIisSwitch -CurrentDirectPath $script:priorDirectPath `
            -CurrentGatewayPath $script:priorGatewayPath -CandidatePath $script:candidatePath
    }
    catch { $failure = $_.Exception.Message }

    Assert-True ($failure -like '*both prior IIS paths were restored healthy*') `
        "The $scenario failure did not report a verified successful rollback: $failure"
    Assert-True ($script:activeDirectPath -ceq $script:priorDirectPath -and
        $script:activeGatewayPath -ceq $script:priorGatewayPath) `
        "The $scenario failure did not restore both distinct prior paths."
    Assert-True (@($script:switchEvents | Where-Object { $_ -eq 'stop' }).Count -eq 2) `
        "The $scenario failure did not perform scoped initial stop and rollback stop."
    $expectedFinalAssertion = "assert:$($script:priorDirectPath)|$($script:priorGatewayPath)"
    Assert-True ($script:switchEvents[-1] -ceq $expectedFinalAssertion -and
        @($script:switchEvents | Where-Object { $_ -ceq $expectedFinalAssertion }).Count -eq 2) `
        "The $scenario rollback did not reassert both prior paths after runtime health verification."
    Assert-True ($script:switchEvents -contains "set:$($script:priorDirectPath)|$($script:priorGatewayPath)") `
        "The $scenario rollback did not restore the independently captured direct and gateway paths."
}

# Exercise the sanitized copy helper against real files under Windows PowerShell 5.1. Package
# Production/Development settings must never enter the candidate; the top-level transaction then
# installs only the hash-verified active Production settings checked above.
. (Get-TestableFunctions -Ast $ast -Names @('Copy-SanitizedApplication'))
$copyRoot = Join-Path ([IO.Path]::GetTempPath()) ('sonaero-project-tracker-release-' + [Guid]::NewGuid().ToString('N'))
$copySource = Join-Path $copyRoot 'source'
$copyDestination = Join-Path $copyRoot 'candidate'
New-Item -ItemType Directory -Path (Join-Path $copySource 'nested') -Force | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $copySource 'ProjectTracker.Api.dll'), 'candidate-binary')
    [IO.File]::WriteAllText((Join-Path $copySource 'web.config'), '<configuration />')
    [IO.File]::WriteAllText((Join-Path $copySource 'appsettings.Production.json'), '{"unsafe":"package"}')
    [IO.File]::WriteAllText((Join-Path $copySource 'appsettings.Development.json'), '{"unsafe":"development"}')
    [IO.File]::WriteAllText((Join-Path $copySource 'nested\asset.txt'), 'asset')

    Copy-SanitizedApplication -Source $copySource -Destination $copyDestination
    Assert-True (Test-Path -LiteralPath (Join-Path $copyDestination 'ProjectTracker.Api.dll')) `
        'Sanitized copy omitted the Project Tracker application DLL.'
    Assert-True (Test-Path -LiteralPath (Join-Path $copyDestination 'nested\asset.txt')) `
        'Sanitized copy omitted an ordinary nested asset.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $copyDestination 'appsettings.Production.json'))) `
        'Sanitized copy accepted package-supplied Production settings.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $copyDestination 'appsettings.Development.json'))) `
        'Sanitized copy accepted Development settings.'
}
finally {
    if (Test-Path -LiteralPath $copyRoot) {
        Remove-Item -LiteralPath $copyRoot -Recurse -Force
    }
}

Write-Output 'PROJECT_TRACKER_RELEASE_TESTS_PASSED'
