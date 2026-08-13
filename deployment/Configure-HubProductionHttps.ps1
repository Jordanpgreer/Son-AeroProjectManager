<#
    Transactionally add the permanent, hostname-based production HTTPS bindings on SON-IIS2.

    The script owns only these five exact SNI bindings on TCP 443:
      hub.son4l.local, projects/engineering/estimating/quality.hub.son4l.local

    Existing HTTP 5135-5170 and pilot HTTPS 6135-6170 bindings are immutable transaction guards.
    Any failed authenticated health check restores the exact prior 443 target state.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'Apply')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Apply')]
    [ValidatePattern('^(?:[A-Fa-f0-9]{2}\s*){20}$')]
    [string]$CertificateThumbprint,
    [Parameter(Mandatory = $true, ParameterSetName = 'Rollback')]
    [switch]$Rollback,
    [ValidateSet('SON-IIS2')]
    [string]$ExpectedComputerName = 'SON-IIS2',
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedServerAddress = '10.50.10.244',
    [ValidateRange(7, 365)]
    [int]$MinimumRemainingDays = 30,
    [ValidateRange(30, 600)]
    [int]$HealthTimeoutSeconds = 180,
    [string]$StatePath = 'C:\ProgramData\SonAero\deployment-state\https-production-hostnames.json'
)

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'HubProductionHttps.Common.psm1'
$stateRoot = 'C:\ProgramData\SonAero\deployment-state'
if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
    throw "Required production HTTPS module was not found at '$modulePath'."
}
Import-Module $modulePath -Force -ErrorAction Stop
$applications = @(Get-HubProductionApplicationMap)

function Get-CanonicalPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path)).TrimEnd('\')
}

function Assert-NoReparsePathChain {
    param([Parameter(Mandatory = $true)][string]$Path)
    $current = Get-CanonicalPath $Path
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "State path '$Path' traverses reparse point '$current'."
            }
        }
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) { break }
        $current = $parent
    }
}

function New-ProtectedFileSystemSecurity {
    param([switch]$Directory)
    $security = if ($Directory) { New-Object Security.AccessControl.DirectorySecurity }
        else { New-Object Security.AccessControl.FileSecurity }
    $security.SetAccessRuleProtection($true, $false)
    $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $security.SetOwner($administrators)
    $inheritance = if ($Directory) { [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' }
        else { [Security.AccessControl.InheritanceFlags]::None }
    foreach ($identity in @($system, $administrators)) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $identity, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance,
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
    }
    return $security
}

function Assert-ProtectedPath {
    param([Parameter(Mandatory = $true)][string]$Path, [switch]$Directory)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Protected state path '$Path' must not be a reparse point." }
    if ($Directory -and -not $item.PSIsContainer) { throw "Protected state directory '$Path' is not a directory." }
    if (-not $Directory -and $item.PSIsContainer) { throw "Protected state file '$Path' is not a file." }
    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) { throw "Protected state path '$Path' still inherits access rules." }
    $allowedSids = @('S-1-5-18', 'S-1-5-32-544')
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin $allowedSids) { throw "Protected state path '$Path' has unexpected owner '$owner'." }
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 2) { throw "Protected state path '$Path' must contain exactly two access rules." }
    $fullControlSids = @()
    foreach ($rule in $rules) {
        $sid = $rule.IdentityReference.Value
        if ($sid -notin $allowedSids -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) {
            throw "Protected state path '$Path' grants access to unexpected identity '$sid'."
        }
        if (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq
            [Security.AccessControl.FileSystemRights]::FullControl) { $fullControlSids += $sid }
    }
    foreach ($sid in $allowedSids) {
        if ($fullControlSids -notcontains $sid) { throw "Protected state path '$Path' does not grant full control to '$sid'." }
    }
}

function Assert-ProductionStateProtection {
    param([Parameter(Mandatory = $true)][string]$Path)
    Assert-NoReparsePathChain -Path $Path
    Assert-ProtectedPath -Path (Split-Path -Parent $Path) -Directory
    Assert-ProtectedPath -Path $Path
}

function Write-ProductionState {
    param([Parameter(Mandatory = $true)]$State)
    Assert-NoReparsePathChain -Path $StatePath
    $directory = Split-Path -Parent $StatePath
    $directorySecurity = New-ProtectedFileSystemSecurity -Directory
    if (-not (Test-Path -LiteralPath $directory)) {
        [void][IO.Directory]::CreateDirectory($directory, $directorySecurity)
    }
    Assert-NoReparsePathChain -Path $directory
    $directoryItem = Get-Item -LiteralPath $directory -Force -ErrorAction Stop
    if (-not $directoryItem.PSIsContainer -or ($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Protected state directory '$directory' is missing, is not a directory, or is a reparse point."
    }
    Set-Acl -LiteralPath $directory -AclObject $directorySecurity
    Assert-ProtectedPath -Path $directory -Directory
    if (Test-Path -LiteralPath $StatePath) { Assert-ProductionStateProtection -Path $StatePath }
    $temporary = Join-Path $directory ((Split-Path -Leaf $StatePath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $json = ($State | ConvertTo-Json -Depth 12) + [Environment]::NewLine
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json)
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
        Assert-NoReparsePathChain -Path $temporary
        Set-Acl -LiteralPath $temporary -AclObject (New-ProtectedFileSystemSecurity)
        Assert-ProtectedPath -Path $temporary
        if (Test-Path -LiteralPath $StatePath) { [IO.File]::Replace($temporary, $StatePath, $null) }
        else { Move-Item -LiteralPath $temporary -Destination $StatePath }
        Assert-ProductionStateProtection -Path $StatePath
    }
    finally {
        $temporaryItem = Get-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        if ($temporaryItem -and -not $temporaryItem.PSIsContainer -and
            ($temporaryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
}

function Assert-SafeStatePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullRoot = Get-CanonicalPath $stateRoot
    $fullPath = Get-CanonicalPath $Path
    $commonApplicationData = Get-CanonicalPath ([Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonApplicationData))
    $configuredCommonApplicationData = Split-Path -Parent (Split-Path -Parent $fullRoot)
    if ($configuredCommonApplicationData -ine $commonApplicationData) {
        throw "Configured deployment-state root '$fullRoot' is not under canonical CommonApplicationData '$commonApplicationData'."
    }
    if ((Split-Path -Parent $fullPath) -ine $fullRoot -or [IO.Path]::GetExtension($fullPath) -ine '.json') {
        throw "StatePath must be a JSON file directly under '$fullRoot'."
    }
    Assert-NoReparsePathChain -Path $fullPath
    return $fullPath
}

function Get-NonTargetBindings {
    param([Parameter(Mandatory = $true)][object[]]$Snapshot)
    $targets = @(Get-HubTargetBindingSnapshot -Snapshot $Snapshot -Applications $applications)
    $targetKeys = @{}
    foreach ($target in $targets) {
        $targetKeys[('{0}|{1}|{2}' -f $target.Site, $target.Protocol, $target.BindingInformation).ToLowerInvariant()] = $true
    }
    return @($Snapshot | Where-Object {
        $key = ('{0}|{1}|{2}' -f $_.Site, $_.Protocol, $_.BindingInformation).ToLowerInvariant()
        -not $targetKeys.ContainsKey($key)
    })
}

function Assert-StateProperties {
    param([Parameter(Mandatory = $true)]$State, [Parameter(Mandatory = $true)][string[]]$Names)
    foreach ($name in $Names) {
        if ($State.PSObject.Properties.Name -notcontains $name) { throw "Transaction state is missing '$name'." }
    }
}

function Assert-SavedTargetBindings {
    param([AllowEmptyCollection()][object[]]$Bindings)
    $seen = @{}
    foreach ($binding in @($Bindings)) {
        Assert-StateProperties $binding @('Site', 'Protocol', 'BindingInformation', 'CertificateHash', 'CertificateStoreName', 'SslFlags')
        if ($binding.Protocol -ine 'https') { throw 'Saved target state contains a non-HTTPS binding.' }
        $parts = Split-HubBindingInformation $binding.BindingInformation
        $application = @($applications | Where-Object { $_.Site -eq $binding.Site -and $_.HostName -ieq $parts.HostName })
        if ($parts.Address -ne '*' -or $parts.Port -ne 443 -or $application.Count -ne 1) {
            throw 'Saved target state contains a binding outside the five production host names.'
        }
        $key = $application[0].HostName.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { throw "Saved target state contains duplicate host '$key'." }
        $seen[$key] = $true
        $null = ConvertTo-HubThumbprint ([string]$binding.CertificateHash)
        if ($binding.CertificateStoreName -ine 'My') { throw "Saved target '$key' is outside LocalMachine\\My." }
        if ([int]$binding.SslFlags -notin @(0, 1)) { throw "Saved target '$key' has unsupported SSL flags." }
    }
}

function Assert-SavedPlannedBindings {
    param([Parameter(Mandatory = $true)]$State)
    Assert-SavedTargetBindings @($State.PlannedTargetBindings)
    $planned = Get-HubComparableBindings @($State.PlannedTargetBindings)
    $expected = Get-HubComparableBindings @(New-HubDesiredBindingSnapshot `
        -Applications $applications -Thumbprint ([string]$State.CertificateThumbprint))
    if ($planned -ne $expected) {
        throw 'Transaction state planned bindings do not exactly match the five managed SNI bindings.'
    }
}

function Read-State {
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { throw "Transaction state was not found at '$StatePath'." }
    Assert-ProductionStateProtection -Path $StatePath
    try { $value = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json }
    catch { throw "Transaction state at '$StatePath' is not valid JSON: $($_.Exception.Message)" }
    Assert-StateProperties $value @(
        'Version', 'ComputerName', 'Status', 'CertificateThumbprint', 'PriorTargetBindings',
        'PlannedTargetBindings', 'NonTargetBindingsBefore'
    )
    if ([int]$value.Version -ne 2 -or $value.ComputerName -ine $ExpectedComputerName) {
        throw 'Transaction state belongs to another script version or computer.'
    }
    $null = ConvertTo-HubThumbprint ([string]$value.CertificateThumbprint)
    Assert-SavedTargetBindings @($value.PriorTargetBindings)
    Assert-SavedPlannedBindings $value
    return $value
}

function Set-StateProperty {
    param([Parameter(Mandatory = $true)]$State, [Parameter(Mandatory = $true)][string]$Name, $Value)
    if ($State.PSObject.Properties.Name -contains $Name) { $State.$Name = $Value }
    else { $State | Add-Member -MemberType NoteProperty -Name $Name -Value $Value }
}

function Try-WriteState {
    param([Parameter(Mandatory = $true)]$State)
    try { Write-ProductionState -State $State; return $null }
    catch { return $_.Exception.Message }
}

function Assert-NonTargetBindingsUnchanged {
    param([Parameter(Mandatory = $true)][object[]]$Snapshot, [Parameter(Mandatory = $true)][object[]]$Expected)
    $current = Get-HubComparableBindings @(Get-NonTargetBindings $Snapshot)
    $recorded = Get-HubComparableBindings @($Expected)
    if ($current -ne $recorded) {
        throw 'An HTTP, pilot, or unrelated IIS binding changed during the transaction; target operation refused.'
    }
}

function Assert-RecoverableTargetBindings {
    param([Parameter(Mandatory = $true)][object[]]$Snapshot, [Parameter(Mandatory = $true)]$State)
    $currentTargets = @(Get-HubTargetBindingSnapshot -Snapshot $Snapshot -Applications $applications)
    foreach ($application in $applications) {
        $current = @($currentTargets | Where-Object {
            (Split-HubBindingInformation $_.BindingInformation).HostName -ieq $application.HostName
        })
        $prior = @($State.PriorTargetBindings | Where-Object {
            (Split-HubBindingInformation $_.BindingInformation).HostName -ieq $application.HostName
        })
        $planned = @($State.PlannedTargetBindings | Where-Object {
            (Split-HubBindingInformation $_.BindingInformation).HostName -ieq $application.HostName
        })
        if ($current.Count -eq 0 -and $prior.Count -eq 0) { continue }
        if ($current.Count -ne 1) {
            throw "Current target state for '$($application.HostName)' is not recoverable from this transaction."
        }
        $actualValue = Get-HubComparableBindings @($current)
        $priorValue = if ($prior.Count -eq 1) { Get-HubComparableBindings @($prior) } else { $null }
        $plannedValue = Get-HubComparableBindings @($planned)
        if ($actualValue -ne $priorValue -and $actualValue -ne $plannedValue) {
            throw "Current target binding for '$($application.HostName)' drifted outside the saved prior/planned transaction state."
        }
    }
}

function Invoke-AutomaticRollback {
    param([Parameter(Mandatory = $true)]$State, [Parameter(Mandatory = $true)][string]$ApplyFailure)
    $State.Status = 'ApplyFailedRollbackPending'
    Set-StateProperty $State 'ApplyFailure' $ApplyFailure
    Set-StateProperty $State 'ApplyFailedAtUtc' ([DateTime]::UtcNow.ToString('o'))
    $initialStateFailure = Try-WriteState $State
    try {
        $current = @(Get-HubIisBindingSnapshot)
        Assert-NonTargetBindingsUnchanged -Snapshot $current -Expected @($State.NonTargetBindingsBefore)
        Assert-HubProductionBindingAvailability -Snapshot $current -Applications $applications `
            -Thumbprint ([string]$State.CertificateThumbprint)
        Assert-RecoverableTargetBindings -Snapshot $current -State $State
        Restore-HubTargetBindings -Applications $applications -Bindings @($State.PriorTargetBindings)
        $restored = @(Get-HubIisBindingSnapshot)
        Assert-HubBaseBindings -Snapshot $restored -Applications $applications
        Assert-NonTargetBindingsUnchanged -Snapshot $restored -Expected @($State.NonTargetBindingsBefore)
        $actual = Get-HubComparableBindings @(Get-HubTargetBindingSnapshot -Snapshot $restored -Applications $applications)
        $expected = Get-HubComparableBindings @($State.PriorTargetBindings)
        if ($actual -ne $expected) { throw 'Restored 443 target bindings do not match the pre-transaction snapshot.' }
        Wait-HubEndpointHealth -Applications $applications -Scheme http -TimeoutSeconds $HealthTimeoutSeconds `
            -ExpectedComputerName $ExpectedComputerName
        Wait-HubEndpointHealth -Applications $applications -Scheme pilotHttps -TimeoutSeconds $HealthTimeoutSeconds `
            -ExpectedComputerName $ExpectedComputerName
        $State.Status = 'AutomaticallyRolledBack'
        Set-StateProperty $State 'RolledBackAtUtc' ([DateTime]::UtcNow.ToString('o'))
        Set-StateProperty $State 'RollbackFailure' $null
        Write-ProductionState -State $State
    }
    catch {
        $State.Status = 'RollbackFailed'
        Set-StateProperty $State 'RollbackFailure' $_.Exception.Message
        Set-StateProperty $State 'RollbackFailedAtUtc' ([DateTime]::UtcNow.ToString('o'))
        $rollbackStateFailure = Try-WriteState $State
        $extra = @()
        if ($initialStateFailure) { $extra += "Could not persist apply failure: $initialStateFailure" }
        if ($rollbackStateFailure) { $extra += "Could not persist rollback failure: $rollbackStateFailure" }
        $suffix = if ($extra.Count -gt 0) { " State errors: $($extra -join ' ')" } else { '' }
        throw "Production HTTPS apply failed: $ApplyFailure Automatic rollback also failed: $($_.Exception.Message)$suffix"
    }
}

function Enter-HubHttpsBindingTransactionLock {
    $mutex = New-Object Threading.Mutex($false, 'Global\SonAero-HubHttpsBindingTransactions')
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) {
            throw 'Another SON-AERO HTTPS binding transaction is already running on SON-IIS2.'
        }
        return $mutex
    }
    catch {
        if (-not $acquired) { $mutex.Dispose() }
        throw
    }
}

if (-not [IO.Path]::IsPathRooted($StatePath)) { throw 'StatePath must be an absolute local path.' }
$StatePath = Assert-SafeStatePath $StatePath
Assert-HubComputerName $ExpectedComputerName
if (-not $WhatIfPreference) { Assert-HubAdministrator }
Import-HubIisAdministration
$transactionMutex = Enter-HubHttpsBindingTransactionLock

try {
if ($Rollback) {
    $state = Read-State
    $terminal = @('RolledBack', 'AutomaticallyRolledBack', 'RecoveredRolledBack')
    $recoverable = @('Applied', 'Prepared', 'ApplyFailedRollbackPending', 'ManualRollbackPending', 'RollbackFailed')
    if ([string]$state.Status -notin @($terminal + $recoverable)) {
        throw "Unknown transaction status '$($state.Status)'; rollback refused."
    }
    $current = @(Get-HubIisBindingSnapshot)
    Assert-HubBaseBindings -Snapshot $current -Applications $applications
    Assert-NonTargetBindingsUnchanged -Snapshot $current -Expected @($state.NonTargetBindingsBefore)
    $currentTarget = Get-HubComparableBindings @(Get-HubTargetBindingSnapshot -Snapshot $current -Applications $applications)
    $priorTarget = Get-HubComparableBindings @($state.PriorTargetBindings)
    if ([string]$state.Status -in $terminal) {
        if ($currentTarget -ne $priorTarget) { throw 'State says rollback completed, but current target bindings do not match the saved baseline.' }
        Wait-HubEndpointHealth -Applications $applications -Scheme http -TimeoutSeconds $HealthTimeoutSeconds `
            -ExpectedComputerName $ExpectedComputerName
        Wait-HubEndpointHealth -Applications $applications -Scheme pilotHttps -TimeoutSeconds $HealthTimeoutSeconds `
            -ExpectedComputerName $ExpectedComputerName
        Write-Output 'PRODUCTION_HTTPS_ALREADY_ROLLED_BACK_AND_RETAINED_HTTP_PILOT_HTTPS_HEALTHY'
        exit 0
    }
    if ([string]$state.Status -eq 'Applied') {
        Assert-StateProperties $state @('AppliedTargetBindings')
        Assert-SavedTargetBindings @($state.AppliedTargetBindings)
        $applied = Get-HubComparableBindings @($state.AppliedTargetBindings)
        $planned = Get-HubComparableBindings @($state.PlannedTargetBindings)
        if ($applied -ne $planned) { throw 'Recorded applied target bindings do not match the planned SNI bindings.' }
        if ($currentTarget -ne $applied) { throw 'Current production bindings drifted after apply; rollback refused.' }
    }
    else {
        Assert-HubProductionBindingAvailability -Snapshot $current -Applications $applications `
            -Thumbprint ([string]$state.CertificateThumbprint)
        Assert-RecoverableTargetBindings -Snapshot $current -State $state
    }
    if ($PSCmdlet.ShouldProcess($ExpectedComputerName, 'Restore the exact pre-production-HTTPS 443 target bindings')) {
        $state.Status = 'ManualRollbackPending'
        Set-StateProperty $state 'RollbackStartedAtUtc' ([DateTime]::UtcNow.ToString('o'))
        Write-ProductionState -State $state
        try {
            Restore-HubTargetBindings -Applications $applications -Bindings @($state.PriorTargetBindings)
            $restored = @(Get-HubIisBindingSnapshot)
            Assert-HubBaseBindings -Snapshot $restored -Applications $applications
            Assert-NonTargetBindingsUnchanged -Snapshot $restored -Expected @($state.NonTargetBindingsBefore)
            $restoredTarget = Get-HubComparableBindings @(Get-HubTargetBindingSnapshot -Snapshot $restored -Applications $applications)
            if ($restoredTarget -ne $priorTarget) { throw 'Restored target bindings do not match the recorded baseline.' }
            Wait-HubEndpointHealth -Applications $applications -Scheme http -TimeoutSeconds $HealthTimeoutSeconds `
                -ExpectedComputerName $ExpectedComputerName
            Wait-HubEndpointHealth -Applications $applications -Scheme pilotHttps -TimeoutSeconds $HealthTimeoutSeconds `
                -ExpectedComputerName $ExpectedComputerName
            $state.Status = 'RolledBack'
            Set-StateProperty $state 'RolledBackAtUtc' ([DateTime]::UtcNow.ToString('o'))
            Write-ProductionState -State $state
            Write-Output 'PRODUCTION_HTTPS_ROLLED_BACK_AND_RETAINED_HTTP_PILOT_HTTPS_HEALTHY'
        }
        catch {
            $state.Status = 'RollbackFailed'
            Set-StateProperty $state 'RollbackFailure' $_.Exception.Message
            Set-StateProperty $state 'RollbackFailedAtUtc' ([DateTime]::UtcNow.ToString('o'))
            $null = Try-WriteState $state
            throw
        }
    }
    elseif ($WhatIfPreference) { Write-Output 'WHATIF_READY_PRODUCTION_HTTPS_ROLLBACK: ownership and drift checks passed; nothing was changed.' }
    else { Write-Output 'PRODUCTION_HTTPS_ROLLBACK_CANCELLED' }
    exit 0
}

$requestedThumbprint = ConvertTo-HubThumbprint $CertificateThumbprint

if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
    $oldState = Read-State
    if ([string]$oldState.Status -eq 'Applied') {
        $stateThumbprint = ConvertTo-HubThumbprint ([string]$oldState.CertificateThumbprint)
        if ($stateThumbprint -ne $requestedThumbprint) {
            throw "Production HTTPS is recorded as applied with certificate $stateThumbprint. Validate the replacement certificate, run the documented rollback, and then apply the new thumbprint as a separate transaction."
        }
        $oldCurrent = @(Get-HubIisBindingSnapshot)
        Assert-HubBaseBindings -Snapshot $oldCurrent -Applications $applications
        Assert-NonTargetBindingsUnchanged -Snapshot $oldCurrent -Expected @($oldState.NonTargetBindingsBefore)
        Assert-HubProductionBindingAvailability -Snapshot $oldCurrent -Applications $applications `
            -Thumbprint ([string]$oldState.CertificateThumbprint)
        Assert-StateProperties $oldState @('AppliedTargetBindings')
        Assert-SavedTargetBindings @($oldState.AppliedTargetBindings)
        $actualApplied = Get-HubComparableBindings @(Get-HubTargetBindingSnapshot -Snapshot $oldCurrent -Applications $applications)
        $savedApplied = Get-HubComparableBindings @($oldState.AppliedTargetBindings)
        $savedPlanned = Get-HubComparableBindings @($oldState.PlannedTargetBindings)
        if ($savedApplied -ne $savedPlanned) { throw 'Applied production HTTPS state does not match its planned SNI bindings.' }
        if ($actualApplied -ne $savedApplied) { throw 'Applied production HTTPS state exists, but current target bindings drifted.' }
        $null = Assert-HubProductionCertificate -Thumbprint $requestedThumbprint -Applications $applications `
            -MinimumRemainingDays $MinimumRemainingDays
        Assert-HubProductionDns -Applications $applications -ExpectedServerAddress $ExpectedServerAddress
        Wait-HubEndpointHealth -Applications $applications -Scheme http -TimeoutSeconds $HealthTimeoutSeconds `
            -ExpectedComputerName $ExpectedComputerName
        Wait-HubEndpointHealth -Applications $applications -Scheme pilotHttps -TimeoutSeconds $HealthTimeoutSeconds `
            -ExpectedComputerName $ExpectedComputerName
        Wait-HubEndpointHealth -Applications $applications -Scheme https -TimeoutSeconds $HealthTimeoutSeconds `
            -ExpectedComputerName $ExpectedComputerName
        Write-Output 'PRODUCTION_HTTPS_ALREADY_CONFIGURED_AND_DUAL_SCHEME_HEALTHY'
        exit 0
    }
    if ([string]$oldState.Status -notin @('RolledBack', 'AutomaticallyRolledBack', 'RecoveredRolledBack')) {
        throw "Incomplete production HTTPS transaction '$($oldState.Status)' exists. Run -Rollback -WhatIf, then -Rollback -Confirm:`$false."
    }
    $baseline = @(Get-HubIisBindingSnapshot)
    $baselineTarget = Get-HubComparableBindings @(Get-HubTargetBindingSnapshot -Snapshot $baseline -Applications $applications)
    $recordedBaseline = Get-HubComparableBindings @($oldState.PriorTargetBindings)
    if ($baselineTarget -ne $recordedBaseline) { throw 'Completed rollback state no longer matches current target bindings; state was not overwritten.' }
}

$thumbprint = $requestedThumbprint
$certificate = Assert-HubProductionCertificate -Thumbprint $thumbprint -Applications $applications `
    -MinimumRemainingDays $MinimumRemainingDays
Assert-HubProductionDns -Applications $applications -ExpectedServerAddress $ExpectedServerAddress
$before = @(Get-HubIisBindingSnapshot)
Assert-HubBaseBindings -Snapshot $before -Applications $applications
Assert-HubProductionBindingAvailability -Snapshot $before -Applications $applications -Thumbprint $thumbprint

# Never begin an IIS transaction unless every retained HTTP endpoint is already healthy. These
# endpoints are the rollback target and remain available throughout the production HTTPS rollout.
Wait-HubEndpointHealth -Applications $applications -Scheme http -TimeoutSeconds $HealthTimeoutSeconds `
    -ExpectedComputerName $ExpectedComputerName
Wait-HubEndpointHealth -Applications $applications -Scheme pilotHttps -TimeoutSeconds $HealthTimeoutSeconds `
    -ExpectedComputerName $ExpectedComputerName

if (Test-HubDesiredBindings -Snapshot $before -Applications $applications -Thumbprint $thumbprint) {
    Wait-HubEndpointHealth -Applications $applications -Scheme https -TimeoutSeconds $HealthTimeoutSeconds `
        -ExpectedComputerName $ExpectedComputerName
    Write-Output 'PRODUCTION_HTTPS_ALREADY_CONFIGURED_AND_DUAL_SCHEME_HEALTHY'
    exit 0
}

$state = [pscustomobject]@{
    Version = 2
    ComputerName = $ExpectedComputerName
    Status = 'Prepared'
    PreparedAtUtc = [DateTime]::UtcNow.ToString('o')
    ExpectedServerAddress = $ExpectedServerAddress
    CertificateThumbprint = $thumbprint
    CertificateSubject = $certificate.Subject
    PriorTargetBindings = @(Get-HubTargetBindingSnapshot -Snapshot $before -Applications $applications)
    PlannedTargetBindings = @(New-HubDesiredBindingSnapshot -Applications $applications -Thumbprint $thumbprint)
    NonTargetBindingsBefore = @(Get-NonTargetBindings $before)
    AppliedTargetBindings = @()
    AppliedAtUtc = $null
    ApplyFailure = $null
    RollbackFailure = $null
    RolledBackAtUtc = $null
}

if (-not $PSCmdlet.ShouldProcess($ExpectedComputerName, 'Create/reconcile five managed-certificate SNI bindings on TCP 443')) {
    if ($WhatIfPreference) { Write-Output 'WHATIF_READY_PRODUCTION_HTTPS: certificate, DNS, IIS ownership, HTTP, and pilot binding guards passed; nothing was changed.' }
    else { Write-Output 'PRODUCTION_HTTPS_CONFIGURATION_CANCELLED' }
    exit 0
}

Write-ProductionState -State $state
try {
    Set-HubDesiredBindings -Applications $applications -Thumbprint $thumbprint
    $after = @(Get-HubIisBindingSnapshot)
    Assert-HubBaseBindings -Snapshot $after -Applications $applications
    Assert-NonTargetBindingsUnchanged -Snapshot $after -Expected @($state.NonTargetBindingsBefore)
    Assert-HubProductionBindingAvailability -Snapshot $after -Applications $applications -Thumbprint $thumbprint
    if (-not (Test-HubDesiredBindings -Snapshot $after -Applications $applications -Thumbprint $thumbprint)) {
        throw 'The five desired SNI bindings were not present after IIS commit.'
    }
    Wait-HubEndpointHealth -Applications $applications -Scheme https -TimeoutSeconds $HealthTimeoutSeconds `
        -ExpectedComputerName $ExpectedComputerName
    Wait-HubEndpointHealth -Applications $applications -Scheme pilotHttps -TimeoutSeconds $HealthTimeoutSeconds `
        -ExpectedComputerName $ExpectedComputerName
    Wait-HubEndpointHealth -Applications $applications -Scheme http -TimeoutSeconds $HealthTimeoutSeconds `
        -ExpectedComputerName $ExpectedComputerName
    $state.Status = 'Applied'
    $state.AppliedAtUtc = [DateTime]::UtcNow.ToString('o')
    $state.AppliedTargetBindings = @(Get-HubTargetBindingSnapshot -Snapshot $after -Applications $applications)
    Write-ProductionState -State $state
    Write-Output 'PRODUCTION_HTTPS_CONFIGURED_AND_DUAL_SCHEME_HEALTHY'
}
catch {
    $failure = $_.Exception.Message
    Invoke-AutomaticRollback -State $state -ApplyFailure $failure
    throw "Production HTTPS transaction failed and was automatically rolled back. Original failure: $failure"
}
}
finally {
    try { $transactionMutex.ReleaseMutex() }
    finally { $transactionMutex.Dispose() }
}
