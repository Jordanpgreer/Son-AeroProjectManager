$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SubcontractingReleaseIis.psm1')

function Restore-SubcontractingTransaction {
    param([Parameter(Mandatory = $true)]$State, [int]$HealthTimeoutSeconds=180)
    if ($State.FirstInstall) {
        Remove-SubcontractingOwnedSite -State $State
    }
    else {
        $current = Get-SubcontractingIisBoundary -Thumbprint $State.Thumbprint -SkipEnvironment
        if ($current.SiteId -ne $State.Prior.SiteId -or
            $current.Path -notin @($State.Candidate, $State.Prior.Path)) {
            throw 'Rollback refused: current IIS resource differs from recorded prior/candidate ownership.'
        }
        if ((Get-FileHash -LiteralPath (Join-Path $State.Prior.Path 'appsettings.Production.json') -Algorithm SHA256).Hash -cne $State.ConfigurationHash) {
            throw 'Rollback refused: prior Production configuration changed.'
        }
        if ($current.Path -ieq $State.Prior.Path -and $current.SiteState -eq 'Started' -and $current.PoolState -eq 'Started') {
            Wait-SubcontractingHealth -TimeoutSeconds $HealthTimeoutSeconds
            return
        }
        Set-SubcontractingRuntimeState -State Stopped
        if ($current.Path -ine $State.Prior.Path) {
            Set-SubcontractingPhysicalPath -Expected $State.Candidate -Destination $State.Prior.Path
        }
        Set-SubcontractingRuntimeState -State Started
        Wait-SubcontractingHealth -TimeoutSeconds $HealthTimeoutSeconds
    }
}

function Invoke-SubcontractingReleaseTransaction {
    param([Parameter(Mandatory = $true)]$State, [Parameter(Mandatory = $true)][hashtable]$Actions)
    # Persist the ownership intent before any candidate or IIS mutation. Both ordinary failures
    # and interrupted execution retain the same recovery contract.
    & $Actions.Persist $State
    try {
        & $Actions.Prepare $State
        $State.Phase = 'Switching'
        & $Actions.Persist $State
        & $Actions.Switch $State
        & $Actions.Health $State
        & $Actions.Verify $State
        $State.Phase = 'Healthy'
        & $Actions.Persist $State
    }
    catch {
        $failure = $_
        try {
            & $Actions.Restore $State
            $State.Phase = 'RolledBack'
            & $Actions.Persist $State
        }
        catch {
            $State.Phase = 'RollbackFailed'
            & $Actions.Persist $State
            throw "Release failed and rollback requires investigation. Transaction $($State.Id). Rollback: $($_.Exception.Message). Original: $($failure.Exception.Message)"
        }
        throw "Release failed; prior IIS state restored. Preserve candidate and transaction $($State.Id). Database/storage changes are not reversed. $($failure.Exception.Message)"
    }
}

Export-ModuleMember -Function Restore-SubcontractingTransaction, Invoke-SubcontractingReleaseTransaction
