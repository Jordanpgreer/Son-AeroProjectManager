[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this compatibility test with Windows PowerShell 5.1.'
}
$modulePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'ArdaFulcrumReleaseConfiguration.psm1'
$tokens = $null
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile($modulePath, [ref]$tokens, [ref]$parseErrors)
if (@($parseErrors).Count -ne 0) { throw 'Release configuration module has a PowerShell parse error.' }
Import-Module $modulePath -Force
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw $Message }
}
function Assert-Rejected([scriptblock]$Action, [string]$Expected) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_ }
    Assert-True ($null -ne $failure) 'Unsafe configuration was unexpectedly accepted.'
    Assert-True ($failure.Exception.Message -match $Expected) 'Failure did not identify the expected guard.'
    Assert-True ($failure.Exception.Message -notmatch 'fixture-private-value') 'Error exposed configuration contents.'
}
function Json([object]$Value) { ConvertTo-Json -InputObject $Value -Depth 100 -Compress }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('arda-fulcrum-release-test-' + [Guid]::NewGuid().ToString('N'))
$release = Join-Path $testRoot 'candidate'
$activeRoot = Join-Path $testRoot 'active'
$fixture = @'
{
  "ConnectionStrings": {"ModuleAccessStore":"fixture-private-value", "Other":"a;Password=x&y;"},
  "Authentication":{"Mode":"Windows"},
  "Custom":{"Empty":[],"One":["a"],"Flag":false,"Null":null,"Nested":[{"X":9007199254740991,"Y":"2026-10-16T10:00:00Z"}]},
  "UniversalFulcrumSync":{"Enabled":false,"IntervalMinutes":60,"MaximumConcurrentRequests":1,"MinimumRequestSpacingMilliseconds":900,"MaximumStaleMinutes":0,"MaximumResponseBytes":2000000,"Future":{"Token":"fixture-private-value"}},
  "QualityIntegration":{"Enabled":false,"FulcrumShipmentUrlTemplate":"","PageSize":777,"FulcrumBaseUrl":"https://api.fulcrumpro.us/","Custom":"fixture-private-value"},
  "QualityDatabase":{"Provider":"Sqlite","StorageMode":"ServerLocalSqlite"},
  "AllowedHosts":"custom.example.local"
}
'@
try {
    foreach ($application in @('ProjectTracker','EstimatingDashboard','QualityAssurance','SmallBusinessSubcontracting')) {
        $candidate = Join-Path $release $application
        $active = Join-Path $activeRoot $application
        [void](New-Item -ItemType Directory -Path $candidate -Force)
        [void](New-Item -ItemType Directory -Path $active -Force)
        $path = Join-Path $candidate 'appsettings.Production.json'
        $activePath = Join-Path $active 'appsettings.Production.json'
        [IO.File]::WriteAllText($path, $fixture)
        [IO.File]::WriteAllText($activePath, $fixture)
        $hash = (Get-FileHash -LiteralPath $path).Hash
        $parameters = @{
            ApplicationName = $application; CandidateApplicationPath = $candidate
            ActiveApplicationPath = $active; ReleasePath = $release
        }
        $result = Set-ArdaFulcrumCandidateConfiguration @parameters
        Assert-True (-not $result.Applied) 'An omitted opt-in must not apply policy.'
        Assert-True ((Get-FileHash -LiteralPath $path).Hash -eq $hash) 'An omitted opt-in changed candidate bytes.'
        Test-ArdaFulcrumProductionConfiguration -ApplicationName $application -ProductionSettingsPath $path
        Assert-True ((Get-FileHash -LiteralPath $path).Hash -eq $hash) 'Read-only preflight changed candidate bytes.'
        $result = Set-ArdaFulcrumCandidateConfiguration @parameters -EnableUniversalFulcrumSync
        Assert-True ($result.Applied) 'Explicit opt-in did not apply policy.'
        Assert-True ((Json $result) -notmatch 'fixture-private-value') 'Result exposed a private field.'
        Assert-True ((Get-FileHash -LiteralPath $activePath).Hash -eq $hash) 'Active Production file changed.'
        $before = $fixture | ConvertFrom-Json
        $after = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        foreach ($name in @('ConnectionStrings','Authentication','Custom','QualityDatabase','AllowedHosts')) {
            Assert-True ((Json $before.$name) -ceq (Json $after.$name)) "Unrelated '$name' configuration changed."
        }
        $sync = $after.UniversalFulcrumSync
        Assert-True ($sync.Enabled -is [bool] -and $sync.Enabled) 'Enabled must be a JSON true.'
        Assert-True ($sync.IntervalMinutes -eq 20 -and $sync.MaximumConcurrentRequests -eq 2 -and
            $sync.MinimumRequestSpacingMilliseconds -eq 250 -and $sync.MaximumStaleMinutes -eq 1440) 'Sync policy differs.'
        Assert-True ($sync.MaximumResponseBytes -eq 2000000 -and (Json $sync.Future) -ceq
            (Json $before.UniversalFulcrumSync.Future)) 'Unknown sync settings changed.'
        if ($application -eq 'QualityAssurance') {
            Assert-True ($after.QualityIntegration.Enabled -and $after.QualityIntegration.FulcrumShipmentUrlTemplate -ceq
                'https://son-aero.fulcrumpro.com/ui/ship/{id}/details') 'Quality policy differs.'
            foreach ($name in @('PageSize','FulcrumBaseUrl','Custom')) {
                Assert-True ((Json $after.QualityIntegration.$name) -ceq (Json $before.QualityIntegration.$name)) 'Quality custom setting changed.'
            }
            Assert-True ($result.ChangedSettings.Count -eq 7) 'Quality must change exactly seven known fields.'
        }
        else {
            Assert-True ((Json $after.QualityIntegration) -ceq (Json $before.QualityIntegration)) 'Non-Quality module received Quality changes.'
            Assert-True ($result.ChangedSettings.Count -eq 5) 'Other modules must change exactly five known fields.'
        }
        $updatedHash = (Get-FileHash -LiteralPath $path).Hash
        $second = Set-ArdaFulcrumCandidateConfiguration @parameters -EnableUniversalFulcrumSync
        Assert-True ($second.ChangedSettings.Count -eq 0 -and
            (Get-FileHash -LiteralPath $path).Hash -eq $updatedHash) 'Repeated policy was not byte-idempotent.'
    }

    # Remaining checks use the final Subcontracting candidate, and must leave rejected files untouched.
    $bad = $parameters.Clone(); $bad.ApplicationName = 'Portal'
    Assert-Rejected { Set-ArdaFulcrumCandidateConfiguration @bad -EnableUniversalFulcrumSync } 'ValidateSet|does not belong'
    $bad = $parameters.Clone(); $bad.ApplicationName = 'EngineeringHub'
    Assert-Rejected { Set-ArdaFulcrumCandidateConfiguration @bad -EnableUniversalFulcrumSync } 'ValidateSet|does not belong'
    $bad = $parameters.Clone(); $bad.ActiveApplicationPath = $candidate
    Assert-Rejected { Set-ArdaFulcrumCandidateConfiguration @bad -EnableUniversalFulcrumSync } 'overlap'
    $bad = $parameters.Clone(); $bad.CandidateApplicationPath = $active
    Assert-Rejected { Set-ArdaFulcrumCandidateConfiguration @bad -EnableUniversalFulcrumSync } 'directly inside'
    $bad = $parameters.Clone(); $bad.ReleasePath = 'relative'
    Assert-Rejected { Set-ArdaFulcrumCandidateConfiguration @bad -EnableUniversalFulcrumSync } 'absolute'
    $junctionPath = Join-Path $testRoot 'junction-release'
    [void](New-Item -ItemType Junction -Path $junctionPath -Target $release)
    try {
        $bad = $parameters.Clone(); $bad.ReleasePath = $junctionPath
        $bad.CandidateApplicationPath = Join-Path $junctionPath $parameters.ApplicationName
        Assert-Rejected { Set-ArdaFulcrumCandidateConfiguration @bad -EnableUniversalFulcrumSync } 'reparse point'
    }
    finally { [IO.Directory]::Delete($junctionPath) }
    foreach ($invalid in @('null', '[]', '{"UniversalFulcrumSync":null}', '{"UniversalFulcrumSync":[]}',
        '{"UniversalFulcrumSync":{"Enabled":"true"}}', '{"UniversalFulcrumSync":{"IntervalMinutes":20.5}}',
        '{"UniversalFulcrumSync":{"MaximumConcurrentRequests":"2"}}', '{"bad":"fixture-private-value"')) {
        [IO.File]::WriteAllText($path, $invalid)
        $hash = (Get-FileHash -LiteralPath $path).Hash
        Assert-Rejected { Set-ArdaFulcrumCandidateConfiguration @parameters -EnableUniversalFulcrumSync } 'JSON|object|type'
        Assert-True ((Get-FileHash -LiteralPath $path).Hash -eq $hash) 'Rejected input was changed.'
    }
    $qualityPath = Join-Path $release 'QualityAssurance\appsettings.Production.json'
    [IO.File]::WriteAllText($qualityPath, '{"QualityIntegration":{"FulcrumShipmentUrlTemplate":false}}')
    Assert-Rejected { Test-ArdaFulcrumProductionConfiguration -ApplicationName QualityAssurance -ProductionSettingsPath $qualityPath } 'type'
    [IO.File]::WriteAllText($path, ('{"Custom":' + ('{"X":' * 93) + '0' + ('}' * 93) + '}'))
    Assert-Rejected { Set-ArdaFulcrumCandidateConfiguration @parameters -EnableUniversalFulcrumSync } 'nesting|JSON'
    [IO.File]::WriteAllText($path, '{}')
    $result = Set-ArdaFulcrumCandidateConfiguration @parameters -EnableUniversalFulcrumSync
    Assert-True ($result.ChangedSettings.Count -eq 5) 'Missing section was not added.'
    Assert-True (@(Get-ChildItem -LiteralPath $release -Recurse -Filter '*.tmp').Count -eq 0) 'Temporary configuration file remains.'
    Write-Output "ARDA_FULCRUM_RELEASE_CONFIGURATION_TESTS_PASSED ($script:assertions assertions)"
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedTestRoot.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedTestRoot) -notlike 'arda-fulcrum-release-test-*') {
        throw 'Refusing cleanup outside the unique test directory.'
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force }
}
