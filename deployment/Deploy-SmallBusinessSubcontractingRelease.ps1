<#
  Scoped first installation and immutable updates on SON-IIS2. Does not change Portal,
  existing Hub sites, databases, document files, firewall, DNS, or external integrations.
  Startup migrations cannot be undone by IIS rollback. Retain backups and all journals.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High', DefaultParameterSetName='Release')]
param(
    [Parameter(Mandatory=$true, ParameterSetName='Release')][string]$PackageRoot,
    [Parameter(Mandatory=$true, ParameterSetName='Release')]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$ReleaseId,
    [Parameter(Mandatory=$true, ParameterSetName='Release')][string]$CertificateThumbprint,
    [Parameter(ParameterSetName='Release')][string]$ApprovedDocumentRoot = '\\SON-SQL2\SmallBusinessSubcontracting$\Documents',
    [Parameter(ParameterSetName='Release')][switch]$FirstInstall,
    [Parameter(ParameterSetName='Release')][string]$ProductionSettingsPath,
    [Parameter(ParameterSetName='Release')][switch]$BackupsVerified,
    [Parameter(Mandatory=$true, ParameterSetName='Recovery')]
    [ValidatePattern('^[a-f0-9]{32}$')][string]$RecoverTransactionId,
    [ValidateRange(30,600)][int]$HealthTimeoutSeconds=180
)
$ErrorActionPreference = 'Stop'
$stateDirectory = 'C:\ProgramData\SonAero\deployment-state\small-business-subcontracting'
$releaseRoot = 'C:\SonAero\releases\small-business-subcontracting'
$lockPath = Join-Path $stateDirectory 'deployment.lock'
$application = [pscustomobject]@{Site='SmallBusinessSubcontracting'; HostName='subcontracting.hub.son4l.local'; HttpPort=5180}
foreach ($module in @('HubProductionHttps.Common','SubcontractingProductionConfiguration',
    'SubcontractingReleaseFiles','SubcontractingReleaseIis','SubcontractingReleaseTransaction')) {
    Import-Module (Join-Path $PSScriptRoot "$module.psm1") -Force -ErrorAction Stop
}
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1 -or -not [Environment]::Is64BitProcess) {
    throw 'Run in 64-bit Windows PowerShell 5.1.'
}
Assert-HubComputerName -ExpectedComputerName SON-IIS2
Assert-HubAdministrator
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ($identity.IsSystem -or $identity.Name -notlike 'SON4L\*' -or -not [Environment]::UserInteractive) {
    throw 'Run interactively as an elevated authorized SON4L domain user; Local System is prohibited.'
}
Import-HubIisAdministration
$priorTransaction = Read-SubcontractingTransaction $stateDirectory

if ($PSCmdlet.ParameterSetName -eq 'Recovery') {
    if (-not $priorTransaction -or $priorTransaction.Id -cne $RecoverTransactionId -or
        $priorTransaction.Phase -in @('Healthy','RolledBack')) { throw 'No matching interrupted transaction is available for recovery.' }
    $null = Get-SubcontractingIisBoundary -Thumbprint $priorTransaction.Thumbprint -AllowAbsent -SkipEnvironment
    if (-not $PSCmdlet.ShouldProcess($RecoverTransactionId, 'Recover only owned Subcontracting IIS resources')) {
        if ($WhatIfPreference) { Write-Output 'WHATIF_READY_SMALL_BUSINESS_SUBCONTRACTING_RECOVERY' }
        return
    }
    $null = Get-SubcontractingFullPath $lockPath
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $state = Read-SubcontractingTransaction $stateDirectory
        if ($state.Id -cne $RecoverTransactionId -or $state.Phase -in @('Healthy','RolledBack')) { throw 'Transaction changed before recovery lock.' }
        Restore-SubcontractingTransaction -State $state -HealthTimeoutSeconds $HealthTimeoutSeconds
        $state.Phase='RolledBack'
        Write-SubcontractingTransaction $stateDirectory $state
        Write-Output 'SMALL_BUSINESS_SUBCONTRACTING_TRANSACTION_RECOVERED'
    }
    finally { $lock.Dispose() }
    return
}
if ($priorTransaction -and $priorTransaction.Phase -notin @('Healthy','RolledBack')) {
    throw "Interrupted transaction $($priorTransaction.Id) blocks deployment. Review evidence, then preview -RecoverTransactionId before recovery."
}
$thumbprint = ConvertTo-HubThumbprint $CertificateThumbprint
$null = Assert-HubProductionCertificate -Thumbprint $thumbprint -Applications @($application)
Assert-HubProductionDns -Applications @($application) -ExpectedServerAddress '10.50.10.244'
$bindings = @(Get-HubIisBindingSnapshot)
Assert-SubcontractingBindingConflicts -Bindings $bindings -Thumbprint $thumbprint -FirstInstall ([bool]$FirstInstall)
$prior = Get-SubcontractingIisBoundary -Thumbprint $thumbprint -AllowAbsent
if ($FirstInstall) {
    if ($prior) { throw '-FirstInstall requires both the dedicated site and pool to be absent.' }
    if (-not $ProductionSettingsPath) { throw '-FirstInstall requires -ProductionSettingsPath.' }
}
else {
    if (-not $prior) { throw 'Missing site/pool; use the reviewed -FirstInstall procedure.' }
    if ($ProductionSettingsPath) { throw 'Updates preserve the active Production configuration; do not supply ProductionSettingsPath.' }
    if ($prior.SiteState -ne 'Started' -or $prior.PoolState -ne 'Started') { throw 'Update requires the existing site and pool to be started.' }
    $ProductionSettingsPath = Join-Path $prior.Path 'appsettings.Production.json'
    Assert-SubcontractingWebConfig (Join-Path $prior.Path 'web.config')
    Wait-SubcontractingHealth -TimeoutSeconds $HealthTimeoutSeconds
}
$productionPath = Get-SubcontractingFullPath $ProductionSettingsPath
$configuration = Read-SubcontractingProductionConfiguration -Path $productionPath -ApprovedDocumentRoot $ApprovedDocumentRoot
$configurationHash = (Get-FileHash -LiteralPath $productionPath -Algorithm SHA256).Hash
$source = Get-SubcontractingFullPath (Join-Path $PackageRoot 'SmallBusinessSubcontracting')
$candidate = Get-SubcontractingFullPath (Join-Path $releaseRoot $ReleaseId)
if ((Test-SubcontractingPathOverlap $source $releaseRoot) -or
    (Test-SubcontractingPathOverlap $productionPath $candidate) -or (Test-Path -LiteralPath $candidate)) {
    throw 'Package/configuration overlaps release target or immutable release directory already exists; use a new ReleaseId.'
}
Assert-SubcontractingCandidateIsolated -Candidate $candidate -Source $source
$manifest = @(Get-SubcontractingPackageManifest $source)
Assert-SubcontractingWebConfig (Join-Path $source 'web.config')
if (-not (Get-WebGlobalModule | Where-Object Name -EQ 'AspNetCoreModuleV2')) { throw 'ASP.NET Core Hosting Bundle is missing.' }
$runtimes = @(& dotnet --list-runtimes)
if ($LASTEXITCODE -ne 0 -or -not ($runtimes -match '^Microsoft.AspNetCore.App 8\.')) { throw 'ASP.NET Core 8 runtime is missing.' }
if (-not (Test-NetConnection -ComputerName SON-SQL2 -Port 1433 -InformationLevel Quiet -WarningAction SilentlyContinue)) {
    throw 'SON-SQL2 TCP1433 is unreachable.'
}
if (-not (Test-Path -LiteralPath $ApprovedDocumentRoot -PathType Container)) { throw 'Approved UNC document directory does not exist or is inaccessible.' }
$storageItem = Get-Item -LiteralPath $ApprovedDocumentRoot -Force
if (($storageItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Document storage cannot be a reparse point.' }
if ($FirstInstall -and @(Get-NetTCPConnection -LocalPort 5180 -State Listen -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'TCP5180 is already listening; inspect the owning service before installation.'
}
# Reserve an unused numeric ID in the journal so interrupted recovery can distinguish ownership.
$manager = New-Object Microsoft.Web.Administration.ServerManager
try { $siteId = if ($prior) { $prior.SiteId } else { [long](($manager.Sites | Measure-Object Id -Maximum).Maximum) + 1 } }
finally { $manager.Dispose() }
Write-Output "Preflight verified: $source -> $candidate; Windows/SQL/UNC; SNI443 and internal HTTP5180."
if (-not $PSCmdlet.ShouldProcess($candidate, 'Install immutable Small Business Subcontracting release')) {
    if ($WhatIfPreference) { Write-Output 'WHATIF_READY_SMALL_BUSINESS_SUBCONTRACTING_RELEASE' }
    return
}
if (-not $BackupsVerified) { throw 'Apply requires -BackupsVerified after actual SQL/document backups and restore evidence have been reviewed.' }
Initialize-SubcontractingStateDirectory $stateDirectory
$null = Get-SubcontractingFullPath $lockPath
$lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $latest = Read-SubcontractingTransaction $stateDirectory
    if ($latest -and $latest.Phase -notin @('Healthy','RolledBack')) { throw 'Another transaction has started; deployment stopped.' }
    $current = Get-SubcontractingIisBoundary -Thumbprint $thumbprint -AllowAbsent
    if (($current | ConvertTo-Json -Compress) -cne ($prior | ConvertTo-Json -Compress)) { throw 'IIS state changed after preflight.' }
    Assert-SubcontractingBindingConflicts -Bindings @(Get-HubIisBindingSnapshot) -Thumbprint $thumbprint -FirstInstall ([bool]$FirstInstall)
    if ($latest) { Copy-Item -LiteralPath (Join-Path $stateDirectory 'transaction.json') -Destination (Join-Path $stateDirectory "$($latest.Id).json") -ErrorAction Stop }
    $state = [pscustomobject]@{ Version=1; Id=[guid]::NewGuid().ToString('N'); ReleaseId=$ReleaseId; FirstInstall=[bool]$FirstInstall;
        Candidate=$candidate; Prior=$prior; SiteId=$siteId; Thumbprint=$thumbprint; ConfigurationHash=$configurationHash;
        Phase='Preparing'; Operator=$identity.Name; StartedUtc=[DateTime]::UtcNow.ToString('o') }
    $actions = @{
        Persist = { param($s) Write-SubcontractingTransaction $stateDirectory $s }
        Prepare = {
            param($s)
            if (Test-Path -LiteralPath $s.Candidate) { throw 'Candidate release directory already exists.' }
            $null = Get-SubcontractingFullPath $s.Candidate
            $null = [IO.Directory]::CreateDirectory($s.Candidate, (New-SubcontractingDirectorySecurity))
            Copy-SubcontractingPackage -Source $source -Destination $s.Candidate -Manifest $manifest
            $targetConfiguration = Join-Path $s.Candidate 'appsettings.Production.json'
            Copy-Item -LiteralPath $productionPath -Destination $targetConfiguration
            if ((Get-FileHash -LiteralPath $targetConfiguration -Algorithm SHA256).Hash -cne $s.ConfigurationHash -or
                (Get-FileHash -LiteralPath $productionPath -Algorithm SHA256).Hash -cne $s.ConfigurationHash) { throw 'Production configuration changed during preparation.' }
            $null = Read-SubcontractingProductionConfiguration $targetConfiguration -ApprovedDocumentRoot $ApprovedDocumentRoot
            Assert-SubcontractingWebConfig (Join-Path $s.Candidate 'web.config')
        }
        Switch = {
            param($s)
            if ($s.FirstInstall) { New-SubcontractingIisSite -Candidate $s.Candidate -Thumbprint $s.Thumbprint -SiteId $s.SiteId }
            else {
                Set-SubcontractingRuntimeState -State Stopped
                Set-SubcontractingPhysicalPath -Expected $s.Prior.Path -Destination $s.Candidate
            }
            Set-Acl -LiteralPath $s.Candidate -AclObject (New-SubcontractingDirectorySecurity -ReadPrincipal 'IIS AppPool\SmallBusinessSubcontracting')
            Set-SubcontractingRuntimeState -State Started
        }
        Health = { param($s) Wait-SubcontractingHealth -TimeoutSeconds $HealthTimeoutSeconds }
        Verify = {
            param($s)
            $live = Get-SubcontractingIisBoundary -Thumbprint $s.Thumbprint
            if ($live.Path -ine $s.Candidate -or $live.SiteId -ne $s.SiteId) { throw 'Final IIS ownership verification failed.' }
            if ($s.FirstInstall) { Enable-SubcontractingAutoStart }
        }
        Restore = { param($s) Restore-SubcontractingTransaction -State $s -HealthTimeoutSeconds $HealthTimeoutSeconds }
    }
    Invoke-SubcontractingReleaseTransaction -State $state -Actions $actions
    Write-Output "Transaction $($state.Id); active release $candidate."
    Write-Output 'SMALL_BUSINESS_SUBCONTRACTING_RELEASE_DEPLOYED_AND_HEALTHY'
}
finally { $lock.Dispose() }
