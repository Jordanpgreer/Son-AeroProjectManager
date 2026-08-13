[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw "Run this focused compatibility test with Windows PowerShell 5.1; current version is $($PSVersionTable.PSVersion)."
}

$deploymentRoot = Split-Path -Parent $PSScriptRoot
$pilotPath = Join-Path $deploymentRoot 'Configure-HubHttpsPilot.ps1'
$productionPath = Join-Path $deploymentRoot 'Configure-HubProductionHttps.ps1'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([Parameter(Mandatory = $true)][scriptblock]$Action, [Parameter(Mandatory = $true)][string]$Message)
    $threw = $false
    try { & $Action | Out-Null }
    catch { $threw = $true }
    Assert-True $threw $Message
}

function Get-ParsedScript {
    param([Parameter(Mandatory = $true)][string]$Path)
    Assert-True (Test-Path -LiteralPath $Path -PathType Leaf) "Missing deployment script '$Path'."
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    Assert-True (@($errors).Count -eq 0) `
        "PowerShell parse failure in '$Path': $(@($errors | ForEach-Object { $_.Message }) -join '; ')"
    return $ast
}

function Get-FunctionAst {
    param(
        [Parameter(Mandatory = $true)]$Ast,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $matches = @($Ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name
    }, $true))
    Assert-True ($matches.Count -eq 1) "Expected exactly one function '$Name' in '$Path'."
    return $matches[0]
}

function Get-CommandNames {
    param([Parameter(Mandatory = $true)]$Ast)
    return @($Ast.FindAll({
        param($node) $node -is [Management.Automation.Language.CommandAst]
    }, $true) | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ })
}

$pilotAst = Get-ParsedScript -Path $pilotPath
$productionAst = Get-ParsedScript -Path $productionPath
$pilotSource = Get-Content -LiteralPath $pilotPath -Raw
$productionSource = Get-Content -LiteralPath $productionPath -Raw

# The migration is an explicit, isolated parameter set and is bounded to the one deployed v1 path.
$migrateParameters = @($pilotAst.ParamBlock.Parameters | Where-Object {
    $_.Name.VariablePath.UserPath -ceq 'MigrateLegacyStateProtection'
})
Assert-True ($migrateParameters.Count -eq 1) 'The legacy migration switch parameter is missing or duplicated.'
$migrateAttributes = @($migrateParameters[0].Attributes | ForEach-Object { $_.Extent.Text }) -join "`n"
Assert-True ($migrateAttributes -match "ParameterSetName\s*=\s*'MigrateLegacyStateProtection'") `
    'The legacy migration switch must have its own parameter set.'
Assert-True ($migrateAttributes -match 'Mandatory\s*=\s*\$true') `
    'The legacy migration switch must be mandatory in its parameter set.'
Assert-True ($pilotSource -match [regex]::Escape("`$legacyStatePath = 'C:\ProgramData\SonAero\deployment-state\https-pilot.json'")) `
    'The migration must name the exact already-deployed legacy state path.'
Assert-True ($pilotSource -match '\$MigrateLegacyStateProtection\s+-and\s+\$StatePath\s+-ine\s+\(Get-CanonicalStatePath\s+\$legacyStatePath\)') `
    'The migration must compare the canonical StatePath to the exact deployed legacy path.'
Assert-True ($pilotSource -match 'Legacy migration is restricted to the exact deployed state path') `
    'The migration exact-path check must fail closed with an operator-facing error.'
Assert-True ($pilotSource -match '\$MigrateLegacyStateProtection\s+-or\s+-not\s+\$WhatIfPreference\)\s*\{\s*Assert-Administrator') `
    'Migration, including -WhatIf, must require an elevated administrator.'

$migrationIfs = @($pilotAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text -match '^if\s*\(\$MigrateLegacyStateProtection\)\s*\{' -and
        $node.Extent.Text -match 'Open-LegacyPilotStateForMigration'
}, $true))
Assert-True ($migrationIfs.Count -eq 1) 'Expected one bounded top-level legacy migration branch.'
$migrationAst = $migrationIfs[0]
$migrationText = $migrationAst.Extent.Text

# Normal state reads remain fail-closed; only the explicit migration opener can parse legacy ACLs.
$readStateText = (Get-FunctionAst -Ast $pilotAst -Name 'Read-State' -Path $pilotPath).Extent.Text
$readProtectionIndex = $readStateText.LastIndexOf('Assert-PilotStateProtection')
$readContentIndex = $readStateText.IndexOf('Get-Content')
Assert-True ($readProtectionIndex -ge 0 -and $readContentIndex -gt $readProtectionIndex) `
    'Read-State must validate the protected file and directory ACL before parsing JSON.'
Assert-True ($readStateText.IndexOf('Assert-NoReparsePointInStatePathChain') -lt $readContentIndex) `
    'Read-State must reject reparse paths before parsing JSON.'
Assert-True ($readStateText -notmatch 'Open-LegacyPilotStateForMigration') `
    'Normal Read-State must not fall back to the legacy migration reader.'

$openText = (Get-FunctionAst -Ast $pilotAst -Name 'Open-LegacyPilotStateForMigration' -Path $pilotPath).Extent.Text
$nativeText = (Get-FunctionAst -Ast $pilotAst -Name 'Initialize-LegacyStateNativeMethods' -Path $pilotPath).Extent.Text
$liveText = (Get-FunctionAst -Ast $pilotAst -Name 'Assert-LegacyPilotLiveState' -Path $pilotPath).Extent.Text
$strictText = (Get-FunctionAst -Ast $pilotAst -Name 'Assert-StrictLegacyPilotState' -Path $pilotPath).Extent.Text

# The untrusted legacy file is bounded, locked, single-link, strict UTF-8, and hashed while open.
Assert-True ($openText -match 'Assert-NoReparsePointInStatePathChain') `
    'The legacy opener must reparse-check the complete path chain.'
Assert-True ($openText -match '1MB' -and $openText -match 'nonempty') `
    'The legacy opener must enforce its nonempty 1 MiB input bound.'
Assert-True ($openText -match '\[IO\.FileShare\]::None') `
    'The legacy opener must hold an exclusive file handle for the migration transaction.'
Assert-True ($openText -match 'GetLinkCount\(\$stream\.SafeFileHandle\)\s+-ne\s+1') `
    'The legacy opener must reject a file with any hard links.'
Assert-True ($nativeText -match 'GetFileInformationByHandle' -and $nativeText -match 'NumberOfLinks') `
    'The legacy hard-link guard must query the opened Windows file handle.'
Assert-True ($openText -match 'UTF8Encoding\(\$false,\s*\$true\)') `
    'The legacy opener must reject malformed non-UTF-8 state bytes.'
Assert-True ($openText -match 'Sha256\s*=\s*Get-Sha256Hex\s+-Bytes\s+\$bytes') `
    'The legacy opener must hash the exact bytes read from the exclusive handle.'
Assert-True ($openText -notmatch '\[IO\.FileAccess\]::Write|FileMode\]::(?:Create|CreateNew|Append|Truncate|OpenOrCreate)') `
    'The legacy opener must not request content-write access or create a state file.'

$contentHashIndex = $migrationText.IndexOf('(Get-Sha256Hex -Bytes $currentBytes)')
$fileAclIndex = $migrationText.IndexOf('$openedState.Stream.SetAccessControl')
$directoryAclIndex = $migrationText.IndexOf('Set-Acl -LiteralPath (Split-Path -Parent $StatePath)')
$postHashIndex = $migrationText.IndexOf('Get-FileHash -LiteralPath $StatePath -Algorithm SHA256')
Assert-True ($contentHashIndex -ge 0 -and $contentHashIndex -lt $fileAclIndex) `
    'Migration must re-hash the still-open file immediately before changing protection.'
Assert-True ($migrationText -match 'GetLinkCount\(\$openedState\.Stream\.SafeFileHandle\)\s+-ne\s+1') `
    'Migration must re-check hard-link count immediately before changing protection.'
Assert-True ($fileAclIndex -ge 0 -and $directoryAclIndex -gt $fileAclIndex) `
    'Migration must protect the exclusively opened file before protecting its directory.'
Assert-True ($postHashIndex -gt $directoryAclIndex) `
    'Migration must verify the protected path content hash after both ACL changes.'
Assert-True ($migrationText -match 'Assert-PilotStateProtection' -and $migrationText -match 'Read-State') `
    'Migration must re-enter the normal protected reader after applying ACLs.'

# Live corroboration covers the certificate, exact IIS binding set, firewall scope, and both retained surfaces.
$requiredLiveCalls = @(
    'Assert-Certificate', 'Get-IisBindingSnapshot', 'Assert-RequiredHttpBindings',
    'Assert-TargetBindingsAvailable', 'Get-TargetBindingSnapshot', 'Get-FirewallSnapshot',
    'Assert-FirewallAvailable'
)
$liveCommands = @(Get-CommandNames -Ast (Get-FunctionAst -Ast $pilotAst -Name 'Assert-LegacyPilotLiveState' -Path $pilotPath))
foreach ($requiredCall in $requiredLiveCalls) {
    Assert-True ($liveCommands -ccontains $requiredCall) `
        "Legacy live validation must call '$requiredCall'."
}
Assert-True ($liveText -match 'Wait-Health\s+-Scheme\s+https' -and $liveText -match 'Wait-Health\s+-Scheme\s+http') `
    'Legacy live validation must health-check both HTTPS pilot and retained HTTP endpoints.'
Assert-True ($liveText -match '\$liveTarget\.Count\s+-ne\s+\$applications\.Count') `
    'Legacy live validation must require exactly five live pilot bindings.'
Assert-True ($liveText -match 'Validated\.ExpectedBindings') `
    'Legacy live validation must compare current bindings with the strictly validated recorded binding set.'

# The migration call graph is read-only except for the two explicit ACL changes.
$migrationFunctions = @(
    'Open-LegacyPilotStateForMigration', 'Assert-StrictLegacyPilotState',
    'Assert-LegacyPilotLiveState', 'Test-PilotStateProtectionCurrent'
)
$mutationCommands = @(
    'New-NetFirewallRule', 'Set-NetFirewallRule', 'Remove-NetFirewallRule',
    'New-WebBinding', 'Remove-WebBinding', 'Set-WebBinding',
    'Set-TargetBindingsFromSnapshot', 'Add-HttpsBindings', 'Restore-HubTargetBindings',
    'Write-State', 'Write-ProductionState', 'Set-Content', 'Add-Content', 'Clear-Content',
    'Out-File', 'Copy-Item', 'Move-Item', 'Rename-Item', 'Remove-Item'
)
$migrationCommands = @(Get-CommandNames -Ast $migrationAst)
foreach ($functionName in $migrationFunctions) {
    $functionAst = Get-FunctionAst -Ast $pilotAst -Name $functionName -Path $pilotPath
    $migrationCommands += @(Get-CommandNames -Ast $functionAst)
    Assert-True ($functionAst.Extent.Text -notmatch '\.CommitChanges\s*\(|\[IO\.File\]::(?:Write|Replace|Move|Delete|Create)|\.Write\s*\(') `
        "Migration helper '$functionName' must not mutate IIS, firewall, or state content."
}
foreach ($mutationCommand in $mutationCommands) {
    Assert-True ($migrationCommands -cnotcontains $mutationCommand) `
        "The legacy migration path must not invoke mutating command '$mutationCommand'."
}
Assert-True ($migrationText -notmatch '\.CommitChanges\s*\(|\[IO\.File\]::(?:Write|Replace|Move|Delete|Create)|\.Write\s*\(') `
    'The legacy migration branch must not mutate IIS, firewall, or JSON content.'
Assert-True ($migrationCommands -ccontains 'Set-Acl' -and $migrationText -match '\.SetAccessControl\(') `
    'The migration branch must limit its writes to explicit file and directory ACL changes.'

# Preview, idempotence, and success have distinct machine-checkable outcomes.
foreach ($token in @(
    'WHATIF_READY_HTTPS_PILOT_STATE_PROTECTION_MIGRATION',
    'HTTPS_PILOT_STATE_PROTECTION_ALREADY_CURRENT',
    'HTTPS_PILOT_STATE_PROTECTION_MIGRATED'
)) {
    Assert-True ($migrationText.Contains($token)) "Migration output token '$token' is missing."
}
$firstLiveIndex = $migrationText.IndexOf('Assert-LegacyPilotLiveState')
$alreadyCurrentIndex = $migrationText.IndexOf('HTTPS_PILOT_STATE_PROTECTION_ALREADY_CURRENT')
$shouldProcessIndex = $migrationText.IndexOf('$PSCmdlet.ShouldProcess')
$preAclLiveIndex = $migrationText.IndexOf('Assert-LegacyPilotLiveState', $shouldProcessIndex + 1)
Assert-True ($firstLiveIndex -ge 0 -and $alreadyCurrentIndex -gt $firstLiveIndex) `
    'An already-protected state must still pass strict live corroboration before idempotent success.'
Assert-True ($shouldProcessIndex -gt $alreadyCurrentIndex -and $fileAclIndex -gt $shouldProcessIndex) `
    'No ACL may change before the migration ShouldProcess gate.'
Assert-True ($preAclLiveIndex -gt $shouldProcessIndex -and $preAclLiveIndex -lt $fileAclIndex) `
    'Live authority must be revalidated after confirmation and immediately before ACL mutation.'

# Pilot migration and production binding changes share the same fail-fast global transaction lock.
$sharedMutexName = 'Global\SonAero-HubHttpsBindingTransactions'
$pilotLockText = (Get-FunctionAst -Ast $pilotAst -Name 'Enter-HubHttpsBindingTransactionLock' -Path $pilotPath).Extent.Text
$productionLockText = (Get-FunctionAst -Ast $productionAst -Name 'Enter-HubHttpsBindingTransactionLock' -Path $productionPath).Extent.Text
Assert-True ($pilotSource.Contains("`$bindingTransactionMutexName = '$sharedMutexName'")) `
    'Pilot must declare the shared HTTPS binding mutex literal.'
Assert-True ($productionLockText.Contains("'$sharedMutexName'")) `
    'Production must acquire the identical shared HTTPS binding mutex literal.'
Assert-True ($pilotLockText -match 'WaitOne\(0\)' -and $productionLockText -match 'WaitOne\(0\)') `
    'Both binding transactions must acquire the shared mutex without waiting.'

foreach ($entry in @(
    [pscustomobject]@{ Ast = $pilotAst; Path = $pilotPath; RequiredCall = 'Open-LegacyPilotStateForMigration' },
    [pscustomobject]@{ Ast = $productionAst; Path = $productionPath; RequiredCall = 'Read-State' }
)) {
    $lockAssignments = @($entry.Ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
            $node.Extent.Text -match '^\$transactionMutex\s*=\s*Enter-HubHttpsBindingTransactionLock\s*$'
    }, $true))
    Assert-True ($lockAssignments.Count -eq 1) "'$($entry.Path)' must acquire the shared lock exactly once."
    $lockOffset = $lockAssignments[0].Extent.StartOffset
    $protectedCalls = @($entry.Ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst]
    }, $true) | Where-Object { $_.GetCommandName() -ceq $entry.RequiredCall })
    Assert-True ($protectedCalls.Count -gt 0) "'$($entry.Path)' has no '$($entry.RequiredCall)' call to order."
    foreach ($call in $protectedCalls) {
        Assert-True ($call.Extent.StartOffset -gt $lockOffset) `
            "'$($entry.Path)' calls '$($entry.RequiredCall)' before acquiring the shared transaction lock."
    }
}
$pilotReadCalls = @($pilotAst.FindAll({
    param($node) $node -is [Management.Automation.Language.CommandAst]
}, $true) | Where-Object { $_.GetCommandName() -ceq 'Read-State' })
$pilotLockOffset = @($pilotAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Extent.Text -match '^\$transactionMutex\s*=\s*Enter-HubHttpsBindingTransactionLock\s*$'
}, $true))[0].Extent.StartOffset
foreach ($call in $pilotReadCalls) {
    Assert-True ($call.Extent.StartOffset -gt $pilotLockOffset) `
        'Pilot normal state reads must occur only after acquiring the shared transaction lock.'
}

# Exercise the strict state validator independently of IIS and firewall dependencies.
$script:expectedComputerName = 'SON-IIS2'
$script:certificateStoreName = 'My'
$script:applications = @(
    [pscustomobject]@{ Site = 'ProjectTracker'; HttpPort = 5135; HttpsPort = 6135 },
    [pscustomobject]@{ Site = 'SonAeroPortal'; HttpPort = 5140; HttpsPort = 6140 },
    [pscustomobject]@{ Site = 'EngineeringHub'; HttpPort = 5150; HttpsPort = 6150 },
    [pscustomobject]@{ Site = 'EstimatingDashboard'; HttpPort = 5160; HttpsPort = 6160 },
    [pscustomobject]@{ Site = 'QualityAssurance'; HttpPort = 5170; HttpsPort = 6170 }
)
$validatorFunctions = @(
    'Convert-HashToHex', 'Convert-ToPilotAddress', 'Get-TargetBindingSnapshot',
    'Assert-RequiredHttpBindings', 'Get-ComparableTargetBindings', 'Assert-PriorTargetBindings',
    'Assert-ExactPropertySet', 'ConvertFrom-StrictUtcTimestamp', 'Assert-BindingSnapshotShape',
    'New-ExpectedPilotBindingSnapshot', 'Assert-StrictLegacyPilotState'
)
foreach ($functionName in $validatorFunctions) {
    $functionText = (Get-FunctionAst -Ast $pilotAst -Name $functionName -Path $pilotPath).Extent.Text
    . ([scriptblock]::Create($functionText))
}

function New-ValidLegacyState {
    $leaf = '00112233445566778899AABBCCDDEEFF00112233'
    $root = 'FFEEDDCCBBAA99887766554433221100FFEEDDCC'
    $before = @()
    $applied = @()
    foreach ($application in $script:applications) {
        $before += [pscustomobject]@{
            Site = [string]$application.Site
            Protocol = 'http'
            BindingInformation = "*:$($application.HttpPort):"
            CertificateHash = ''
            CertificateStoreName = ''
            SslFlags = [int]0
        }
        $applied += [pscustomobject]@{
            Site = [string]$application.Site
            Protocol = 'https'
            BindingInformation = "*:$($application.HttpsPort):"
            CertificateHash = $leaf
            CertificateStoreName = 'My'
            SslFlags = [int]0
        }
    }
    return [pscustomobject]@{
        Version = [int]1
        ComputerName = 'SON-IIS2'
        Status = 'Applied'
        PreparedAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(-2).ToString('o')
        CertificateThumbprint = $leaf
        PilotRootThumbprint = $root
        PilotRemoteAddress = [object[]]@('10.50.10.25')
        AllBindingsBefore = [object[]]@($before)
        PriorTargetBindings = [object[]]@()
        FirewallBefore = [pscustomobject]@{ Existed = $false }
        FirewallRuleAdded = $true
        AppliedTargetBindings = [object[]]@($applied)
        AppliedAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o')
        RolledBackAtUtc = $null
        ApplyFailure = $null
        ApplyFailedAtUtc = $null
        RollbackFailure = $null
        RollbackFailedAtUtc = $null
    }
}

try {
    $validated = Assert-StrictLegacyPilotState -State (New-ValidLegacyState)
    Assert-True ($validated.Thumbprint -ceq '00112233445566778899AABBCCDDEEFF00112233') `
        'The strict validator did not accept the exact valid leaf thumbprint.'
    Assert-True (@($validated.ExpectedBindings).Count -eq 5) `
        'The strict validator must produce exactly five expected live pilot bindings.'

    Assert-Throws { $state = New-ValidLegacyState; $state.Version = [int]2; Assert-StrictLegacyPilotState $state } `
        'Strict legacy validation accepted a non-v1 state.'
    Assert-Throws { $state = New-ValidLegacyState; $state.ComputerName = 'son-iis2'; Assert-StrictLegacyPilotState $state } `
        'Strict legacy validation accepted a non-exact computer name.'
    Assert-Throws { $state = New-ValidLegacyState; $state.Status = 'Prepared'; Assert-StrictLegacyPilotState $state } `
        'Strict legacy validation accepted a state that was not Applied.'
    Assert-Throws {
        $state = New-ValidLegacyState
        $state.PriorTargetBindings = [object[]]@($state.AppliedTargetBindings[0])
        Assert-StrictLegacyPilotState $state
    } 'Strict legacy validation accepted a nonempty pre-pilot 61xx binding baseline.'
    Assert-Throws {
        $state = New-ValidLegacyState
        $state.AppliedTargetBindings = [object[]]@($state.AppliedTargetBindings | Select-Object -First 4)
        Assert-StrictLegacyPilotState $state
    } 'Strict legacy validation accepted fewer than five AppliedTargetBindings.'
    Assert-Throws {
        $state = New-ValidLegacyState
        $state.AppliedTargetBindings[0].BindingInformation = '*:6199:'
        Assert-StrictLegacyPilotState $state
    } 'Strict legacy validation accepted an unexpected pilot binding port.'
    Assert-Throws {
        $state = New-ValidLegacyState
        $state.AllBindingsBefore += $state.AppliedTargetBindings[0]
        Assert-StrictLegacyPilotState $state
    } 'Strict legacy validation accepted a preexisting pilot-port binding in AllBindingsBefore.'
    Assert-Throws {
        $state = New-ValidLegacyState
        $state.FirewallBefore.Existed = $true
        Assert-StrictLegacyPilotState $state
    } 'Strict legacy validation accepted an unsafe preexisting firewall-rule baseline.'
    Assert-Throws {
        $state = New-ValidLegacyState
        $state.FirewallRuleAdded = $false
        Assert-StrictLegacyPilotState $state
    } 'Strict legacy validation accepted state that did not own the pilot firewall rule.'
    Assert-Throws {
        $state = New-ValidLegacyState
        $state.PilotRemoteAddress = [object[]]@('Any')
        Assert-StrictLegacyPilotState $state
    } 'Strict legacy validation accepted an unconstrained firewall remote address.'
    Assert-Throws {
        $state = New-ValidLegacyState
        $state | Add-Member -MemberType NoteProperty -Name Unexpected -Value 'value'
        Assert-StrictLegacyPilotState $state
    } 'Strict legacy validation accepted an unexpected schema property.'
}
finally {
    foreach ($functionName in $validatorFunctions) {
        Remove-Item -LiteralPath "Function:\$functionName" -ErrorAction SilentlyContinue
    }
}

Write-Output 'HUB_HTTPS_PILOT_LEGACY_STATE_MIGRATION_TESTS_PASSED'
