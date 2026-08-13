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
    'Get-FullPath', 'Assert-NoReparsePathChain', 'Read-JsonFile', 'Get-FileSha256', 'Get-BytesSha256', 'Set-StateProperty',
    'New-ProtectedFileSystemSecurity', 'Assert-ProtectedPath', 'New-SecureDirectory',
    'Assert-StatePathProtection', 'Write-SecureState',
    'Resolve-TransactionStatePath', 'Set-TopologyConfiguration', 'Convert-ToUtf8JsonBytes',
    'Assert-PortalShape', 'Assert-TrackerShape', 'New-TransformedConfig',
    'Assert-RequiredStateProperties', 'Assert-SafeStatePath', 'Assert-PathUnderRoot',
    'Assert-TransactionState', 'Assert-RequestedStateTopology', 'Assert-TransactionBackups',
    'New-VerifiedTransactionSnapshot', 'Assert-RecoverableActiveConfiguration',
    'Assert-AppliedConfiguration', 'Get-DualSchemeHealthUris', 'Get-RetainedHealthUris', 'Assert-CorsResponse', 'Assert-DualCors',
    'Assert-OriginalConfiguration', 'Invoke-WithPoolsStopped', 'Restore-FilesWhilePoolsStopped',
    'Restore-Configuration'
)
. (Get-TestableFunctions $functions)

# Guard the original PS5.1 defect: every directly assigned state property must exist in the literal.
$scriptSource = Get-Content -LiteralPath $ScriptPath -Raw
Assert-True ([regex]::IsMatch($scriptSource, '\[string\]\$Topology\s*=\s*''Production''')) `
    'The permanent DNS/SNI topology is not the default transaction mode.'
Assert-True ($scriptSource -match '\[IO\.File\]::Replace\(\$temporary,\s*\$StatePath,\s*\$replacementBackup\)' -and
    $scriptSource -notmatch '\[IO\.File\]::Replace\(\$temporary,\s*\$StatePath,\s*\$null\)') `
    'The secure state writer must use a named File.Replace backup under Windows PowerShell 5.1.'
$replaceWriterMatch = [regex]::Match(
    $scriptSource,
    '(?s)function Write-SecureState\s*\{(?<Body>.*?)\r?\n\}'
)
Assert-True $replaceWriterMatch.Success 'The secure state writer could not be inspected.'
$replaceWriter = $replaceWriterMatch.Groups['Body'].Value
$replaceCallIndex = $replaceWriter.IndexOf('[IO.File]::Replace($temporary, $StatePath, $replacementBackup)')
$backupReparseIndex = $replaceWriter.IndexOf('Assert-NoReparsePathChain -Path $replacementBackup', $replaceCallIndex + 1)
$backupProtectionIndex = $replaceWriter.IndexOf('Assert-ProtectedPath -Path $replacementBackup', $replaceCallIndex + 1)
$destinationProtectionIndex = $replaceWriter.IndexOf('Assert-StatePathProtection', $replaceCallIndex + 1)
$cleanupRemoveIndex = $replaceWriter.LastIndexOf('Remove-Item -LiteralPath $replacementBackup')
$cleanupReparseIndex = $replaceWriter.LastIndexOf('Assert-NoReparsePathChain -Path $replacementBackup', $cleanupRemoveIndex)
$cleanupProtectionIndex = $replaceWriter.LastIndexOf('Assert-ProtectedPath -Path $replacementBackup', $cleanupRemoveIndex)
Assert-True ($replaceCallIndex -ge 0 -and $backupReparseIndex -gt $replaceCallIndex -and
    $backupProtectionIndex -gt $backupReparseIndex -and
    $destinationProtectionIndex -gt $backupProtectionIndex -and
    $replaceWriter -match 'Get-FileSha256\s+\$replacementBackup\)\s+-ine\s+\$previousStateSha256' -and
    $replaceWriter -match 'Get-FileSha256\s+\$StatePath\)\s+-ine\s+\$temporarySha256' -and
    $replaceWriter -match 'prior state backup is preserved at ''\$replacementBackup''' -and
    $cleanupRemoveIndex -gt 0 -and $cleanupReparseIndex -gt $destinationProtectionIndex -and
    $cleanupProtectionIndex -gt $cleanupReparseIndex -and $cleanupProtectionIndex -lt $cleanupRemoveIndex) `
    'The secure state writer does not hash/ACL-verify both replacement artifacts and safely clean its named backup.'

# Exercise the exact .NET Framework API contract used by Windows PowerShell 5.1. A same-directory,
# non-null backup must preserve the old destination while atomically installing the new content.
$fileReplaceRoot = Join-Path ([IO.Path]::GetTempPath()) ('sonaero-file-replace-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fileReplaceRoot) | Out-Null
try {
    $fileReplaceSource = Join-Path $fileReplaceRoot 'state.new'
    $fileReplaceDestination = Join-Path $fileReplaceRoot 'state.json'
    $fileReplaceBackup = Join-Path $fileReplaceRoot 'state.previous'
    [IO.File]::WriteAllText($fileReplaceSource, 'new-state')
    [IO.File]::WriteAllText($fileReplaceDestination, 'old-state')
    [IO.File]::Replace($fileReplaceSource, $fileReplaceDestination, $fileReplaceBackup)
    Assert-True (([IO.File]::ReadAllText($fileReplaceDestination)) -ceq 'new-state') `
        'Windows PowerShell 5.1 File.Replace did not atomically install the source state.'
    Assert-True (([IO.File]::ReadAllText($fileReplaceBackup)) -ceq 'old-state') `
        'Windows PowerShell 5.1 File.Replace did not create the named rollback backup.'
    Assert-True (-not (Test-Path -LiteralPath $fileReplaceSource)) `
        'Windows PowerShell 5.1 File.Replace unexpectedly retained the source file.'
}
finally { Remove-Item -LiteralPath $fileReplaceRoot -Recurse -Force }

# Exercise the real writer twice under Windows PowerShell 5.1. Only ACL/path primitives are mocked;
# the second call must traverse the named File.Replace path, verify both hashes, and remove its backup.
$writerRoot = Join-Path ([IO.Path]::GetTempPath()) ('sonaero-state-writer-' + [Guid]::NewGuid().ToString('N'))
$stateRoot = $writerRoot
$StatePath = Join-Path $writerRoot 'state.json'
$script:writerProtectedPaths = @()
function Assert-NoReparsePathChain {
    param([Parameter(Mandatory = $true)][string]$Path)
    $null = [IO.Path]::GetFullPath($Path)
}
function New-SecureDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    [IO.Directory]::CreateDirectory($Path) | Out-Null
}
function New-ProtectedFileSystemSecurity { return [pscustomobject]@{ MockAcl = $true } }
function Set-Acl {
    param([Parameter(Mandatory = $true)][string]$LiteralPath, $AclObject)
    if (-not (Test-Path -LiteralPath $LiteralPath)) { throw "Mock ACL target is missing: '$LiteralPath'." }
}
function Assert-ProtectedPath {
    param([Parameter(Mandatory = $true)][string]$Path, [switch]$Directory)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Mock protected path is missing: '$Path'." }
    $script:writerProtectedPaths += $Path
}
function Assert-StatePathProtection {
    Assert-NoReparsePathChain -Path $StatePath
    Assert-ProtectedPath -Path $stateRoot -Directory
    Assert-ProtectedPath -Path $StatePath
}
try {
    Write-SecureState ([pscustomobject]@{ Version = 1; Status = 'Prepared' })
    Write-SecureState ([pscustomobject]@{ Version = 2; Status = 'Applied' })
    $writtenState = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    Assert-True ($writtenState.Version -eq 2 -and $writtenState.Status -ceq 'Applied') `
        'The real secure state writer did not atomically install its second state.'
    Assert-True (@($script:writerProtectedPaths | Where-Object { $_ -like '*.replace-backup' }).Count -ge 2) `
        'The real secure state writer did not ACL-validate its named backup after replacement and before cleanup.'
    Assert-True (@(Get-ChildItem -LiteralPath $writerRoot -Filter '*.replace-backup' -Force).Count -eq 0) `
        'The real secure state writer left a verified named backup behind.'
    Assert-True (@(Get-ChildItem -LiteralPath $writerRoot -Filter '*.tmp' -Force).Count -eq 0) `
        'The real secure state writer left a temporary state file behind.'

    # Once File.Replace succeeds, a failed destination validation must retain the verified prior-state
    # backup and report its exact path rather than silently deleting the only recovery artifact.
    $script:writerProtectionCallCount = 0
    function Assert-StatePathProtection {
        $script:writerProtectionCallCount++
        Assert-NoReparsePathChain -Path $StatePath
        Assert-ProtectedPath -Path $stateRoot -Directory
        Assert-ProtectedPath -Path $StatePath
        if ($script:writerProtectionCallCount -eq 2) { throw 'Simulated post-replacement validation failure.' }
    }
    $writerFailure = $null
    try { Write-SecureState ([pscustomobject]@{ Version = 3; Status = 'ValidationFailure' }) }
    catch { $writerFailure = $_.Exception.Message }
    $preservedBackups = @(Get-ChildItem -LiteralPath $writerRoot -Filter '*.replace-backup' -Force)
    Assert-True ($preservedBackups.Count -eq 1 -and
        $writerFailure -like "*prior state backup is preserved at '$($preservedBackups[0].FullName)'*") `
        'Post-replacement validation failure did not retain and identify the exact prior-state backup.'
    $preservedState = Get-Content -LiteralPath $preservedBackups[0].FullName -Raw | ConvertFrom-Json
    Assert-True ($preservedState.Version -eq 2 -and $preservedState.Status -ceq 'Applied') `
        'The preserved replacement backup is not the exact prior transaction state.'
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $preservedBackups[0].FullName -Force

    # A verified commit remains successful if removal is momentarily blocked. This must remain true
    # even when the caller promotes ordinary warnings to terminating errors.
    function Assert-StatePathProtection {
        Assert-NoReparsePathChain -Path $StatePath
        Assert-ProtectedPath -Path $stateRoot -Directory
        Assert-ProtectedPath -Path $StatePath
    }
    function Remove-Item {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory = $true)][string]$LiteralPath,
            [switch]$Force,
            [switch]$Recurse
        )
        if ($LiteralPath -like '*.replace-backup') { throw 'Simulated cleanup contention.' }
        Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
    }
    $savedWarningPreference = $WarningPreference
    try {
        $WarningPreference = 'Stop'
        Write-SecureState ([pscustomobject]@{ Version = 4; Status = 'CleanupContended' })
    }
    finally { $WarningPreference = $savedWarningPreference }
    $contendedBackups = @(Get-ChildItem -LiteralPath $writerRoot -Filter '*.replace-backup' -Force)
    $contendedState = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    Assert-True ($contendedState.Version -eq 4 -and $contendedBackups.Count -eq 1) `
        'Backup cleanup contention incorrectly failed or reverted the verified transaction-state commit.'
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath Function:\Remove-Item -Force
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $contendedBackups[0].FullName -Force
}
finally {
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath Function:\Remove-Item -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $writerRoot) {
        Microsoft.PowerShell.Management\Remove-Item -LiteralPath $writerRoot -Recurse -Force
    }
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath Function:\Set-Acl -Force -ErrorAction SilentlyContinue
    . (Get-TestableFunctions $functions)
}
Assert-True ($scriptSource -match "Global\\SonAero-HubHttpsApplicationConfig" -and
    $scriptSource -match '\.WaitOne\(0\)' -and
    $scriptSource -match '\$transactionMutex\.ReleaseMutex\(\)') `
    'Production and Pilot application-config transactions are not serialized by one global mutex.'
Assert-True ($scriptSource -match 'Assert-TrackerAuthenticationState -AnonymousEnabled \$true -WindowsEnabled \$true' -and
    $scriptSource -match 'Assert-AnonymousTrackerApiDenied' -and
    $scriptSource -match 'Assert-CredentialedTrackerIdentity' -and
    $scriptSource -match '\$payload\.accountName -ine \$ExpectedAccountName' -and
    $scriptSource -match 'Assert-TrackerCorsAuthenticationBoundary -RetainedOnly') `
    'Application config does not guard browser preflight, anonymous API denial, and retained rollback CORS.'
Assert-True ([regex]::Matches(
        $scriptSource,
        'Restore-Configuration\s+-State\s+\$state\s+-VerifyDualScheme'
    ).Count -ge 2) `
    'Manual and automatic rollback must both verify the retained HTTP/61xx baseline.'

$script:preflightCall = $null
function Invoke-WebRequest {
    param(
        [switch]$UseBasicParsing,
        [string]$Method,
        [string]$Uri,
        [int]$TimeoutSec,
        [hashtable]$Headers
    )
    $script:preflightCall = [pscustomobject]@{
        Method = $Method
        Uri = $Uri
        Headers = $Headers
    }
    return [pscustomobject]@{
        StatusCode = 204
        Headers = @{
            'Access-Control-Allow-Origin' = $Headers.Origin
            'Access-Control-Allow-Credentials' = 'true'
            'Access-Control-Allow-Methods' = 'POST'
            'Access-Control-Allow-Headers' = 'content-type'
        }
    }
}
Assert-CorsResponse -Origin 'https://hub.son4l.local' -Uri 'https://projects.hub.son4l.local/api/me'
Assert-True ($script:preflightCall.Method -ceq 'Options') `
    'CORS verification did not send an actual OPTIONS preflight.'
Assert-True ($script:preflightCall.Headers['Access-Control-Request-Method'] -ceq 'POST' -and
    $script:preflightCall.Headers['Access-Control-Request-Headers'] -ceq 'content-type') `
    'CORS verification omitted the requested method or header.'
Remove-Item Function:\Invoke-WebRequest
$pilotRunbookPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) `
    'deployment\two-person-https-pilot.md'
$pilotRunbookSource = Get-Content -LiteralPath $pilotRunbookPath -Raw
$pilotApplicationConfigCommands = @(
    [regex]::Matches(
        $pilotRunbookSource,
        '(?m)^\s*&\s+(?:\$configScript|[^\r\n]*Configure-HubHttpsApplicationConfig\.ps1[^\r\n]*)[^\r\n]*'
    ) | ForEach-Object { $_.Value }
)
Assert-True ($pilotApplicationConfigCommands.Count -gt 0) `
    'The retained pilot runbook no longer documents its application-config transaction.'
Assert-True (@($pilotApplicationConfigCommands | Where-Object { $_ -notmatch '-Topology Pilot' }).Count -eq 0) `
    'Every retained pilot application-config command must explicitly select -Topology Pilot.'
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
$pilotModuleUrls = @{
    'project-tracker' = 'https://SON-IIS2:6135'
    'engineering-hub' = 'https://SON-IIS2:6150'
    'estimating-dashboard' = 'https://SON-IIS2:6160'
    'quality-assurance' = 'https://SON-IIS2:6170'
}
$productionModuleUrls = @{
    'project-tracker' = 'https://projects.hub.son4l.local'
    'engineering-hub' = 'https://engineering.hub.son4l.local'
    'estimating-dashboard' = 'https://estimating.hub.son4l.local'
    'quality-assurance' = 'https://quality.hub.son4l.local'
}
$pilotHubOrigins = @('https://SON-IIS2:6140', 'http://SON-IIS2:5140')
$productionHubOrigins = @(
    'https://hub.son4l.local',
    'https://SON-IIS2:6140',
    'http://SON-IIS2:5140'
)
$gatewayPath = '/project-tracker-api'
$applications = @(
    [pscustomobject]@{ Id = 'project-tracker'; HttpPort = 5135; HttpsPort = 6135; ProductionHost = 'projects.hub.son4l.local' },
    [pscustomobject]@{ Id = 'portal'; HttpPort = 5140; HttpsPort = 6140; ProductionHost = 'hub.son4l.local' },
    [pscustomobject]@{ Id = 'engineering-hub'; HttpPort = 5150; HttpsPort = 6150; ProductionHost = 'engineering.hub.son4l.local' },
    [pscustomobject]@{ Id = 'estimating-dashboard'; HttpPort = 5160; HttpsPort = 6160; ProductionHost = 'estimating.hub.son4l.local' },
    [pscustomobject]@{ Id = 'quality-assurance'; HttpPort = 5170; HttpsPort = 6170; ProductionHost = 'quality.hub.son4l.local' }
)
Set-TopologyConfiguration -Name Production

$pilotStatePath = Join-Path $stateRoot 'https-application-config.json'
$productionStatePath = Join-Path $stateRoot 'https-production-application-config.json'
Assert-True ((Resolve-TransactionStatePath -SelectedTopology Production -SuppliedPath '' -WasExplicit $false) -ceq $productionStatePath) `
    'Production did not default to its isolated transaction-state file.'
Assert-True ((Resolve-TransactionStatePath -SelectedTopology Pilot -SuppliedPath '' -WasExplicit $false) -ceq $pilotStatePath) `
    'Pilot did not retain its legacy transaction-state file.'
$customStatePath = Join-Path $stateRoot 'custom-production-config.json'
Assert-True ((Resolve-TransactionStatePath -SelectedTopology Production -SuppliedPath $customStatePath -WasExplicit $true) -ceq $customStatePath) `
    'An explicit transaction state path was not retained for subsequent strict validation.'
$emptyExplicitStateRejected = $false
try { Resolve-TransactionStatePath -SelectedTopology Production -SuppliedPath '' -WasExplicit $true | Out-Null }
catch { $emptyExplicitStateRejected = $true }
Assert-True $emptyExplicitStateRejected 'An explicitly empty StatePath was accepted.'

$productionPortal = [pscustomobject]@{
    Portal = [pscustomobject]@{
        Applications = @(
            [pscustomobject]@{ Id = 'project-tracker'; Url = 'http://SON-IIS2:5135' },
            [pscustomobject]@{ Id = 'engineering-hub'; Url = 'https://SON-IIS2:6150' },
            [pscustomobject]@{ Id = 'estimating-dashboard'; Url = 'http://SON-IIS2:5160' },
            [pscustomobject]@{ Id = 'quality-assurance'; Url = 'https://SON-IIS2:6170' }
        )
    }
}
$productionTracker = [pscustomobject]@{
    Cors = [pscustomobject]@{ HubOrigins = @('https://SON-IIS2:6140', 'http://SON-IIS2:5140') }
}
$productionTransform = New-TransformedConfig -PortalConfig $productionPortal -TrackerConfig $productionTracker
foreach ($id in $productionModuleUrls.Keys) {
    $application = @($productionTransform.Portal.Portal.Applications | Where-Object Id -eq $id)[0]
    Assert-True ($application.Url -ceq $productionModuleUrls[$id]) "Permanent URL was not applied for '$id'."
}
Assert-True ((@($productionTransform.Tracker.Cors.HubOrigins) -join '|') -ceq ($productionHubOrigins -join '|')) `
    'Permanent Hub CORS origins were not applied HTTPS-first with transitional HTTP retained.'

$testAppliedPortal = Join-Path ([IO.Path]::GetTempPath()) ('sonaero-applied-portal-' + [Guid]::NewGuid().ToString('N') + '.json')
$testAppliedTracker = Join-Path ([IO.Path]::GetTempPath()) ('sonaero-applied-tracker-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($testAppliedPortal, '{"status":"planned"}')
    [IO.File]::WriteAllText($testAppliedTracker, '{"status":"planned"}')
    $appliedState = [pscustomobject]@{
        Status = 'Applied'
        Topology = 'production'
        PortalConfigPath = $testAppliedPortal
        TrackerConfigPath = $testAppliedTracker
        PortalPlannedSha256 = Get-FileSha256 $testAppliedPortal
        TrackerPlannedSha256 = Get-FileSha256 $testAppliedTracker
    }
    Assert-RequestedStateTopology -State $appliedState -RequestedTopology Production
    Assert-AppliedConfiguration $appliedState
    $appliedState.Topology = 'production'
    Assert-RequestedStateTopology -State $appliedState -RequestedTopology Production
    Assert-True ($appliedState.Topology -ceq 'production') `
        'Requested-topology comparison unexpectedly mutated an already loaded state object.'
    $appliedState.Topology = 'Production'
    $wrongTopologyRejected = $false
    try { Assert-RequestedStateTopology -State $appliedState -RequestedTopology Pilot }
    catch { $wrongTopologyRejected = $true }
    Assert-True $wrongTopologyRejected 'An applied Production state was accepted for a Pilot request.'
    [IO.File]::AppendAllText($testAppliedTracker, ' ')
    $appliedDriftRejected = $false
    try { Assert-AppliedConfiguration $appliedState }
    catch { $appliedDriftRejected = $true }
    Assert-True $appliedDriftRejected 'Applied-state idempotency accepted active configuration drift.'
}
finally {
    Remove-Item -LiteralPath $testAppliedPortal, $testAppliedTracker -Force -ErrorAction SilentlyContinue
}
$productionHealthUris = @(Get-DualSchemeHealthUris)
foreach ($uri in @(
    'https://projects.hub.son4l.local/api/health',
    'https://hub.son4l.local/api/health',
    'https://engineering.hub.son4l.local/api/health',
    'https://estimating.hub.son4l.local/api/health',
    'https://quality.hub.son4l.local/api/health',
    'https://hub.son4l.local/project-tracker-api/api/health',
    'http://SON-IIS2:5140/project-tracker-api/api/health'
)) {
    Assert-True ($uri -cin $productionHealthUris) "Permanent topology health plan omitted '$uri'."
}
$retainedRollbackUris = @(Get-RetainedHealthUris)
foreach ($uri in @(
    'http://SON-IIS2:5135/api/health',
    'http://SON-IIS2:5140/api/health',
    'https://SON-IIS2:6135/api/health',
    'https://SON-IIS2:6140/api/health',
    'http://SON-IIS2:5140/project-tracker-api/api/health',
    'https://SON-IIS2:6140/project-tracker-api/api/health'
)) {
    Assert-True ($uri -cin $retainedRollbackUris) "Rollback health plan omitted retained endpoint '$uri'."
}
Assert-True (@($retainedRollbackUris | Where-Object { $_ -match '\.hub\.son4l\.local' }).Count -eq 0) `
    'Rollback health verification still depends on production port-443 hostnames.'
foreach ($uri in @(
    'https://SON-IIS2:6135/api/health',
    'https://SON-IIS2:6140/api/health',
    'https://SON-IIS2:6150/api/health',
    'https://SON-IIS2:6160/api/health',
    'https://SON-IIS2:6170/api/health',
    'https://SON-IIS2:6140/project-tracker-api/api/health'
)) {
    Assert-True ($uri -cin $productionHealthUris) "Permanent topology health plan omitted retained pilot endpoint '$uri'."
}

$script:corsCalls = @()
function Assert-CorsResponse {
    param([Parameter(Mandatory = $true)][string]$Uri, [Parameter(Mandatory = $true)][string]$Origin)
    $script:corsCalls += [pscustomobject]@{ Uri = $Uri; Origin = $Origin }
}
Assert-DualCors
Assert-True ($script:corsCalls.Count -eq 3) 'Permanent topology did not validate permanent HTTPS, pilot HTTPS, and transitional HTTP CORS.'
Assert-True ($script:corsCalls[0].Uri -ceq 'https://projects.hub.son4l.local/api/me' -and
    $script:corsCalls[0].Origin -ceq 'https://hub.son4l.local') `
    'Permanent topology CORS validation did not target the exact permanent HTTPS origin and Project Tracker host.'
Assert-True ($script:corsCalls[1].Uri -ceq 'https://SON-IIS2:6135/api/me' -and
    $script:corsCalls[1].Origin -ceq 'https://son-iis2:6140') `
    'Permanent topology CORS validation did not retain the pilot HTTPS Hub origin.'
Assert-True ($script:corsCalls[2].Uri -ceq 'http://SON-IIS2:5135/api/me' -and
    $script:corsCalls[2].Origin -ceq 'http://son-iis2:5140') `
    'Permanent topology CORS validation did not retain the transitional HTTP Hub origin.'

Set-TopologyConfiguration -Name Pilot
$pilotPortal = $productionPortal | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$pilotTracker = $productionTracker | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$pilotTransform = New-TransformedConfig -PortalConfig $pilotPortal -TrackerConfig $pilotTracker
Assert-True ((@($pilotTransform.Tracker.Cors.HubOrigins) -join '|') -ceq ($pilotHubOrigins -join '|')) `
    'Backward-compatible pilot CORS transform was not retained.'
$pilotHealthUris = @(Get-DualSchemeHealthUris)
Assert-True ('https://SON-IIS2:6135/api/health' -cin $pilotHealthUris -and
    'https://SON-IIS2:6140/project-tracker-api/api/health' -cin $pilotHealthUris) `
    'Backward-compatible pilot health plan was not retained.'
$script:corsCalls = @()
Assert-DualCors
Assert-True ($script:corsCalls[0].Uri -ceq 'https://SON-IIS2:6135/api/me' -and
    $script:corsCalls[0].Origin -ceq 'https://son-iis2:6140') `
    'Backward-compatible pilot CORS validation was not retained.'
Set-TopologyConfiguration -Name Production

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
$script:lastHealthUris = @()
function Wait-UriHealth { param([string[]]$Uris) $script:lastHealthUris = @($Uris) }
function Get-HttpHealthUris { @('http://test') }
function Get-DualSchemeHealthUris { @('https://test') }
function Get-RetainedHealthUris { @('http://retained-test', 'https://retained-test') }
function Assert-TrackerCorsAuthenticationBoundary { param([switch]$RetainedOnly) }

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
        Topology = 'Production'
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
    Assert-True ($state.Topology -ceq 'Production') `
        'A mixed-case transaction topology was not normalized to its canonical persisted value.'
    $legacyVersionTwoState = $state | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $legacyVersionTwoState.Version = 2
    $legacyVersionTwoState.PSObject.Properties.Remove('Topology')
    Assert-TransactionState `
        -State $legacyVersionTwoState `
        -ExpectedPortalConfigPath $portal `
        -ExpectedTrackerConfigPath $tracker
    Assert-True ($legacyVersionTwoState.Version -eq 3 -and $legacyVersionTwoState.Topology -ceq 'Pilot') `
        'A legacy version 2 pilot state was not normalized backward-compatibly.'
    $missingVersionThreeTopology = $state | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $missingVersionThreeTopology.Version = 3
    $missingVersionThreeTopology.PSObject.Properties.Remove('Topology')
    $missingVersionThreeTopologyRejected = $false
    try {
        Assert-TransactionState `
            -State $missingVersionThreeTopology `
            -ExpectedPortalConfigPath $portal `
            -ExpectedTrackerConfigPath $tracker
    }
    catch { $missingVersionThreeTopologyRejected = $true }
    Assert-True $missingVersionThreeTopologyRejected `
        'A version 3 transaction state without its required rollback topology was accepted.'
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
    Assert-True (($script:lastHealthUris -join '|') -ceq 'http://test') `
        'Automatic rollback did not limit verification to its HTTP safety baseline.'

    [IO.File]::WriteAllText($portal, '{"mode":"https"}')
    [IO.File]::WriteAllText($tracker, '{"mode":"https"}')
    Restore-Configuration $state -VerifyDualScheme
    Assert-OriginalConfiguration $state
    Assert-True (($script:lastHealthUris -join '|') -ceq 'http://retained-test|https://retained-test') `
        'Manual rollback still depends on production 443 instead of the retained HTTP/61xx baseline.'

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
                [pscustomobject]@{ Id = 'estimating-dashboard'; Url = 'http://SON-IIS2:5160' },
                [pscustomobject]@{ Id = 'quality-assurance'; Url = 'http://SON-IIS2:5170' }
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
        Topology = 'Pilot'
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
    Assert-True ($legacyPrepared.Version -eq 3) 'Legacy state was not normalized to version 3 in memory.'
    Assert-RecoverableActiveConfiguration $legacyPrepared

    $legacyPilotTransform = New-TransformedConfig `
        -PortalConfig (Read-JsonFile $portalBackup) `
        -TrackerConfig (Read-JsonFile $trackerBackup) `
        -TargetModuleUrls $pilotModuleUrls `
        -TargetHubOrigins $pilotHubOrigins
    [IO.File]::WriteAllBytes($portal, (Convert-ToUtf8JsonBytes $legacyPilotTransform.Portal))
    Assert-RecoverableActiveConfiguration $legacyPrepared
    [IO.File]::WriteAllBytes($tracker, (Convert-ToUtf8JsonBytes $legacyPilotTransform.Tracker))
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
