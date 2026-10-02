[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Run these compatibility tests in Windows PowerShell 5.1.' }
$deploymentRoot = Join-Path $PSScriptRoot '..\..\deployment'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Get-ScriptAst([string]$Name) {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $deploymentRoot $Name), [ref]$tokens, [ref]$errors)
    Assert-True ($errors.Count -eq 0) "$Name has syntax errors: $($errors.Message -join '; ')"
    return $ast
}
function Get-Inventory($Ast, [string]$VariableName, [bool]$Include) {
    $initial = @($Ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
        $_.Left.Extent.Text -ceq ('$' + $VariableName) -and $_.Operator -eq 'Equals'
    })
    Assert-True ($initial.Count -eq 1) "Missing base inventory: $VariableName"
    Invoke-Expression $initial[0].Extent.Text
    $selection = @($Ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.IfStatementAst] -and
        $_.Extent.Text.StartsWith('if ($IncludeSmallBusinessSubcontracting)') -and
        $_.Extent.Text.Contains(('$' + $VariableName + ' +='))
    })
    Assert-True ($selection.Count -eq 1) "Missing explicit opt-in inventory: $VariableName"
    if ($Include) {
        $addition = @($selection[0].FindAll({
            param($node)
            $node -is [Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -ceq ('$' + $VariableName) -and $node.Operator -eq 'PlusEquals'
        }, $true))
        Assert-True ($addition.Count -eq 1) "Ambiguous opt-in inventory: $VariableName"
        Invoke-Expression $addition[0].Extent.Text
    }
    $inventory = Get-Variable -Name $VariableName -ValueOnly
    foreach ($item in $inventory) { Write-Output $item }
}

$releaseAst = Get-ScriptAst 'Deploy-HubRelease.ps1'
$publishAst = Get-ScriptAst 'Publish-Hub.ps1'
$iisAst = Get-ScriptAst 'Configure-IisServer.ps1'
$sqlAst = Get-ScriptAst 'Configure-SqlServer.ps1'
$backupAst = Get-ScriptAst 'Test-HubBackupReadiness.ps1'
foreach ($entry in @(@($releaseAst, 'applications'), @($publishAst, 'applications'), @($iisAst, 'sites'))) {
    $baseline = @(Get-Inventory $entry[0] $entry[1] $false)
    $extended = @(Get-Inventory $entry[0] $entry[1] $true)
    Assert-True ($baseline.Count -eq 5 -and $baseline.Name -notcontains 'SmallBusinessSubcontracting') `
        "Default deployment inventory must remain compatible with the existing five applications. Count=$($baseline.Count); Names=$($baseline.Name -join ',')"
    Assert-True ($extended.Count -eq 6 -and @($extended | Where-Object Name -EQ 'SmallBusinessSubcontracting').Count -eq 1) `
        'Explicit six-application selection did not require exactly one SmallBusinessSubcontracting entry.'
}
$applications = @(Get-Inventory $releaseAst 'applications' $true)
$subcontracting = @($applications | Where-Object Name -EQ 'SmallBusinessSubcontracting')[0]
Assert-True ($subcontracting.Folder -ceq 'SmallBusinessSubcontracting' -and
    $subcontracting.Port -eq 5180 -and $subcontracting.MainDll -ceq 'SmallBusinessSubcontracting.Api.dll') `
    'Full-Hub and first-install module identities are inconsistent.'

$source = $releaseAst.Extent.Text
foreach ($contract in @(
    'SmallBusinessSubcontracting is installed. A full Hub release must specify -IncludeSmallBusinessSubcontracting',
    'WHATIF_READY_HUB_RELEASE_WITH_SMALL_BUSINESS_SUBCONTRACTING',
    'HUB_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SMALL_BUSINESS_SUBCONTRACTING',
    'Read-SubcontractingProductionConfiguration -Path $productionSettings',
    'Read-SubcontractingProductionConfiguration -Path $candidateProductionSettings',
    'Assert-SubcontractingWebConfig -Path $sourceWebConfig',
    'Assert-SubcontractingWebConfig -Path $candidateWebConfig',
    'Subcontracting effective IIS environment',
    'SmallBusinessSubcontracting must disable anonymous and enable Windows authentication.'
)) { Assert-True $source.Contains($contract) "Missing integration contract: $contract" }
$preflight = $source.IndexOf('Read-SubcontractingProductionConfiguration -Path $productionSettings')
$approval = $source.IndexOf('if (-not $PSCmdlet.ShouldProcess(')
$candidate = $source.IndexOf('Read-SubcontractingProductionConfiguration -Path $candidateProductionSettings')
$mutation = $source.IndexOf('$liveIisTouched = $true')
Assert-True ($preflight -lt $approval -and $candidate -gt $approval -and $candidate -lt $mutation) `
    'Subcontracting config must be validated before preview approval and again before live IIS changes.'
Assert-True ($source -match '(?s)foreach \(\$application in \$applications\).*?Package application folder is missing:') `
    'An explicit six-application package can silently skip a missing module folder.'

$definitions = @($releaseAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -in @('Start-HubApplications', 'Stop-HubApplications', 'Get-HealthResult')
}, $true))
Invoke-Expression (($definitions.Extent.Text) -join [Environment]::NewLine)
$script:calls = New-Object 'System.Collections.Generic.List[string]'
$projectTrackerGateway = [pscustomobject]@{ Site = 'SonAeroPortal'; Pool = 'ProjectTrackerAdminGateway' }
$HealthTimeoutSeconds = 60
function Start-OneApplication($Application) { $script:calls.Add("start:$($Application.Name)") }
function Wait-ApplicationHealth($Targets, $TimeoutSeconds) { $script:calls.Add("health:$($Targets[0].Name)") }
function Request-IisState($Kind, $Name, $State) { $script:calls.Add("$State`:$Kind`:$Name") }
function Wait-IisState($Kind, $Names, $State) { }
function Wait-ProjectTrackerGatewayHealth($TimeoutSeconds) { $script:calls.Add('health:gateway') }
$deploymentApplications = @($applications)
Start-HubApplications
Assert-True (($script:calls | Select-Object -Last 2) -join '|' -ceq
    'start:SmallBusinessSubcontracting|health:SmallBusinessSubcontracting') `
    'Six-application startup must wait for Subcontracting health after prior applications.'
Assert-True ($script:calls.IndexOf('health:QualityAssurance') -lt $script:calls.IndexOf('start:SmallBusinessSubcontracting')) `
    'Subcontracting started before the previous SQL application was healthy.'
$script:calls.Clear()
Stop-HubApplications
Assert-True ($script:calls.Contains('Stopped:Site:SmallBusinessSubcontracting') -and
    $script:calls.Contains('Stopped:Pool:SmallBusinessSubcontracting')) `
    'Six-application release/rollback failed to stop the new site and pool.'
$script:calls.Clear()
$deploymentApplications = @($applications | Where-Object Name -NE 'QualityAssurance')
Start-HubApplications
Assert-True ($script:calls.Contains('health:SmallBusinessSubcontracting') -and
    -not $script:calls.Contains('start:QualityAssurance')) `
    'Retained Quality must still update and health-gate the selected sixth application.'

Import-Module (Join-Path $deploymentRoot 'SubcontractingProductionConfiguration.psm1') -Force
$script:healthBody = @{ status='ok'; databaseProvider='SqlServer'; migrations='ready'; roleStore='ready'; documentStorage='ready' }
function Invoke-WebRequest {
    param([switch]$UseBasicParsing, [switch]$UseDefaultCredentials, $Uri, $TimeoutSec)
    return [pscustomobject]@{ StatusCode = 200; Content = ($script:healthBody | ConvertTo-Json) }
}
Assert-True (Get-HealthResult $subcontracting).Healthy 'Approved SQL/storage health response was rejected.'
$script:healthBody.documentStorage = 'unavailable'
$result = Get-HealthResult $subcontracting
Assert-True (-not $result.Healthy -and $result.Detail -like '*Health response*') `
    'A bare HTTP 200 with failed document readiness must trigger full-Hub rollback.'
$script:healthBody = @{ status = 'ok' }
Assert-True (-not (Get-HealthResult $subcontracting).Healthy) 'Bare ok cannot prove module database/storage readiness.'
Assert-True (Get-HealthResult ([pscustomobject]@{Name='QualityAssurance';Port=5170})).Healthy `
    'Subcontracting health requirements leaked into legacy Quality readiness.'

$sqlFunction = @($sqlAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Configure-HubDatabases'
}, $true))[0]
Invoke-Expression $sqlFunction.Extent.Text
$script:sqlCalls = New-Object 'System.Collections.Generic.List[object]'
function Invoke-HubSql($server, $database, $commandText, $applicationName) {
    $script:sqlCalls.Add([pscustomobject]@{ Database=$database; Command=$commandText })
    return 1
}
$IisComputerAccount = 'SON4L\SON-IIS2$'
$localSqlServer = '(local)'
$IncludeSmallBusinessSubcontracting = $false
Configure-HubDatabases 'Test'
Assert-True (@($script:sqlCalls | Where-Object { $_.Command -match 'SmallBusinessSubcontracting' }).Count -eq 0) `
    'Default SQL setup unexpectedly provisions the unselected module.'
$script:sqlCalls.Clear()
$IncludeSmallBusinessSubcontracting = $true
Configure-HubDatabases 'Test'
Assert-True (@($script:sqlCalls | Where-Object { $_.Command -match 'CREATE DATABASE \[SmallBusinessSubcontracting\]' }).Count -eq 1) `
    'Explicit SQL setup did not provision the module database.'
Assert-True (@($script:sqlCalls | Where-Object Database -EQ 'SmallBusinessSubcontracting').Count -eq 1) `
    'Explicit SQL setup did not grant the module database permissions.'

$measureFunction = @($backupAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Measure-SubcontractingBackupDocuments'
}, $true))[0]
Invoke-Expression $measureFunction.Extent.Text
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('subcontracting-backup-test-' + [Guid]::NewGuid().ToString('N'))
try {
    [void](New-Item -ItemType Directory -Path (Join-Path $fixture 'nested'))
    [IO.File]::WriteAllBytes((Join-Path $fixture 'one.dat'), [byte[]](1,2,3))
    [IO.File]::WriteAllBytes((Join-Path $fixture 'nested\two.dat'), [byte[]](4,5))
    $measurement = Measure-SubcontractingBackupDocuments $fixture
    Assert-True ($measurement.FileCount -eq 2 -and $measurement.Bytes -eq 5) `
        'Backup capacity did not include all module document bytes.'
    foreach ($invalid in @('relative\documents', '\\SON-SQL2\share\documents', ($fixture + '\..\other'))) {
        $failed = $false
        try { [void](Measure-SubcontractingBackupDocuments $invalid) } catch { $failed = $true }
        Assert-True $failed 'Backup document inventory accepted a nonlocal or traversal path.'
    }
    function Get-ChildItem {
        param($LiteralPath, [switch]$Force, $ErrorAction)
        [pscustomobject]@{ Attributes = [IO.FileAttributes]::ReparsePoint; FullName = (Join-Path $LiteralPath 'linked'); PSIsContainer = $true }
    }
    try {
        $linkFailure = ''
        try { [void](Measure-SubcontractingBackupDocuments $fixture) } catch { $linkFailure = $_.Exception.Message }
        Assert-True ($linkFailure -like '*contains a reparse point*') `
            'Backup measurement traversed or counted an unapproved linked document directory.'
    }
    finally { Remove-Item -LiteralPath 'Function:\Get-ChildItem' }
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $approvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedFixture.StartsWith($approvedTemp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
Assert-True ($backupAst.Extent.Text.Contains('$drawingBytes + $databaseBytes + $subcontractingBytes')) `
    'Backup recovery-set capacity excludes selected module documents.'
Assert-True ($backupAst.Extent.Text.Contains('@includeSubcontracting = 1')) `
    'Backup database audit does not cover the opted-in operational database.'
Write-Output 'SMALL_BUSINESS_SUBCONTRACTING_HUB_INTEGRATION_TESTS_PASSED'
