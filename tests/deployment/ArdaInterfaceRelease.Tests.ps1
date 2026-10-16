[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Run these tests in Windows PowerShell 5.1.' }

$scriptPath = Join-Path $PSScriptRoot '..\..\deployment\Invoke-ArdaInterfaceRelease.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $scriptPath), [ref]$tokens, [ref]$parseErrors)
if (@($parseErrors).Count) { throw 'Release wrapper does not parse in Windows PowerShell 5.1.' }
$outerTry = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.TryStatementAst]
}, $true))[0]
if ($null -eq $outerTry -or $outerTry.CatchClauses.Count -ne 1) { throw 'Expected one guarded release transaction.' }

# Exercise the real operational statements, after platform/identity validation. All
# I/O commands below are local mocks. Replace only the two explicit script invocations.
$body = $outerTry.Body.Extent.Text
$start = $body.IndexOf('Set-Location -LiteralPath $repositoryPath')
if ($start -lt 0) { throw 'Cannot locate wrapper operational boundary.' }
$fixtureSource = 'try {' + $body.Substring($start) + $outerTry.CatchClauses[0].Extent.Text
foreach ($replacement in @(
    @('& .\deployment\Publish-Hub.ps1', 'Invoke-FakePublish'),
    @('& .\deployment\Deploy-HubRelease.ps1', 'Invoke-FakeDeploy')
)) {
    if (-not $fixtureSource.Contains($replacement[0])) { throw 'Cannot replace a deployment command with its mock.' }
    $fixtureSource = $fixtureSource.Replace($replacement[0], $replacement[1])
}
if ($fixtureSource -match '&\s*\.\\deployment\\') { throw 'An unmocked deployment invocation remains.' }
$fixtureBlock = [scriptblock]::Create($fixtureSource)
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw $Message }
}

function Invoke-WrapperFixture {
    param([string]$Scenario = '', [bool]$InstalledSubcontracting = $false)
    $ExpectedCommit = '0123456789abcdef0123456789abcdef01234567'
    $repositoryPath = 'C:\SonAero\src\SonAeroInternalHub'
    $verifiedApply = $false
    $state = @{
        Published = $false; GitCalls = @(); DeployCalls = @(); Warnings = @()
        PublishArguments = $null; HealthCalls = @(); Error = $null; Output = @()
    }
    function Set-Location { param([string]$LiteralPath)
        if ($LiteralPath -cne $repositoryPath) { throw 'Unexpected repository path.' }
    }
    function git {
        $global:LASTEXITCODE = 0
        $command = $args -join ' '
        $state.GitCalls += $command
        switch ($command) {
            'branch --show-current' { if ($Scenario -eq 'wrong-branch') { 'feature' } else { 'main' } }
            'status --porcelain' {
                if ($Scenario -eq 'dirty' -or ($Scenario -eq 'dirty-build' -and $state.Published)) { ' M preserved.txt' }
            }
            'fetch --prune origin' { if ($Scenario -eq 'git-failure') { $global:LASTEXITCODE = 1 } }
            'rev-parse origin/main' { if ($Scenario -eq 'wrong-remote') { 'f' * 40 } else { $ExpectedCommit } }
            'pull --ff-only origin main' { }
            'rev-parse HEAD' { if ($Scenario -eq 'wrong-head') { 'f' * 40 } else { $ExpectedCommit } }
            default { throw "Unexpected Git command: $command" }
        }
    }
    function Import-Module { param([string]$Name, [string]$ErrorAction)
        if ($Name -cne 'WebAdministration') { throw "Unexpected import: $Name" }
    }
    function Test-Path { param([string]$Path, [string]$LiteralPath)
        $value = if ($LiteralPath) { $LiteralPath } else { $Path }
        if ($value -eq 'IIS:\Sites\SmallBusinessSubcontracting') { return $InstalledSubcontracting }
        if ($value -like 'IIS:\Sites\*') { return $true }
        if ($value -like 'C:\SonAero\staging\*' -or $value -like 'C:\SonAero\releases\*') {
            return $Scenario -eq 'existing-path'
        }
        throw "Unexpected path probe: $value"
    }
    function Get-Item { param([string]$Path)
        if ($Path -like 'IIS:\AppPools\*') {
            return [pscustomobject]@{
                startMode = $(if ($Scenario -eq 'idle-pool') { 'OnDemand' } else { 'AlwaysRunning' })
                autoStart = ($Scenario -ne 'no-pool-autostart')
                state = $(if ($Scenario -eq 'stopped-pool') { 'Stopped' } else { 'Started' })
                processModel = [pscustomobject]@{ idleTimeout = [TimeSpan]::Zero }
            }
        }
        if ($Path -like 'IIS:\Sites\*') {
            return [pscustomobject]@{ state = 'Started'; serverAutoStart = ($Scenario -ne 'no-site-autostart') }
        }
        throw "Unexpected IIS object: $Path"
    }
    function Get-WebConfigurationProperty { param([string]$PSPath, [string]$Filter, [string]$Name)
        if ($Name -cne 'preloadEnabled') { throw 'Unexpected IIS attribute request.' }
        return [pscustomobject]@{ Value = ($Scenario -ne 'no-preload') }
    }
    function Get-WebAppPoolState { param([string]$Name)
        return [pscustomobject]@{ Value = $(if ($Scenario -eq 'stopped-pool') { 'Stopped' } else { 'Started' }) }
    }
    function Read-Host { param([string]$Prompt)
        if ($Scenario -eq 'backup-refused') { 'no' } else { 'BACKUPS_VERIFIED' }
    }
    function Get-Date { param([string]$Format) '20261016-120000' }
    function Write-Host { }
    function Out-Host { process { } }
    function Write-Warning { param([string]$Message) $state.Warnings += $Message }
    function Invoke-FakePublish {
        param([string]$OutputRoot, [string]$ProjectTrackerUrl, [string]$Configuration,
            [switch]$IncludeSmallBusinessSubcontracting)
        $state.Published = $true
        $state.PublishArguments = $PSBoundParameters
    }
    function Invoke-FakeDeploy {
        [CmdletBinding(SupportsShouldProcess)]
        param([string]$PackageRoot, [string]$ReleaseId, [string]$ReleaseRoot, [int]$HealthTimeoutSeconds,
            [switch]$EnableUniversalFulcrumSync, [switch]$IncludeSmallBusinessSubcontracting)
        $record = @{}; foreach ($key in $PSBoundParameters.Keys) { $record[$key] = $PSBoundParameters[$key] }
        $state.DeployCalls += $record
        if ($WhatIfPreference) {
            if ($Scenario -eq 'bad-preview') { 'WHATIF_READY extra' } else { 'WHATIF_READY' }
        }
        elseif ($Scenario -eq 'bad-apply') { 'HUB_RELEASE_DEPLOYED_AND_HEALTHY extra' }
        else { 'HUB_RELEASE_DEPLOYED_AND_HEALTHY' }
    }
    function Invoke-WebRequest {
        param([string]$Uri, [switch]$UseBasicParsing, [switch]$UseDefaultCredentials, [int]$TimeoutSec)
        $state.HealthCalls += $Uri
        if ($Scenario -eq 'postcheck-failure') { throw 'Simulated HTTPS health failure.' }
        return [pscustomobject]@{ StatusCode = 200 }
    }
    try { $state.Output = @(& $fixtureBlock) } catch { $state.Error = $_.Exception.Message }
    return $state
}

foreach ($installed in @($false, $true)) {
    $result = Invoke-WrapperFixture -InstalledSubcontracting $installed
    Assert-True ($null -eq $result.Error) "Valid release failed: $($result.Error)"
    Assert-True ($result.DeployCalls.Count -eq 2) 'Expected exactly preview then apply.'
    Assert-True ([bool]$result.PublishArguments.IncludeSmallBusinessSubcontracting -eq $installed) 'Publisher module selection differs from installed IIS.'
    Assert-True ([bool]$result.DeployCalls[0].IncludeSmallBusinessSubcontracting -eq $installed) 'Deployment module selection differs from installed IIS.'
    foreach ($name in @('PackageRoot','ReleaseId','ReleaseRoot','HealthTimeoutSeconds','EnableUniversalFulcrumSync','IncludeSmallBusinessSubcontracting')) {
        Assert-True ($result.DeployCalls[0][$name] -eq $result.DeployCalls[1][$name]) "Preview/apply changed $name."
    }
    Assert-True ($result.PublishArguments.OutputRoot -ceq $result.DeployCalls[0].PackageRoot) 'Deployment used a different package.'
    Assert-True ($result.DeployCalls[0].PackageRoot -ceq 'C:\SonAero\staging\arda-0123456789ab-20261016-120000') 'Package path does not identify SHA/time.'
    Assert-True ($result.DeployCalls[0].HealthTimeoutSeconds -eq 300 -and $result.DeployCalls[0].EnableUniversalFulcrumSync) 'Expected guarded release policy.'
    Assert-True ([bool]$result.DeployCalls[0].WhatIf -and $result.DeployCalls[1].Confirm -eq $false) 'Preview/apply invocation modes differ.'
    Assert-True ($result.HealthCalls.Count -eq $(if ($installed) { 7 } else { 6 })) 'Wrong number of HTTPS service checks.'
    Assert-True ($result.Output -contains 'ARDA_INTERFACE_RELEASE_DEPLOYED_HEALTHY') 'Final success marker missing.'
}
foreach ($scenario in @('wrong-branch','dirty','git-failure','wrong-remote','wrong-head','backup-refused','existing-path','idle-pool','no-preload','no-pool-autostart','no-site-autostart','stopped-pool')) {
    $result = Invoke-WrapperFixture -Scenario $scenario
    Assert-True ($null -ne $result.Error) "Guard did not stop $scenario."
    Assert-True (-not $result.Published -and $result.DeployCalls.Count -eq 0) "Guard $scenario reached publishing/deployment."
}
$result = Invoke-WrapperFixture -Scenario 'dirty-build'
Assert-True ($null -ne $result.Error -and $result.Published -and $result.DeployCalls.Count -eq 0) 'Build-modified checkout reached deployment.'
$result = Invoke-WrapperFixture -Scenario 'bad-preview'
Assert-True ($null -ne $result.Error -and $result.DeployCalls.Count -eq 1) 'Invalid preview marker reached apply.'
$result = Invoke-WrapperFixture -Scenario 'bad-apply'
Assert-True ($null -ne $result.Error -and $result.HealthCalls.Count -eq 0) 'Invalid apply marker reached postchecks.'
$result = Invoke-WrapperFixture -Scenario 'postcheck-failure'
Assert-True ($null -ne $result.Error -and $result.DeployCalls.Count -eq 2) 'Postcheck failure retried deployment.'
Assert-True (@($result.Warnings | Where-Object { $_ -like 'POSTCHECK_STOP_AFTER_VERIFIED_APPLY:*' }).Count -eq 1) 'Postcheck failure was confused with apply failure.'
Write-Output "ARDA_INTERFACE_RELEASE_TESTS_PASSED ($script:assertions assertions)"
