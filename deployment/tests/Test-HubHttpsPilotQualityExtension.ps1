[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw "Run this focused compatibility test with Windows PowerShell 5.1; current version is $($PSVersionTable.PSVersion)."
}

$deploymentRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $deploymentRoot 'Configure-HubHttpsPilotQualityExtension.ps1'

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    Assert-True $threw $Message
}
function Get-FunctionAst($Ast, [string]$Name) {
    $matches = @($Ast.FindAll({
        param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name
    }, $true))
    Assert-True ($matches.Count -eq 1) "Expected exactly one function '$Name'."
    return $matches[0]
}
function Get-Commands($Ast) {
    return @($Ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true) |
        ForEach-Object { $_.GetCommandName() } | Where-Object { $_ })
}

Assert-True (Test-Path -LiteralPath $scriptPath -PathType Leaf) "Missing '$scriptPath'."
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
Assert-True (@($errors).Count -eq 0) "PS5 parser errors: $(@($errors | ForEach-Object Message) -join '; ')"
$source = Get-Content -LiteralPath $scriptPath -Raw

# Public contract: only apply/rollback controls, with no operator-selected authority.
Assert-True ($ast.ParamBlock.Attributes.Extent.Text -match 'SupportsShouldProcess') 'Script must support -WhatIf.'
$parameterNames = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
foreach ($name in @('Rollback','MinimumRemainingDays','HealthTimeoutSeconds','HistoricalStatePath','StatePath')) {
    Assert-True ($parameterNames -ccontains $name) "Missing parameter '$name'."
}
foreach ($forbidden in @('CertificateThumbprint','PilotRootThumbprint','PilotRemoteAddress')) {
    Assert-True ($parameterNames -cnotcontains $forbidden) "Authority must not be operator supplied through '$forbidden'."
}
Assert-True ($source.Contains("'C:\ProgramData\SonAero\deployment-state\https-pilot.json'")) `
    'Historical state must use the exact deployed path.'
Assert-True ($source.Contains("'C:\ProgramData\SonAero\deployment-state\https-pilot-quality-extension.json'")) `
    'Extension state must use its exact separate path.'
Assert-True ($source -match 'HistoricalStatePath must be the exact deployed path' -and
    $source -match 'StatePath must be the exact quality-extension path') `
    'Both state paths must fail closed on an override.'

# Both privileged reads validate the complete path and SYSTEM/Admin-only ACLs first.
$readText = (Get-FunctionAst $ast 'Read-ProtectedJson').Extent.Text
$contentIndex = $readText.IndexOf('Get-Content')
Assert-True ($contentIndex -gt $readText.LastIndexOf('Assert-ProtectedStateFile', $contentIndex)) `
    'Protected ACL validation must precede JSON parsing.'
Assert-True ($readText.IndexOf('Assert-NoReparsePathChain') -lt $contentIndex) `
    'Reparse-chain validation must precede JSON parsing.'
$aclText = (Get-FunctionAst $ast 'New-ProtectedFileSystemSecurity').Extent.Text
Assert-True ($aclText -match 'S-1-5-18' -and $aclText -match 'S-1-5-32-544' -and
    $aclText -match 'SetAccessRuleProtection\(\$true,\s*\$false\)') `
    'State ACLs must disable inheritance and allow only SYSTEM/Administrators.'
$writeText = (Get-FunctionAst $ast 'Write-ExtensionState').Extent.Text
foreach ($pattern in @("\[Guid\]::NewGuid\(\)\.ToString\('N'\)",'FileMode\]::CreateNew',
    'FileShare\]::None','Flush\(\$true\)','\[IO\.File\]::Replace','Assert-ProtectedStateFile')) {
    Assert-True ($writeText -match $pattern) "Atomic protected state writer lacks '$pattern'."
}

# The shared lock is acquired before either historical or extension state can be read.
Assert-True ($source.Contains("`$mutexName = 'Global\SonAero-HubHttpsBindingTransactions'")) `
    'The extension must share the pilot/production binding mutex.'
$lockText = (Get-FunctionAst $ast 'Enter-TransactionLock').Extent.Text
Assert-True ($lockText -match 'WaitOne\(0\)') 'The shared lock must fail immediately under contention.'
$lockAssignment = @($ast.FindAll({
    param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Extent.Text -match '^\$transactionMutex\s*=\s*Enter-TransactionLock\s*$'
}, $true))
Assert-True ($lockAssignment.Count -eq 1) 'The shared mutex must be acquired exactly once.'
$lockOffset = $lockAssignment[0].Extent.StartOffset
foreach ($readCall in @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true) |
    Where-Object { $_.GetCommandName() -ceq 'Read-ProtectedJson' })) {
    Assert-True ($readCall.Extent.StartOffset -gt $lockOffset) 'State was read before acquiring the shared mutex.'
}

# Historical authority is exact-four and drives certificate, root, and remote scope.
$historyText = (Get-FunctionAst $ast 'Assert-HistoricalState').Extent.Text
Assert-True ($historyText -match "Status\s+-cne\s+'Applied'" -and $historyText -match 'Version\s+-ne\s+1' -and
    $historyText -match 'ComputerName\s+-cne\s+\$expectedComputerName') `
    'Historical state must be exact v1 SON-IIS2 Applied state.'
Assert-True ($historyText -match 'PriorTargetBindings\)\.Count\s+-ne\s+0' -and
    $historyText -match 'AppliedTargetBindings\)\.Count\s+-ne\s+4') `
    'Historical state must have an empty prior baseline and exactly four applied bindings.'
Assert-True ($historyText -match "Site\s+-eq\s+'QualityAssurance'" -and
    $historyText -match 'FirewallRuleAdded' -and $historyText -match 'FirewallBefore\.Existed') `
    'Authentic historical state must predate QA and own creation of the pilot firewall.'
Assert-True ($source -match 'Assert-Certificate\s+\$history\.Thumbprint\s+\$history\.RootThumbprint') `
    'Certificate validation must use only protected historical authority.'

# Apply snapshots every binding except the sole QA target; 443 remains guarded.
$unrelatedText = (Get-FunctionAst $ast 'Get-UnrelatedBindings').Extent.Text
Assert-True ($unrelatedText -match "qualityApplication\.Site" -and $unrelatedText -match "qualityApplication\.HttpsPort") `
    'Only the exact QA 6170 target may be excluded from unrelated bindings.'
Assert-True ($unrelatedText -notmatch '443') 'Shared 443 bindings must not be excluded from drift protection.'
Assert-True ($source -match '\$unrelated\s*=\s*@\(Get-UnrelatedBindings\s+\$before\)') `
    'Apply must snapshot unrelated bindings before mutation.'
Assert-True ($source -match 'UnrelatedBindingsBefore\s*=\s*\$unrelated') `
    'Prepared state must durably own the unrelated-binding baseline.'
$preparedWriteIndex = $source.LastIndexOf('Write-ExtensionState $state', $source.IndexOf('Add-QaBinding $history.Thumbprint'))
$firstAddIndex = $source.IndexOf('Add-QaBinding $history.Thumbprint')
Assert-True ($preparedWriteIndex -ge 0 -and $firstAddIndex -gt $preparedWriteIndex) `
    'Prepared protected state must be written before the first IIS mutation.'
Assert-True ($source.IndexOf("Read-ProtectedJson `$StatePath 'Quality-extension state'", $preparedWriteIndex) -lt $firstAddIndex -and
    $source.IndexOf('Assert-RecoverableState $state $history', $preparedWriteIndex) -lt $firstAddIndex) `
    'Prepared state and exact live baseline must be revalidated before the first IIS mutation.'

# Mutation surface is limited to QA 6170 and the existing firewall port filter.
$addAst = Get-FunctionAst $ast 'Add-QaBinding'; $removeAst = Get-FunctionAst $ast 'Remove-QaBinding'
$addText = $addAst.Extent.Text; $removeText = $removeAst.Extent.Text
Assert-True ($addText -match 'qualityApplication\.Site' -and $addText -match 'qualityApplication\.HttpsPort' -and
    $addText -match "Bindings\.Add" -and $addText -match "'https'") 'Apply must add only QA HTTPS 6170.'
Assert-True ($removeText -match 'qualityApplication\.HttpsPort' -and
    $removeText -match 'Bindings\.Remove') 'Rollback must remove only QA HTTPS 6170.'
Assert-True ($removeText -match 'foreach\s*\(\$site\s+in\s+\$manager\.Sites\)' -and
    $removeText -match 'Get-ComparableBindings\s+@\(\$matches\[0\]\.Snapshot\)' -and
    $removeText -match 'PlannedQaBinding') `
    'Rollback removal must enumerate every site and remove only the exact transaction-owned QA binding.'
$firewallMutationText = (Get-FunctionAst $ast 'Set-FirewallPorts').Extent.Text
Assert-True ($firewallMutationText -match 'Set-NetFirewallPortFilter' -and
    $firewallMutationText -notmatch 'New-NetFirewallRule|Remove-NetFirewallRule') `
    'The transaction may update only the existing firewall port filter.'
Assert-True ($firewallMutationText -match 'Get-FirewallSnapshot' -and
    $firewallMutationText -match 'ExpectedCurrentPorts' -and
    $firewallMutationText -match 'AlternateCurrentPorts' -and $firewallMutationText -match 'Assert-Firewall') `
    'The firewall mutator must take and validate a fresh exact snapshot immediately before mutation.'
Assert-True ($source -notmatch 'New-NetFirewallRule|Remove-NetFirewallRule') `
    'The extension must never create or delete the historical firewall rule.'

# Apply, rollback, recovery, health, and idempotence remain machine-checkable.
foreach ($token in @(
    'WHATIF_READY_HTTPS_PILOT_QA_EXTENSION',
    'HTTPS_PILOT_QA_EXTENSION_APPLIED_AND_FIVE_SITE_HEALTHY',
    'HTTPS_PILOT_QA_EXTENSION_ALREADY_APPLIED_AND_FIVE_SITE_HEALTHY',
    'WHATIF_READY_HTTPS_PILOT_QA_EXTENSION_ROLLBACK',
    'HTTPS_PILOT_QA_EXTENSION_ROLLED_BACK_AND_FOUR_SITE_HEALTHY',
    'HTTPS_PILOT_QA_EXTENSION_ALREADY_ROLLED_BACK_AND_FOUR_SITE_HEALTHY'
)) { Assert-True $source.Contains($token) "Missing output token '$token'." }
foreach ($status in @('Prepared','Applied','ApplyFailedRollbackPending','RollbackPending','RollbackFailed',
    'RolledBack','AutomaticallyRolledBack')) {
    Assert-True $source.Contains("'$status'") "State machine lacks status '$status'."
}
Assert-True ($source -match 'Restore-FourSite\s+\$state\s+\$history') `
    'Apply failure must invoke the same deterministic four-site restoration primitive.'
$fourText = (Get-FunctionAst $ast 'Assert-FourSiteLive').Extent.Text
$fiveText = (Get-FunctionAst $ast 'Assert-FiveSiteLive').Extent.Text
Assert-True ($fourText -match 'Wait-Health\s+http\s+@\(\$applications\.HttpPort\)' -and
    $fourText -match 'Wait-Health\s+https\s+@\(\$historicalApplications\.HttpsPort\)') `
    'Four-site verification must test all five HTTP and four historical HTTPS surfaces.'
Assert-True ($fiveText -match 'Wait-Health\s+http\s+@\(\$applications\.HttpPort\)' -and
    $fiveText -match 'Wait-Health\s+https\s+@\(\$applications\.HttpsPort\)') `
    'Five-site verification must test all five HTTP and HTTPS surfaces.'
Assert-True ($source -match 'target\s+-cne\s+\$four\s+-and\s+\$target\s+-cne\s+\$five') `
    'Crash recovery must accept only exact four- or five-site IIS target states.'
Assert-True ($source -match '-not\s+\$fourFirewall\s+-and\s+-not\s+\$fiveFirewall') `
    'Crash recovery must accept only exact four- or five-port firewall states.'
Assert-True ((Get-FunctionAst $ast 'Restore-FourSite').Extent.Text -match '^function\s+Restore-FourSite[\s\S]*?\{\s*Assert-RecoverableState') `
    'Rollback must validate the exact recoverable tuple before persisting or mutating.'
$restoreText = (Get-FunctionAst $ast 'Restore-FourSite').Extent.Text
Assert-True ($restoreText.IndexOf('Assert-RecoverableState', $restoreText.IndexOf('Write-ExtensionState')) -lt
    $restoreText.IndexOf('Remove-QaBinding')) `
    'Rollback must revalidate the tuple after durable RollbackPending state and before removal.'
Assert-True ($restoreText.IndexOf('Get-FirewallSnapshot', $restoreText.IndexOf('Remove-QaBinding')) -lt
    $restoreText.IndexOf('Set-FirewallPorts')) `
    'Rollback must revalidate firewall state immediately before contracting its ports.'
Assert-True ($source -match '\$state\s*=\s*Read-ProtectedJson\s+\$StatePath[\s\S]{0,300}HistoricalStateSha256[\s\S]{0,300}Assert-RecoverableState\s+\$state\s+\$history[\s\S]{0,100}try\s*\{\s*Add-QaBinding') `
    'Post-Prepared protected reread must become authoritative and revalidate history/live state before QA mutation.'

# Exercise pure validation with realistic state and binding snapshots.
$script:expectedComputerName = 'SON-IIS2'; $script:storeName = 'My'
$script:applications = @(
    [pscustomobject]@{ Site='ProjectTracker'; HttpPort=5135; HttpsPort=6135 },
    [pscustomobject]@{ Site='SonAeroPortal'; HttpPort=5140; HttpsPort=6140 },
    [pscustomobject]@{ Site='EngineeringHub'; HttpPort=5150; HttpsPort=6150 },
    [pscustomobject]@{ Site='EstimatingDashboard'; HttpPort=5160; HttpsPort=6160 },
    [pscustomobject]@{ Site='QualityAssurance'; HttpPort=5170; HttpsPort=6170 }
)
$script:historicalApplications = @($script:applications | Select-Object -First 4)
$script:qualityApplication = $script:applications[4]
$pureFunctions = @('Convert-HashToHex','Convert-ToRemoteAddresses','Get-ComparableBindings','Get-TargetBindings',
    'Get-UnrelatedBindings','New-ExpectedBinding','Assert-HttpBindings','Assert-HistoricalBindings','Assert-Firewall',
    'Assert-BindingShape','Assert-ExactProperties','Assert-HistoricalState','Assert-ExtensionState')
foreach ($name in $pureFunctions) { . ([scriptblock]::Create((Get-FunctionAst $ast $name).Extent.Text)) }

function New-HistoricalState {
    $leaf='00112233445566778899AABBCCDDEEFF00112233'; $root='FFEEDDCCBBAA99887766554433221100FFEEDDCC'
    $before=@(); $applied=@()
    foreach ($app in $script:historicalApplications) {
        $before += [pscustomobject]@{ Site=$app.Site;Protocol='http';BindingInformation="*:$($app.HttpPort):";CertificateHash='';CertificateStoreName='';SslFlags=[int]0 }
        $applied += New-ExpectedBinding $app $leaf
    }
    return [pscustomobject]@{
        Version=[int]1;ComputerName='SON-IIS2';Status='Applied';PreparedAtUtc=[DateTime]::UtcNow.AddMinutes(-2).ToString('o')
        CertificateThumbprint=$leaf;PilotRootThumbprint=$root;PilotRemoteAddress=[object[]]@('10.50.10.25')
        AllBindingsBefore=[object[]]$before;PriorTargetBindings=[object[]]@();FirewallBefore=[pscustomobject]@{Existed=$false}
        FirewallRuleAdded=$true;AppliedTargetBindings=[object[]]$applied;AppliedAtUtc=[DateTime]::UtcNow.AddMinutes(-1).ToString('o')
        RolledBackAtUtc=$null;ApplyFailure=$null;ApplyFailedAtUtc=$null;RollbackFailure=$null;RollbackFailedAtUtc=$null
    }
}
try {
    $history = Assert-HistoricalState (New-HistoricalState)
    Assert-True ($history.Thumbprint -ceq '00112233445566778899AABBCCDDEEFF00112233') 'Valid history was rejected.'
    Assert-Throws { $s=New-HistoricalState; $s.AppliedTargetBindings=@($s.AppliedTargetBindings|Select-Object -First 3); Assert-HistoricalState $s } `
        'Three-site history was accepted.'
    Assert-Throws {
        $s=New-HistoricalState; $s.AppliedTargetBindings += New-ExpectedBinding $script:qualityApplication $s.CertificateThumbprint
        Assert-HistoricalState $s
    } 'Five-site history was accepted as authentic four-site history.'
    Assert-Throws {
        $s=New-HistoricalState; $s.AllBindingsBefore += [pscustomobject]@{Site='QualityAssurance';Protocol='http';BindingInformation='*:5170:';CertificateHash='';CertificateStoreName='';SslFlags=[int]0}
        Assert-HistoricalState $s
    } 'Historical baseline claiming QA was accepted.'

    $snapshot=@()
    foreach($app in $script:applications){$snapshot += [pscustomobject]@{Site=$app.Site;Protocol='http';BindingInformation="*:$($app.HttpPort):";CertificateHash='';CertificateStoreName='';SslFlags=0}}
    foreach($app in $script:historicalApplications){$snapshot += New-ExpectedBinding $app $history.Thumbprint}
    $snapshot += [pscustomobject]@{Site='SonAeroPortal';Protocol='https';BindingInformation='*:443:hub.son4l.local';CertificateHash=$history.Thumbprint;CertificateStoreName='My';SslFlags=1}
    Assert-HttpBindings $snapshot; Assert-HistoricalBindings $snapshot $history.Thumbprint
    Assert-True (@(Get-UnrelatedBindings $snapshot | Where-Object BindingInformation -eq '*:443:hub.son4l.local').Count -eq 1) `
        'Unrelated snapshot dropped the 443 binding.'
    $five=@($snapshot)+(New-ExpectedBinding $script:qualityApplication $history.Thumbprint)
    Assert-HistoricalBindings $five $history.Thumbprint -AllowQuality
    Assert-Throws { Assert-HistoricalBindings $five $history.Thumbprint } 'Four-site validator accepted QA 6170.'

    $firewall=[pscustomobject]@{Existed=$true;Enabled='True';Direction='Inbound';Action='Allow';Profile='Domain,Private';Protocol='TCP';LocalPort=@('6135','6140','6150','6160');RemoteAddress=@('10.50.10.25')}
    Assert-Firewall $firewall $history.RemoteAddress @($script:historicalApplications.HttpsPort)
    Assert-Throws {$firewall.LocalPort=@('6135','6140','6150','6160','6170');Assert-Firewall $firewall $history.RemoteAddress @($script:historicalApplications.HttpsPort)} `
        'Four-site firewall guard accepted QA 6170.'
    Assert-Throws {$firewall.LocalPort=@('Any');Assert-Firewall $firewall $history.RemoteAddress @($script:historicalApplications.HttpsPort)} `
        'Firewall guard accepted a broad port token.'

    # Windows PowerShell 5.1 must preserve each expected port set as one [int[]].
    $setFirewallText = (Get-FunctionAst $ast 'Set-FirewallPorts').Extent.Text
    . ([scriptblock]::Create($setFirewallText))
    function Get-FirewallSnapshot { return $script:testFirewallSnapshot }
    function Get-NetFirewallRule { return [pscustomobject]@{ DisplayName = 'SON-AERO Hub HTTPS pilot' } }
    function Get-NetFirewallPortFilter { param([Parameter(ValueFromPipeline = $true)]$InputObject) process { return $InputObject } }
    function Set-NetFirewallPortFilter {
        param([Parameter(ValueFromPipeline = $true)]$InputObject, [string]$Protocol, [int[]]$LocalPort)
        process { $script:capturedFirewallPorts = @($LocalPort); return $InputObject }
    }
    $script:firewallRuleName = 'SON-AERO Hub HTTPS pilot'
    $script:testFirewallSnapshot = [pscustomobject]@{
        Existed=$true;Enabled='True';Direction='Inbound';Action='Allow';Profile='Domain,Private';Protocol='TCP'
        LocalPort=@('6135','6140','6150','6160');RemoteAddress=@('10.50.10.25')
    }
    $script:capturedFirewallPorts = @()
    Set-FirewallPorts -Ports @(6135,6140,6150,6160,6170) -RemoteAddress @('10.50.10.25') `
        -ExpectedCurrentPorts @(6135,6140,6150,6160)
    Assert-True (($script:capturedFirewallPorts -join ',') -eq '6135,6140,6150,6160,6170') `
        'Single allowed-current apply port set was flattened under Windows PowerShell 5.1.'
    $script:testFirewallSnapshot.LocalPort = @('6135','6140','6150','6160','6170')
    $script:capturedFirewallPorts = @()
    Set-FirewallPorts -Ports @(6135,6140,6150,6160) -RemoteAddress @('10.50.10.25') `
        -ExpectedCurrentPorts @(6135,6140,6150,6160) -AlternateCurrentPorts @(6135,6140,6150,6160,6170)
    Assert-True (($script:capturedFirewallPorts -join ',') -eq '6135,6140,6150,6160') `
        'Alternate rollback port set did not bind as one exact array under Windows PowerShell 5.1.'
}
finally {
    foreach ($name in @($pureFunctions + @('Set-FirewallPorts','Get-FirewallSnapshot','Get-NetFirewallRule',
        'Get-NetFirewallPortFilter','Set-NetFirewallPortFilter'))) {
        Remove-Item -LiteralPath "Function:\$name" -ErrorAction SilentlyContinue
    }
}

Write-Output 'HUB_HTTPS_PILOT_QUALITY_EXTENSION_TESTS_PASSED'
