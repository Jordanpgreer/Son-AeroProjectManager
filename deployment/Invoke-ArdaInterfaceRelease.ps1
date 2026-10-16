<#
    Release the Arda inbox/sidebar/20-minute Fulcrum update on an existing SON-IIS2 installation.
    Requires a verified commit on origin/main and an interactive backup attestation.
    This does not install a new module or provision SQL databases.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedCommit
)

& {
    $ErrorActionPreference = 'Stop'
    $verifiedApply = $false
    try {
        if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Use Windows PowerShell 5.1.' }
        if ($env:COMPUTERNAME -ine 'SON-IIS2') { throw 'Run only on SON-IIS2.' }
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if ($identity.IsSystem -or $identity.Name -notmatch '^SON4L\\' -or -not [Environment]::UserInteractive) {
            throw 'Run interactively as an authorized SON4L domain user, not Local System.'
        }
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Use an elevated Windows PowerShell 5.1 window.'
        }
        $repositoryPath = 'C:\SonAero\src\SonAeroInternalHub'
        if ([IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\') -ine $repositoryPath) {
            throw "Run the script from the verified checkout at $repositoryPath."
        }
        Set-Location -LiteralPath $repositoryPath
        function Invoke-CheckedGit {
            param([string[]]$Arguments)
            $result = @(& git @Arguments)
            if ($LASTEXITCODE -ne 0) { throw "Git failed: $($Arguments -join ' ')" }
            return $result
        }
        function Assert-CleanCheckout {
            $status = @(Invoke-CheckedGit -Arguments @('status', '--porcelain'))
            if ($status.Count -ne 0) { throw 'Server checkout is dirty. Stop and preserve the changes; do not stash or reset.' }
        }
        $branch = @(Invoke-CheckedGit -Arguments @('branch', '--show-current')) -join ''
        if ($branch -cne 'main') { throw 'The server checkout must already be on main.' }
        Assert-CleanCheckout
        Invoke-CheckedGit -Arguments @('fetch', '--prune', 'origin') | Out-Host
        $remoteCommit = @(Invoke-CheckedGit -Arguments @('rev-parse', 'origin/main')) -join ''
        if ($remoteCommit -cne $ExpectedCommit) { throw 'origin/main differs from the reviewed release SHA. Obtain a fresh handoff.' }
        Invoke-CheckedGit -Arguments @('pull', '--ff-only', 'origin', 'main') | Out-Host
        $headCommit = @(Invoke-CheckedGit -Arguments @('rev-parse', 'HEAD')) -join ''
        if ($headCommit -cne $ExpectedCommit) { throw 'HEAD differs from the reviewed release SHA.' }
        Assert-CleanCheckout

        Import-Module WebAdministration -ErrorAction Stop
        $includeSubcontracting = Test-Path 'IIS:\Sites\SmallBusinessSubcontracting'
        $applicationNames = @('ProjectTracker', 'SonAeroPortal', 'EngineeringHub', 'EstimatingDashboard', 'QualityAssurance')
        if ($includeSubcontracting) { $applicationNames += 'SmallBusinessSubcontracting' }
        foreach ($applicationName in $applicationNames) {
            if (-not (Test-Path "IIS:\Sites\$applicationName")) { throw "Required existing site is missing: $applicationName" }
            $pool = Get-Item "IIS:\AppPools\$applicationName"
            $site = Get-Item "IIS:\Sites\$applicationName"
            $preload = Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
                -Filter "system.applicationHost/sites/site[@name='$applicationName']/application[@path='/']" -Name preloadEnabled
            if (-not [bool]$pool.autoStart -or -not [bool]$site.serverAutoStart -or
                [string]$pool.startMode -ne 'AlwaysRunning' -or $pool.processModel.idleTimeout -ne [TimeSpan]::Zero -or -not [bool]$preload.Value) {
                throw 'IIS residency needs repair. Use Configure-IisWarmStart.ps1 -Scheme https -PermanentHttps with preview/apply and the installed-module flag, then obtain a new release run.'
            }
            if ([string]$site.state -ne 'Started') { throw "Existing site is not started: $applicationName" }
            if ((Get-WebAppPoolState -Name $applicationName).Value -ne 'Started') { throw "Existing pool is not started: $applicationName" }
        }

        Write-Host 'Confirm current restorable SQL backups, active Quality storage (including SQLite if used), drawing storage, and installed Subcontracting database/documents.'
        Write-Host 'Backup readiness checks alone are not backup/restore verification. Verify shared ProjectTracker SQL permissions and the active Fulcrum credential separately.'
        if ((Read-Host 'Type BACKUPS_VERIFIED after the required backups and restore evidence are confirmed') -cne 'BACKUPS_VERIFIED') {
            throw 'Backup attestation was not provided. Nothing was deployed.'
        }
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $releaseId = 'arda-' + $ExpectedCommit.Substring(0, 12) + '-' + $stamp
        if ($releaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') { throw 'Invalid release ID.' }
        $packageRoot = Join-Path 'C:\SonAero\staging' $releaseId
        $releaseRoot = 'C:\SonAero\releases'
        if ((Test-Path -LiteralPath $packageRoot) -or (Test-Path -LiteralPath (Join-Path $releaseRoot $releaseId))) {
            throw 'Package or release path already exists; use a fresh run, never overwrite.'
        }
        $publishArguments = @{ OutputRoot = $packageRoot; ProjectTrackerUrl = '/project-tracker-api'; Configuration = 'Release' }
        $deployArguments = @{
            PackageRoot = $packageRoot; ReleaseId = $releaseId; ReleaseRoot = $releaseRoot
            HealthTimeoutSeconds = 300; EnableUniversalFulcrumSync = $true
        }
        if ($includeSubcontracting) {
            $publishArguments.IncludeSmallBusinessSubcontracting = $true
            $deployArguments.IncludeSmallBusinessSubcontracting = $true
        }
        & .\deployment\Publish-Hub.ps1 @publishArguments
        Assert-CleanCheckout
        $previewOutput = @(& .\deployment\Deploy-HubRelease.ps1 @deployArguments -WhatIf)
        $previewOutput | Out-Host
        if (@($previewOutput | Where-Object { $_ -is [string] -and $_ -ceq 'WHATIF_READY' }).Count -ne 1) {
            throw 'Preview did not produce the exact WHATIF_READY marker. Stop.'
        }
        $applyOutput = @(& .\deployment\Deploy-HubRelease.ps1 @deployArguments -Confirm:$false)
        $applyOutput | Out-Host
        if (@($applyOutput | Where-Object { $_ -is [string] -and $_ -ceq 'HUB_RELEASE_DEPLOYED_AND_HEALTHY' }).Count -ne 1) {
            throw 'Apply did not produce the exact HUB_RELEASE_DEPLOYED_AND_HEALTHY marker. Stop; do not retry blindly.'
        }
        $verifiedApply = $true
        $origins = @(
            'https://hub.son4l.local', 'https://projects.hub.son4l.local', 'https://engineering.hub.son4l.local',
            'https://estimating.hub.son4l.local', 'https://quality.hub.son4l.local', 'https://hub.son4l.local/project-tracker-api'
        )
        if ($includeSubcontracting) { $origins += 'https://subcontracting.hub.son4l.local' }
        foreach ($origin in $origins) {
            $response = Invoke-WebRequest -Uri "$origin/api/health" -UseBasicParsing -UseDefaultCredentials -TimeoutSec 30
            if ($response.StatusCode -ne 200) { throw "HTTPS health failed for $origin" }
        }
        Write-Host "Release: $releaseId; commit: $ExpectedCommit"
        Write-Host 'Complete the browser and scheduled-sync checks in docs/arda-interface-release.md. Health alone does not prove upstream synchronization.'
        Write-Output 'ARDA_INTERFACE_RELEASE_DEPLOYED_HEALTHY'
    }
    catch {
        if ($verifiedApply) { Write-Warning 'POSTCHECK_STOP_AFTER_VERIFIED_APPLY: diagnose the existing release; do not redeploy.' }
        else { Write-Warning 'RELEASE_STOP: inspect the failure and any rollback report before taking further action.' }
        throw
    }
}
