<#
  Operator entry point after SQL/storage/DNS prerequisites and a verified Git pull.
  Publishes six apps, updates the existing five, installs Subcontracting while hidden,
  and includes it in startup recovery. Grant module access and activate its card afterward.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$ExpectedCommit,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
$repositoryPath = 'C:\SonAero\src\SonAeroInternalHub'
$completedStages = @()

function Assert-InstallationSource {
    param([string]$Commit)
    $branch = (& git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -cne 'main') { throw 'Production checkout must be on main.' }
    $dirty = @(& git status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count) { throw 'Production checkout is dirty; preserve local work and investigate.' }
    $head = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ine $Commit) { throw 'Checked-out source does not match ExpectedCommit.' }
    & git fetch --prune origin
    if ($LASTEXITCODE -ne 0) { throw 'Git fetch failed.' }
    $remote = (& git rev-parse origin/main).Trim()
    if ($LASTEXITCODE -ne 0 -or $remote -ine $Commit) { throw 'origin/main changed; obtain a reviewed command for the new release.' }
}

function Invoke-InstallationStep {
    param([string]$Script, [hashtable]$Arguments, [string]$ExpectedMarker)
    $scriptPath = if ([IO.Path]::IsPathRooted($Script)) { $Script } else { Join-Path $PSScriptRoot $Script }
    $output = @(& $scriptPath @Arguments 4>&1 6>&1 | ForEach-Object {
        $line = $_.ToString().Trim()
        Write-Host $line
        $line
    })
    if ($output -cnotcontains $ExpectedMarker) { throw "$Script did not emit $ExpectedMarker. Preserve the complete output." }
}

function Assert-InstallationHiddenCatalog {
    param([Parameter(Mandatory = $true)]$Configuration, [switch]$RequireEntry)
    if (-not $Configuration.Portal -or -not $Configuration.Portal.Applications) { throw 'Active Portal catalog is missing.' }
    $entries = @($Configuration.Portal.Applications | Where-Object Id -EQ 'small-business-subcontracting')
    if ($entries.Count -eq 0 -and -not $RequireEntry) { return }
    if ($entries.Count -ne 1 -or $entries[0].Status -cne 'Maintenance' -or
        $entries[0].Url -cne 'https://subcontracting.hub.son4l.local' -or
        (@($entries[0].AllowedRoles) -join '|') -cne '__production-disabled__') {
        throw 'The existing Subcontracting Portal entry is not the reviewed hidden policy. Review and stage it before first installation.'
    }
}

function Assert-InstallationPortalHidden {
    param([switch]$RequireEntry)
    $portal = Get-Website -Name 'SonAeroPortal' -ErrorAction Stop
    if (-not $portal) { throw 'The existing Portal IIS site is missing.' }
    $portalPath = [Environment]::ExpandEnvironmentVariables([string]$portal.physicalPath)
    $configuration = Get-Content -LiteralPath (Join-Path $portalPath 'appsettings.Production.json') -Raw |
        ConvertFrom-Json -ErrorAction Stop
    Assert-InstallationHiddenCatalog -Configuration $configuration -RequireEntry:$RequireEntry
}

try {
    if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1 -or
        -not [Environment]::Is64BitProcess -or $env:COMPUTERNAME -ine 'SON-IIS2') {
        throw 'Run on SON-IIS2 in 64-bit Windows PowerShell 5.1.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($identity.IsSystem -or $identity.Name -notlike 'SON4L\*' -or -not [Environment]::UserInteractive -or
        -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run interactively as an authorized SON4L operator in an elevated session.'
    }
    if ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\') -ine $repositoryPath) {
        throw "Run the reviewed script from $repositoryPath\deployment."
    }
    Set-Location -LiteralPath $repositoryPath
    Assert-InstallationSource -Commit $ExpectedCommit
    Import-Module WebAdministration -ErrorAction Stop
    if ((Test-Path 'IIS:\Sites\SmallBusinessSubcontracting') -or
        (Test-Path 'IIS:\AppPools\SmallBusinessSubcontracting')) {
        throw 'This entry point is first installation only. Inspect an existing/partial installation before using update or recovery mode.'
    }
    Assert-InstallationPortalHidden
    $releaseId = '{0}-sbs-{1}' -f $ExpectedCommit.Substring(0, 12).ToLowerInvariant(), (Get-Date -Format 'yyyyMMdd-HHmmss')
    $packageRoot = Join-Path 'C:\SonAero\staging' ('hub-' + $releaseId)
    if (Test-Path -LiteralPath $packageRoot) { throw 'Fresh package path already exists; do not reuse it.' }
    & (Join-Path $PSScriptRoot 'Publish-Hub.ps1') -OutputRoot $packageRoot `
        -ProjectTrackerUrl '/project-tracker-api' -Configuration Release -IncludeSmallBusinessSubcontracting
    foreach ($folder in @('ProjectTracker','Portal','EngineeringHub','EstimatingDashboard','QualityAssurance','SmallBusinessSubcontracting')) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageRoot "$folder\web.config") -PathType Leaf)) {
            throw "Fresh package is missing $folder."
        }
    }
    Assert-InstallationSource -Commit $ExpectedCommit
    $existingHub = @{ PackageRoot = $packageRoot; ReleaseId = $releaseId
        ReleaseRoot = 'C:\SonAero\releases'; ExpectedComputerName = 'SON-IIS2'; HealthTimeoutSeconds = 300 }
    $module = @{ PackageRoot = $packageRoot; ReleaseId = $releaseId; FirstInstall = $true
        CertificateThumbprint = $CertificateThumbprint
        ApprovedDocumentRoot = '\\SON-SQL2\SmallBusinessSubcontracting$\Documents'
        ProductionSettingsPath = (Join-Path $PSScriptRoot 'templates\small-business-subcontracting.appsettings.Production.json')
        HealthTimeoutSeconds = 300 }

    # Validate both transactions before either is applied. The new site is intentionally
    # absent during the baseline five-app transaction; its Production card remains hidden.
    $hubPreview = $existingHub.Clone(); $hubPreview.WhatIf = $true
    Invoke-InstallationStep 'Deploy-HubRelease.ps1' $hubPreview 'WHATIF_READY'
    $modulePreview = $module.Clone(); $modulePreview.WhatIf = $true
    Invoke-InstallationStep 'Deploy-SmallBusinessSubcontractingRelease.ps1' $modulePreview `
        'WHATIF_READY_SMALL_BUSINESS_SUBCONTRACTING_RELEASE'
    $attestation = Read-Host 'After verifying restorable backups and restore evidence for ALL affected SQL databases, Quality storage, and document stores, type BACKUPS_VERIFIED'
    if ($attestation -cne 'BACKUPS_VERIFIED') { throw 'Backup attestation was not provided. No release was applied.' }

    $hubApply = $existingHub.Clone(); $hubApply.Confirm = $false
    Invoke-InstallationStep 'Deploy-HubRelease.ps1' $hubApply 'HUB_RELEASE_DEPLOYED_AND_HEALTHY'
    $completedStages += 'Existing five applications updated'
    Assert-InstallationPortalHidden -RequireEntry

    $moduleApply = $module.Clone(); $moduleApply.Confirm = $false; $moduleApply.BackupsVerified = $true
    Invoke-InstallationStep 'Deploy-SmallBusinessSubcontractingRelease.ps1' $moduleApply `
        'SMALL_BUSINESS_SUBCONTRACTING_RELEASE_DEPLOYED_AND_HEALTHY'
    $completedStages += 'Small Business Subcontracting installed'

    $warm = @{ Scheme = 'https'; PermanentHttps = $true; IncludeSmallBusinessSubcontracting = $true
        HealthTimeoutSeconds = 300; Confirm = $false }
    $warmPreview = $warm.Clone(); $warmPreview.WhatIf = $true
    Invoke-InstallationStep 'Configure-IisWarmStart.ps1' $warmPreview `
        'WHATIF_READY: no IIS features, settings, files, or scheduled tasks were changed.'
    Invoke-InstallationStep 'Configure-IisWarmStart.ps1' $warm 'WARM_START_CONFIGURED_AND_HEALTHY'
    $completedStages += 'Six-application startup recovery configured'
    Assert-InstallationSource -Commit $ExpectedCommit
    Write-Output "Source: $ExpectedCommit; Release: $releaseId; Package: $packageRoot"
    Write-Output 'SMALL_BUSINESS_SUBCONTRACTING_INSTALLED_ACCESS_SETUP_PENDING'
    Write-Output 'Next: assign Small Business Subcontracting permissions in Portal Admin; verify employee access; activate only its Portal card using the runbook.'
}
catch {
    Write-Host "Completed stages: $($completedStages -join '; ')"
    Write-Host 'INSTALLATION_STOPPED: Preserve all output. Do not rerun the full installer; diagnose the failed stage and use its scoped recovery/update procedure.' -ForegroundColor Red
    throw
}
