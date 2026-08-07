[CmdletBinding()]
param(
    [string]$ScriptPath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Configure-HubHttpsApplicationConfig.ps1'
}
if ($PSVersionTable.PSVersion.Major -ne 5) {
    throw "These compatibility tests must run under Windows PowerShell 5.1; current version is $($PSVersionTable.PSVersion)."
}

function Assert-True {
    param([Parameter(Mandatory = $true)][bool]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-TestableFunctions {
    param([Parameter(Mandatory = $true)][string[]]$Names)
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Resolve-Path $ScriptPath), [ref]$tokens, [ref]$errors
    )
    if ($errors.Count -gt 0) { throw ($errors.Message -join '; ') }
    $definitions = @(
        $ast.FindAll({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $Names
        }, $true) | ForEach-Object { $_.Extent.Text }
    )
    if ($definitions.Count -ne $Names.Count) {
        throw "Expected $($Names.Count) testable functions but found $($definitions.Count)."
    }
    return [scriptblock]::Create(($definitions -join [Environment]::NewLine))
}

$functions = @(
    'Get-FullPath', 'Read-JsonFile', 'Get-FileSha256', 'Get-BytesSha256', 'Set-StateProperty',
    'Convert-ToUtf8JsonBytes', 'Assert-PortalShape', 'Assert-TrackerShape', 'New-TransformedConfig',
    'Assert-RequiredStateProperties', 'Assert-SafeStatePath', 'Assert-PathUnderRoot',
    'Assert-TransactionState', 'Assert-TransactionBackups', 'New-VerifiedTransactionSnapshot',
    'Assert-RecoverableActiveConfiguration',
    'Assert-OriginalConfiguration', 'Invoke-WithPoolsStopped', 'Restore-FilesWhilePoolsStopped',
    'Restore-Configuration'
)
. (Get-TestableFunctions $functions)

# Guard the original PS5.1 defect: every directly assigned state property must exist in the literal.
$scriptSource = Get-Content -LiteralPath $ScriptPath -Raw
$stateLiteralMatch = [regex]::Match(
    $scriptSource,
    '(?s)\$state\s*=\s*\[pscustomobject\]@\{(?<Body>.*?)\r?\n\}'
)
Assert-True $stateLiteralMatch.Success 'The transaction state literal could not be inspected.'
$initializedStateProperties = @(
    [regex]::Matches($stateLiteralMatch.Groups['Body'].Value, '(?m)^\s*([A-Za-z][A-Za-z0-9]*)\s*=') |
        ForEach-Object { $_.Groups[1].Value }
)
$directStateAssignments = @(
    [regex]::Matches($scriptSource, '\$state\.([A-Za-z][A-Za-z0-9]*)\s*=') |
        ForEach-Object { $_.Groups[1].Value } |
        Select-Object -Unique
)
$uninitializedAssignments = @($directStateAssignments | Where-Object { $_ -notin $initializedStateProperties })
Assert-True ($uninitializedAssignments.Count -eq 0) `
    "Direct state assignments are not initialized: $($uninitializedAssignments -join ', ')."

$stateRoot = 'C:\ProgramData\SonAero\deployment-state'
$backupBaseRoot = Join-Path $stateRoot 'https-config-backups'
$expectedComputerName = 'SON-IIS2'
$moduleUrls = @{
    'project-tracker' = 'https://SON-IIS2:6135'
    'engineering-hub' = 'https://SON-IIS2:6150'
    'estimating-dashboard' = 'https://SON-IIS2:6160'
}
$hubOrigins = @('https://SON-IIS2:6140', 'http://SON-IIS2:5140')

# Windows PowerShell 5.1 must be able to normalize legacy deserialized state objects.
$legacy = [pscustomobject]@{ Status = 'Prepared' }
Set-StateProperty -State $legacy -Name 'AppliedAtUtc' -Value '2026-08-06T00:00:00Z'
Set-StateProperty -State $legacy -Name 'RolledBackAtUtc' -Value '2026-08-06T00:01:00Z'
Set-StateProperty -State $legacy -Name 'RollbackFailure' -Value $null
Assert-True ($legacy.AppliedAtUtc -eq '2026-08-06T00:00:00Z') 'AppliedAtUtc was not added safely.'
Assert-True ($legacy.RolledBackAtUtc -eq '2026-08-06T00:01:00Z') 'RolledBackAtUtc was not added safely.'
Assert-True ($legacy.PSObject.Properties.Name -contains 'RollbackFailure') 'A null state property was not added.'

$nullRejected = $false
try { Assert-RequiredStateProperties -State ([pscustomobject]@{ Status = $null }) -Names @('Status') }
catch { $nullRejected = $true }
Assert-True $nullRejected 'A null required state property was accepted.'

# A partial pool-stop failure must still attempt to restart all transaction pools.
$script:restartCount = 0
function Stop-TargetPools { throw 'stop-fault' }
function Start-TargetPools { $script:restartCount++ }
$stopFailure = ''
try { Invoke-WithPoolsStopped -Precondition { } -Operation { throw 'operation-should-not-run' } -RecoveryOperation { } }
catch { $stopFailure = $_.Exception.Message }
Assert-True ($script:restartCount -eq 1) 'Pool recovery was not attempted after a stop failure.'
Assert-True ($stopFailure -match 'stop-fault') 'The primary pool-stop failure was not preserved.'

function Stop-TargetPools { }
function Start-TargetPools { throw 'restart-fault' }
$combinedFailure = ''
try { Invoke-WithPoolsStopped -Precondition { } -Operation { throw 'operation-fault' } -RecoveryOperation { } }
catch { $combinedFailure = $_.Exception.Message }
Assert-True ($combinedFailure -match 'operation-fault') 'The stopped-pool operation failure was masked.'
Assert-True ($combinedFailure -match 'restart-fault') 'The pool restart failure was masked.'

$script:restartCount = 0
function Stop-TargetPools { }
function Start-TargetPools { $script:restartCount++ }
$consistencyFailure = ''
try {
    Invoke-WithPoolsStopped `
        -Precondition { } `
        -Operation { throw 'replacement-fault' } `
        -RecoveryOperation { throw 'consistency-fault' }
}
catch { $consistencyFailure = $_.Exception.Message }
Assert-True ($script:restartCount -eq 0) 'Pools restarted after consistency recovery failed.'
Assert-True ($consistencyFailure -match 'replacement-fault') 'The replacement failure was not preserved.'
Assert-True ($consistencyFailure -match 'consistency-fault') 'The consistency failure was not preserved.'

# Backups must be validated before either active production file is replaced.
function Stop-TargetPools { }
function Start-TargetPools { }
function Wait-UriHealth { param([string[]]$Uris) }
function Get-HttpHealthUris { @('http://test') }
function Get-DualSchemeHealthUris { @('https://test') }

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('sonaero-config-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $backupBaseRoot = $testRoot
    $portal = Join-Path $testRoot 'portal.json'
    $tracker = Join-Path $testRoot 'tracker.json'
    $portalBackup = Join-Path $testRoot 'portal.backup.json'
    $trackerBackup = Join-Path $testRoot 'tracker.backup.json'
    [IO.File]::WriteAllText($portal, '{"mode":"https"}')
    [IO.File]::WriteAllText($tracker, '{"mode":"https"}')
    [IO.File]::WriteAllText($portalBackup, '{"mode":"http"}')
    [IO.File]::WriteAllText($trackerBackup, '{"mode":"http"}')
    $state = [pscustomobject]@{
        Version = 2
        ComputerName = 'SON-IIS2'
        Status = 'Prepared'
        PortalConfigPath = $portal
        TrackerConfigPath = $tracker
        PortalBackupPath = $portalBackup
        TrackerBackupPath = $trackerBackup
        PortalOriginalSha256 = Get-FileSha256 $portalBackup
        TrackerOriginalSha256 = Get-FileSha256 $trackerBackup
        PortalPlannedSha256 = Get-FileSha256 $portal
        TrackerPlannedSha256 = Get-FileSha256 $tracker
        PortalAppliedSha256 = ''
        TrackerAppliedSha256 = ''
    }
    Assert-TransactionState -State $state -ExpectedPortalConfigPath $portal -ExpectedTrackerConfigPath $tracker
    $safePortalBackup = $state.PortalBackupPath
    $state.PortalBackupPath = 'C:\inetpub\wwwroot\Portal.json'
    $tamperRejected = $false
    try { Assert-TransactionState -State $state -ExpectedPortalConfigPath $portal -ExpectedTrackerConfigPath $tracker }
    catch { $tamperRejected = $true }
    Assert-True $tamperRejected 'An out-of-root backup path was accepted.'
    $state.PortalBackupPath = $safePortalBackup

    # A stopped-pool precondition failure must not invoke destructive recovery or alter drifted files.
    [IO.File]::WriteAllText($portal, '{"mode":"concurrent-edit"}')
    [IO.File]::WriteAllText($tracker, '{"mode":"concurrent-edit"}')
    $portalDriftSha256 = Get-FileSha256 $portal
    $trackerDriftSha256 = Get-FileSha256 $tracker
    $script:driftRecoveryCalled = $false
    $driftRejected = $false
    try {
        Invoke-WithPoolsStopped `
            -Precondition { Assert-OriginalConfiguration $state } `
            -Operation { throw 'drift-operation-should-not-run' } `
            -RecoveryOperation {
                $script:driftRecoveryCalled = $true
                Restore-FilesWhilePoolsStopped $state
            }
    }
    catch { $driftRejected = $true }
    Assert-True $driftRejected 'A concurrent active-config edit was not rejected.'
    Assert-True (-not $script:driftRecoveryCalled) 'Precondition drift incorrectly invoked destructive recovery.'
    Assert-True ((Get-FileSha256 $portal) -eq $portalDriftSha256) 'Portal drift was overwritten after precondition rejection.'
    Assert-True ((Get-FileSha256 $tracker) -eq $trackerDriftSha256) 'Tracker drift was overwritten after precondition rejection.'

    [IO.File]::WriteAllText($portal, '{"mode":"https"}')
    [IO.File]::WriteAllText($tracker, '{"mode":"https"}')

    Assert-RecoverableActiveConfiguration $state
    Restore-Configuration $state
    Assert-OriginalConfiguration $state

    # A fault on the second move is recovered while pools are stopped; no mixed pair is served.
    [IO.File]::WriteAllText($portal, '{"mode":"https"}')
    [IO.File]::WriteAllText($tracker, '{"mode":"https"}')
    $script:moveCount = 0
    function Move-Item {
        param(
            [Parameter(Mandatory = $true)][string]$LiteralPath,
            [Parameter(Mandatory = $true)][string]$Destination,
            [switch]$Force
        )
        $script:moveCount++
        if ($script:moveCount -eq 2) { throw 'injected-second-move-fault' }
        Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
    }
    Restore-Configuration $state
    Remove-Item -LiteralPath 'Function:\Move-Item'
    Assert-True ($script:moveCount -eq 4) 'The second-move fault did not run the stopped-pool recovery pass.'
    Assert-OriginalConfiguration $state

    [IO.File]::WriteAllText($portal, '{"mode":"https"}')
    [IO.File]::WriteAllText($tracker, '{"mode":"https"}')
    [IO.File]::WriteAllText($portalBackup, '{"corrupt":true}')
    $portalBefore = Get-FileSha256 $portal
    $trackerBefore = Get-FileSha256 $tracker
    $corruptBackupRejected = $false
    try { Restore-Configuration $state }
    catch { $corruptBackupRejected = $true }
    Assert-True $corruptBackupRejected 'A corrupt backup was accepted.'
    Assert-True ((Get-FileSha256 $portal) -eq $portalBefore) 'Portal changed before corrupt-backup rejection.'
    Assert-True ((Get-FileSha256 $tracker) -eq $trackerBefore) 'Tracker changed before corrupt-backup rejection.'

    # A legacy v1 state is safely upgraded in memory from its validated originals.
    $portalOriginalObject = [pscustomobject]@{
        Portal = [pscustomobject]@{
            Applications = @(
                [pscustomobject]@{ Id = 'project-tracker'; Url = 'http://SON-IIS2:5135' },
                [pscustomobject]@{ Id = 'engineering-hub'; Url = 'http://SON-IIS2:5150' },
                [pscustomobject]@{ Id = 'estimating-dashboard'; Url = 'http://SON-IIS2:5160' }
            )
        }
    }
    $trackerOriginalObject = [pscustomobject]@{
        Cors = [pscustomobject]@{ HubOrigins = @('http://SON-IIS2:5140') }
    }

    # The apply plan is generated from a stable secured snapshot, never from an earlier live read.
    [IO.File]::WriteAllBytes($portal, (Convert-ToUtf8JsonBytes $portalOriginalObject))
    [IO.File]::WriteAllBytes($tracker, (Convert-ToUtf8JsonBytes $trackerOriginalObject))
    $snapshotPortalBackup = Join-Path $testRoot 'snapshot.portal.json'
    $snapshotTrackerBackup = Join-Path $testRoot 'snapshot.tracker.json'
    $snapshot = New-VerifiedTransactionSnapshot `
        -PortalConfigPath $portal `
        -TrackerConfigPath $tracker `
        -PortalBackupPath $snapshotPortalBackup `
        -TrackerBackupPath $snapshotTrackerBackup
    Assert-True ($snapshot.PortalOriginalSha256 -eq (Get-FileSha256 $snapshotPortalBackup)) `
        'Portal snapshot hash was not bound to its secured backup.'
    Assert-True ($snapshot.TrackerOriginalSha256 -eq (Get-FileSha256 $snapshotTrackerBackup)) `
        'Tracker snapshot hash was not bound to its secured backup.'
    Assert-True ($snapshot.PortalPlannedSha256 -eq (Get-BytesSha256 ([byte[]]$snapshot.PortalPlannedBytes))) `
        'Portal plan hash was not generated from the verified snapshot.'

    [IO.File]::WriteAllBytes($portal, (Convert-ToUtf8JsonBytes $portalOriginalObject))
    [IO.File]::WriteAllBytes($tracker, (Convert-ToUtf8JsonBytes $trackerOriginalObject))
    $script:snapshotCopyCount = 0
    function Copy-Item {
        param(
            [Parameter(Mandatory = $true)][string]$LiteralPath,
            [Parameter(Mandatory = $true)][string]$Destination,
            [switch]$Force
        )
        $script:snapshotCopyCount++
        if ($script:snapshotCopyCount -eq 1) { [IO.File]::AppendAllText($LiteralPath, ' ') }
        Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    }
    $snapshotDriftRejected = $false
    try {
        New-VerifiedTransactionSnapshot `
            -PortalConfigPath $portal `
            -TrackerConfigPath $tracker `
            -PortalBackupPath (Join-Path $testRoot 'drift.portal.json') `
            -TrackerBackupPath (Join-Path $testRoot 'drift.tracker.json') | Out-Null
    }
    catch { $snapshotDriftRejected = $true }
    finally { Remove-Item -LiteralPath 'Function:\Copy-Item' }
    Assert-True $snapshotDriftRejected 'A config edit during backup capture was accepted.'

    [IO.File]::WriteAllBytes($portalBackup, (Convert-ToUtf8JsonBytes $portalOriginalObject))
    [IO.File]::WriteAllBytes($trackerBackup, (Convert-ToUtf8JsonBytes $trackerOriginalObject))
    Copy-Item -LiteralPath $portalBackup -Destination $portal -Force
    Copy-Item -LiteralPath $trackerBackup -Destination $tracker -Force
    $legacyPrepared = [pscustomobject]@{
        Version = 1
        ComputerName = 'SON-IIS2'
        Status = 'Prepared'
        PortalConfigPath = $portal
        TrackerConfigPath = $tracker
        PortalBackupPath = $portalBackup
        TrackerBackupPath = $trackerBackup
        PortalOriginalSha256 = Get-FileSha256 $portalBackup
        TrackerOriginalSha256 = Get-FileSha256 $trackerBackup
        PortalAppliedSha256 = ''
        TrackerAppliedSha256 = ''
    }
    Assert-TransactionState -State $legacyPrepared -ExpectedPortalConfigPath $portal -ExpectedTrackerConfigPath $tracker
    Assert-True ($legacyPrepared.Version -eq 2) 'Legacy state was not normalized to version 2 in memory.'
    Assert-RecoverableActiveConfiguration $legacyPrepared

    [IO.File]::WriteAllBytes($portal, (Convert-ToUtf8JsonBytes (New-TransformedConfig `
        -PortalConfig (Read-JsonFile $portalBackup) `
        -TrackerConfig (Read-JsonFile $trackerBackup)).Portal))
    Assert-RecoverableActiveConfiguration $legacyPrepared
    [IO.File]::WriteAllBytes($tracker, (Convert-ToUtf8JsonBytes (New-TransformedConfig `
        -PortalConfig (Read-JsonFile $portalBackup) `
        -TrackerConfig (Read-JsonFile $trackerBackup)).Tracker))
    Assert-RecoverableActiveConfiguration $legacyPrepared

    $legacyApplied = $legacyPrepared | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $legacyApplied.Version = 1
    $legacyApplied.Status = 'Applied'
    $legacyApplied.PortalAppliedSha256 = $legacyPrepared.PortalPlannedSha256
    $legacyApplied.TrackerAppliedSha256 = $legacyPrepared.TrackerPlannedSha256
    $legacyApplied.PSObject.Properties.Remove('PortalPlannedSha256')
    $legacyApplied.PSObject.Properties.Remove('TrackerPlannedSha256')
    Assert-TransactionState -State $legacyApplied -ExpectedPortalConfigPath $portal -ExpectedTrackerConfigPath $tracker

    Copy-Item -LiteralPath $portalBackup -Destination $portal -Force
    Copy-Item -LiteralPath $trackerBackup -Destination $tracker -Force
    $legacyTerminal = $legacyPrepared | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $legacyTerminal.Version = 1
    $legacyTerminal.Status = 'RolledBack'
    $legacyTerminal.PortalAppliedSha256 = ''
    $legacyTerminal.TrackerAppliedSha256 = ''
    $legacyTerminal.PSObject.Properties.Remove('PortalPlannedSha256')
    $legacyTerminal.PSObject.Properties.Remove('TrackerPlannedSha256')
    Assert-TransactionState -State $legacyTerminal -ExpectedPortalConfigPath $portal -ExpectedTrackerConfigPath $tracker
    Assert-OriginalConfiguration $legacyTerminal
}
finally { Remove-Item -LiteralPath $testRoot -Recurse -Force }

Write-Output 'CONFIGURE_HUB_HTTPS_APPLICATION_CONFIG_TESTS_PASSED'
