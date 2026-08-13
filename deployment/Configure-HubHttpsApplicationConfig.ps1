<#
    Atomic production configuration transaction for HTTPS module URLs and dual Hub CORS.

    Preview permanent production topology:
      .\Configure-HubHttpsApplicationConfig.ps1 -Topology Production -WhatIf
    Apply:
      .\Configure-HubHttpsApplicationConfig.ps1 -Topology Production -Confirm:$false
    Roll back the last successful apply:
      .\Configure-HubHttpsApplicationConfig.ps1 -Topology Production -Rollback -Confirm:$false

    Production and Pilot use separate transaction-state files. The production transaction captures
    the currently active retained configuration as its own rollback baseline; it does not trust or
    overwrite a state file written by an older Pilot script.

    The retained two-person pilot must always pass -Topology Pilot explicitly.

    The active IIS production files are backed up with restricted ACLs. Both files are restored
    automatically if replacement, targeted pool restart, CORS, dual-scheme, or gateway health fails.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'Apply')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Rollback')]
    [switch]$Rollback,

    [ValidateRange(30, 600)]
    [int]$HealthTimeoutSeconds = 180,

    [ValidateSet('Production', 'Pilot')]
    [string]$Topology = 'Production',

    [string]$StatePath = ''
)

$ErrorActionPreference = 'Stop'
$expectedComputerName = 'SON-IIS2'
$stateRoot = 'C:\ProgramData\SonAero\deployment-state'
$backupBaseRoot = Join-Path $stateRoot 'https-config-backups'
$pilotStatePath = Join-Path $stateRoot 'https-application-config.json'
$productionStatePath = Join-Path $stateRoot 'https-production-application-config.json'
$portalSiteName = 'SonAeroPortal'
$trackerSiteName = 'ProjectTracker'
$gatewayPath = '/project-tracker-api'
$poolNames = @('ProjectTracker', 'SonAeroPortal', 'ProjectTrackerAdminGateway')
$applications = @(
    [pscustomobject]@{ Id = 'project-tracker'; Site = 'ProjectTracker'; HttpPort = 5135; HttpsPort = 6135; ProductionHost = 'projects.hub.son4l.local' },
    [pscustomobject]@{ Id = 'portal'; Site = 'SonAeroPortal'; HttpPort = 5140; HttpsPort = 6140; ProductionHost = 'hub.son4l.local' },
    [pscustomobject]@{ Id = 'engineering-hub'; Site = 'EngineeringHub'; HttpPort = 5150; HttpsPort = 6150; ProductionHost = 'engineering.hub.son4l.local' },
    [pscustomobject]@{ Id = 'estimating-dashboard'; Site = 'EstimatingDashboard'; HttpPort = 5160; HttpsPort = 6160; ProductionHost = 'estimating.hub.son4l.local' },
    [pscustomobject]@{ Id = 'quality-assurance'; Site = 'QualityAssurance'; HttpPort = 5170; HttpsPort = 6170; ProductionHost = 'quality.hub.son4l.local' }
)
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

function Resolve-TransactionStatePath {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Production', 'Pilot')][string]$SelectedTopology,
        [AllowEmptyString()][string]$SuppliedPath,
        [Parameter(Mandatory = $true)][bool]$WasExplicit
    )
    if ($WasExplicit) {
        if ([string]::IsNullOrWhiteSpace($SuppliedPath)) {
            throw 'StatePath cannot be empty when it is supplied explicitly.'
        }
        return $SuppliedPath
    }
    if ($SelectedTopology -eq 'Production') { return $productionStatePath }
    return $pilotStatePath
}

function Set-TopologyConfiguration {
    param([Parameter(Mandatory = $true)][ValidateSet('Production', 'Pilot')][string]$Name)
    $script:activeTopology = $Name
    if ($Name -eq 'Production') {
        $script:moduleUrls = $productionModuleUrls
        $script:hubOrigins = $productionHubOrigins
    }
    else {
        $script:moduleUrls = $pilotModuleUrls
        $script:hubOrigins = $pilotHubOrigins
    }
}

$Topology = if ($Topology -ieq 'Production') { 'Production' } else { 'Pilot' }
Set-TopologyConfiguration -Name $Topology
$StatePath = Resolve-TransactionStatePath -SelectedTopology $Topology `
    -SuppliedPath $StatePath -WasExplicit ($PSBoundParameters.ContainsKey('StatePath'))

function Assert-Host {
    if ($env:COMPUTERNAME -ine $expectedComputerName) {
        throw "This transaction is restricted to $expectedComputerName; current computer is $env:COMPUTERNAME."
    }
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated Windows PowerShell session.'
    }
}

function Import-IisAdministration {
    $priorWhatIf = $WhatIfPreference
    try {
        $WhatIfPreference = $false
        Import-Module WebAdministration -Global -ErrorAction Stop
    }
    finally { $WhatIfPreference = $priorWhatIf }
    $assemblyPath = Join-Path $env:WINDIR 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "IIS administration assembly was not found at '$assemblyPath'."
    }
    Add-Type -Path $assemblyPath -ErrorAction Stop
}

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path)).TrimEnd('\')
}

function Assert-NoReparsePathChain {
    param([Parameter(Mandatory = $true)][string]$Path)
    $current = Get-FullPath $Path
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

function Get-ActiveSitePath {
    param([Parameter(Mandatory = $true)][string]$SiteName)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$SiteName]
        if (-not $site) { throw "Required IIS site '$SiteName' is missing." }
        $application = $site.Applications['/']
        $virtualDirectory = if ($application) { $application.VirtualDirectories['/'] } else { $null }
        if (-not $virtualDirectory) { throw "IIS site '$SiteName' has no root virtual directory." }
        $path = Get-FullPath $virtualDirectory.PhysicalPath
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Active IIS path does not exist: '$path'." }
        return $path
    }
    finally { $manager.Dispose() }
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required production configuration is missing: '$Path'." }
    try {
        $raw = Get-Content -LiteralPath $Path -Raw
        if ([string]::IsNullOrWhiteSpace($raw)) { throw 'The file is empty.' }
        return $raw | ConvertFrom-Json
    }
    catch { throw "Invalid JSON at '$Path': $($_.Exception.Message)" }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($Path)))).Replace('-', '') }
    finally { $hasher.Dispose() }
}

function Get-BytesSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hasher.ComputeHash($Bytes))).Replace('-', '') }
    finally { $hasher.Dispose() }
}

function Set-StateProperty {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$Name,
        $Value
    )
    if ($State.PSObject.Properties.Name -contains $Name) { $State.$Name = $Value }
    else { $State | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}

function Assert-RequiredStateProperties {
    param([Parameter(Mandatory = $true)]$State, [Parameter(Mandatory = $true)][string[]]$Names)
    foreach ($name in $Names) {
        if ($State.PSObject.Properties.Name -notcontains $name -or $null -eq $State.$name) {
            throw "Transaction state is missing required property '$name'."
        }
        if ($State.$name -is [string] -and [string]::IsNullOrWhiteSpace([string]$State.$name)) {
            throw "Transaction state property '$name' is empty."
        }
    }
}

function Assert-SafeStatePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullRoot = Get-FullPath $stateRoot
    $fullPath = Get-FullPath $Path
    $commonApplicationData = Get-FullPath ([Environment]::GetFolderPath(
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

function Assert-PathUnderRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $fullPath = Get-FullPath $Path
    $fullRoot = Get-FullPath $Root
    if (-not $fullPath.StartsWith($fullRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must remain under '$fullRoot'."
    }
    return $fullPath
}

function Convert-ToUtf8JsonBytes {
    param([Parameter(Mandatory = $true)]$Value)
    $json = $Value | ConvertTo-Json -Depth 100
    return (New-Object Text.UTF8Encoding($false)).GetBytes($json + [Environment]::NewLine)
}

function Assert-PortalShape {
    param([Parameter(Mandatory = $true)]$Config)
    if (-not $Config.Portal -or -not $Config.Portal.Applications) { throw 'Portal configuration has no Portal.Applications catalog.' }
    foreach ($id in $moduleUrls.Keys) {
        $matches = @($Config.Portal.Applications | Where-Object { $_.Id -eq $id })
        if ($matches.Count -ne 1) { throw "Portal.Applications must contain exactly one '$id' entry; found $($matches.Count)." }
        if ($matches[0].PSObject.Properties.Name -notcontains 'Url') { throw "Portal application '$id' has no Url property." }
    }
}

function Assert-TrackerShape {
    param([Parameter(Mandatory = $true)]$Config)
    if (-not $Config.Cors -or $Config.Cors.PSObject.Properties.Name -notcontains 'HubOrigins') {
        throw 'Project Tracker configuration has no Cors.HubOrigins property.'
    }
}

function New-TransformedConfig {
    param(
        [Parameter(Mandatory = $true)]$PortalConfig,
        [Parameter(Mandatory = $true)]$TrackerConfig,
        [hashtable]$TargetModuleUrls = $moduleUrls,
        [string[]]$TargetHubOrigins = $hubOrigins
    )
    Assert-PortalShape $PortalConfig
    Assert-TrackerShape $TrackerConfig
    foreach ($id in $TargetModuleUrls.Keys) {
        $entry = @($PortalConfig.Portal.Applications | Where-Object { $_.Id -eq $id })[0]
        $entry.Url = $TargetModuleUrls[$id]
    }
    $TrackerConfig.Cors.HubOrigins = @($TargetHubOrigins)
    Assert-PortalShape $PortalConfig
    Assert-TrackerShape $TrackerConfig
    foreach ($id in $TargetModuleUrls.Keys) {
        $entry = @($PortalConfig.Portal.Applications | Where-Object { $_.Id -eq $id })[0]
        if ($entry.Url -ne $TargetModuleUrls[$id]) { throw "Portal URL transform failed for '$id'." }
    }
    if ((@($TrackerConfig.Cors.HubOrigins) -join '|') -ne ($TargetHubOrigins -join '|')) {
        throw 'Project Tracker retained-origin CORS transform failed or permanent HTTPS is not first.'
    }
    return [pscustomobject]@{ Portal = $PortalConfig; Tracker = $TrackerConfig }
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

function New-SecureDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    Assert-NoReparsePathChain -Path $Path
    $security = New-ProtectedFileSystemSecurity -Directory
    if (-not (Test-Path -LiteralPath $Path)) { [void][IO.Directory]::CreateDirectory($Path, $security) }
    Assert-NoReparsePathChain -Path $Path
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Protected directory '$Path' is missing, is not a directory, or is a reparse point."
    }
    Set-Acl -LiteralPath $Path -AclObject $security
    Assert-ProtectedPath -Path $Path -Directory
}

function Assert-StatePathProtection {
    Assert-NoReparsePathChain -Path $StatePath
    Assert-ProtectedPath -Path $stateRoot -Directory
    Assert-ProtectedPath -Path $StatePath
}

function Read-SecureState {
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { throw "Transaction state was not found at '$StatePath'." }
    Assert-StatePathProtection
    try { return Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json }
    catch { throw "Transaction state at '$StatePath' is not valid JSON: $($_.Exception.Message)" }
}

function Write-SecureState {
    param([Parameter(Mandatory = $true)]$State)
    $directory = Split-Path -Parent $StatePath
    Assert-NoReparsePathChain -Path $StatePath
    New-SecureDirectory $directory
    $previousStateSha256 = $null
    if (Test-Path -LiteralPath $StatePath) {
        Assert-StatePathProtection
        $previousStateSha256 = Get-FileSha256 $StatePath
    }
    $temporary = Join-Path $directory ((Split-Path -Leaf $StatePath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $replacementBackup = Join-Path $directory ((Split-Path -Leaf $StatePath) + '.' + [Guid]::NewGuid().ToString('N') + '.replace-backup')
    $replacementVerified = $false
    $temporarySha256 = $null
    try {
        $json = ($State | ConvertTo-Json -Depth 12) + [Environment]::NewLine
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json)
        $expectedStateSha256 = Get-BytesSha256 $bytes
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
        $temporarySha256 = Get-FileSha256 $temporary
        if ($temporarySha256 -ine $expectedStateSha256) {
            throw "Temporary transaction state hash verification failed at '$temporary'."
        }
        if ($null -ne $previousStateSha256) {
            # .NET Framework requires a non-null backupFileName. Keeping the unique backup beside
            # the protected destination also preserves File.Replace's atomic/crash-safe semantics.
            if ((Get-FullPath (Split-Path -Parent $replacementBackup)) -ine (Get-FullPath $directory)) {
                throw "Replacement backup must be a direct sibling of the transaction state: '$replacementBackup'."
            }
            Assert-NoReparsePathChain -Path $replacementBackup
            if (Test-Path -LiteralPath $replacementBackup) {
                throw "Replacement backup path already exists: '$replacementBackup'."
            }
            [IO.File]::Replace($temporary, $StatePath, $replacementBackup)
            try {
                Assert-NoReparsePathChain -Path $replacementBackup
                Assert-ProtectedPath -Path $replacementBackup
                if ((Get-FileSha256 $replacementBackup) -ine $previousStateSha256) {
                    throw 'Replacement backup hash does not match the prior transaction state.'
                }
                Assert-StatePathProtection
                if ((Get-FileSha256 $StatePath) -ine $temporarySha256) {
                    throw 'Installed transaction state hash does not match the protected temporary state.'
                }
                $replacementVerified = $true
            }
            catch {
                throw "Atomic state replacement completed but verification failed. The prior state backup is preserved at '$replacementBackup'. $($_.Exception.Message)"
            }
        }
        else {
            Move-Item -LiteralPath $temporary -Destination $StatePath
            Assert-StatePathProtection
            if ((Get-FileSha256 $StatePath) -ine $temporarySha256) {
                throw 'Installed transaction state hash does not match the protected temporary state.'
            }
        }
    }
    finally {
        if ((Get-FullPath (Split-Path -Parent $temporary)) -ieq (Get-FullPath $directory)) {
            try {
                Assert-NoReparsePathChain -Path $temporary
                $temporaryItem = Get-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
                if ($temporaryItem -and -not $temporaryItem.PSIsContainer -and
                    ($temporaryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
                    Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
                }
            }
            catch { }
        }
        $replacementBackupExists = $false
        try { $replacementBackupExists = Test-Path -LiteralPath $replacementBackup -PathType Leaf -ErrorAction Stop }
        catch {
            Write-Warning -WarningAction Continue `
                "The replacement backup could not be safely inspected and may remain at '$replacementBackup': $($_.Exception.Message)"
        }
        if ($replacementBackupExists) {
            if (-not $replacementVerified) {
                Write-Warning -WarningAction Continue `
                    "State replacement did not reach verified completion. The prior state backup is preserved at '$replacementBackup'."
            }
            else {
                try {
                    if ((Get-FullPath (Split-Path -Parent $replacementBackup)) -ine (Get-FullPath $directory)) {
                        throw 'Replacement backup is no longer a direct sibling of the transaction state.'
                    }
                    Assert-NoReparsePathChain -Path $replacementBackup
                    Assert-ProtectedPath -Path $replacementBackup
                    if ((Get-FileSha256 $replacementBackup) -ine $previousStateSha256) {
                        throw 'Replacement backup hash changed before cleanup.'
                    }
                    Assert-StatePathProtection
                    if ((Get-FileSha256 $StatePath) -ine $temporarySha256) {
                        throw 'Installed transaction state hash changed before backup cleanup.'
                    }
                    Remove-Item -LiteralPath $replacementBackup -Force -ErrorAction Stop
                }
                catch {
                    Write-Warning -WarningAction Continue `
                        "The transaction state commit is verified, but its protected replacement backup could not be safely removed and remains at '$replacementBackup': $($_.Exception.Message)"
                }
            }
        }
    }
}

function Try-WriteSecureState {
    param([Parameter(Mandatory = $true)]$State)
    try {
        Write-SecureState $State
        return $null
    }
    catch { return $_.Exception.Message }
}

function Assert-TransactionState {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$ExpectedPortalConfigPath,
        [Parameter(Mandatory = $true)][string]$ExpectedTrackerConfigPath
    )
    $required = @(
        'Version', 'ComputerName', 'Status', 'PortalConfigPath', 'TrackerConfigPath',
        'PortalBackupPath', 'TrackerBackupPath', 'PortalOriginalSha256', 'TrackerOriginalSha256'
    )
    Assert-RequiredStateProperties -State $State -Names $required
    foreach ($name in @('PortalAppliedSha256', 'TrackerAppliedSha256')) {
        if ($State.PSObject.Properties.Name -notcontains $name -or $null -eq $State.$name) {
            throw "Transaction state is missing required property '$name'."
        }
    }
    $version = [int]$State.Version
    if ($version -notin @(1, 2, 3) -or $State.ComputerName -ine $expectedComputerName) {
        throw 'Transaction state identity or version does not match this script.'
    }
    if ($State.PSObject.Properties.Name -notcontains 'Topology') {
        if ($version -eq 3) { throw "Transaction state is missing required property 'Topology'." }
        Set-StateProperty -State $State -Name 'Topology' -Value 'Pilot'
    }
    if ([string]$State.Topology -notin @('Production', 'Pilot')) {
        throw "Transaction state has an invalid Topology value '$($State.Topology)'."
    }
    Set-StateProperty -State $State -Name 'Topology' -Value $(
        if ([string]$State.Topology -ieq 'Production') { 'Production' } else { 'Pilot' }
    )
    $stateModuleUrls = if ([string]$State.Topology -eq 'Production') { $productionModuleUrls } else { $pilotModuleUrls }
    $stateHubOrigins = if ([string]$State.Topology -eq 'Production') { $productionHubOrigins } else { $pilotHubOrigins }
    if ((Get-FullPath $State.PortalConfigPath) -ine (Get-FullPath $ExpectedPortalConfigPath) -or
        (Get-FullPath $State.TrackerConfigPath) -ine (Get-FullPath $ExpectedTrackerConfigPath)) {
        throw 'Transaction state active configuration paths do not match the current IIS sites.'
    }
    $portalBackupPath = Assert-PathUnderRoot -Path $State.PortalBackupPath -Root $backupBaseRoot -Label 'Portal backup path'
    $trackerBackupPath = Assert-PathUnderRoot -Path $State.TrackerBackupPath -Root $backupBaseRoot -Label 'Project Tracker backup path'
    if ((Split-Path -Parent $portalBackupPath) -ine (Split-Path -Parent $trackerBackupPath)) {
        throw 'Transaction backup files do not share the same secured transaction directory.'
    }
    foreach ($name in @('PortalOriginalSha256', 'TrackerOriginalSha256')) {
        if ([string]$State.$name -notmatch '^[A-Fa-f0-9]{64}$') { throw "Transaction state has an invalid $name value." }
    }
    Assert-TransactionBackups $State
    if ($version -eq 1) {
        $portalOriginal = Read-JsonFile $State.PortalBackupPath
        $trackerOriginal = Read-JsonFile $State.TrackerBackupPath
        $transformed = New-TransformedConfig `
            -PortalConfig $portalOriginal `
            -TrackerConfig $trackerOriginal `
            -TargetModuleUrls $stateModuleUrls `
            -TargetHubOrigins $stateHubOrigins
        $portalPlannedSha256 = Get-BytesSha256 (Convert-ToUtf8JsonBytes $transformed.Portal)
        $trackerPlannedSha256 = Get-BytesSha256 (Convert-ToUtf8JsonBytes $transformed.Tracker)
        foreach ($pair in @(
            [pscustomobject]@{ Name = 'PortalAppliedSha256'; Planned = $portalPlannedSha256 },
            [pscustomobject]@{ Name = 'TrackerAppliedSha256'; Planned = $trackerPlannedSha256 }
        )) {
            $applied = [string]$State.($pair.Name)
            if (-not [string]::IsNullOrWhiteSpace($applied) -and $applied -ne $pair.Planned) {
                throw "Legacy transaction state $($pair.Name) does not match the safely reconstructed transform."
            }
        }
        Set-StateProperty -State $State -Name 'PortalPlannedSha256' -Value $portalPlannedSha256
        Set-StateProperty -State $State -Name 'TrackerPlannedSha256' -Value $trackerPlannedSha256
        Set-StateProperty -State $State -Name 'Version' -Value 3
    }
    else {
        Assert-RequiredStateProperties -State $State -Names @('PortalPlannedSha256', 'TrackerPlannedSha256')
        if ($version -eq 2) { Set-StateProperty -State $State -Name 'Version' -Value 3 }
    }
    foreach ($name in @('PortalPlannedSha256', 'TrackerPlannedSha256')) {
        if ([string]$State.$name -notmatch '^[A-Fa-f0-9]{64}$') { throw "Transaction state has an invalid $name value." }
    }
    if ($State.Status -eq 'Applied' -and
        ($State.PortalAppliedSha256 -ne $State.PortalPlannedSha256 -or
         $State.TrackerAppliedSha256 -ne $State.TrackerPlannedSha256)) {
        throw 'Applied transaction state hashes do not match the planned transform.'
    }
    foreach ($name in @(
        'AppliedAtUtc', 'RolledBackAtUtc', 'ApplyFailure', 'ApplyFailedAtUtc',
        'RollbackStartedAtUtc', 'RollbackFailure', 'RollbackFailedAtUtc'
    )) {
        if ($State.PSObject.Properties.Name -notcontains $name) { Set-StateProperty -State $State -Name $name -Value $null }
    }
}

function Enter-HttpsApplicationConfigTransactionLock {
    $mutex = New-Object Threading.Mutex($false, 'Global\SonAero-HubHttpsApplicationConfig')
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) {
            throw 'Another Hub HTTPS application-configuration transaction is already running on SON-IIS2.'
        }
        return $mutex
    }
    catch {
        if (-not $acquired) { $mutex.Dispose() }
        throw
    }
}

function Assert-RequestedStateTopology {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][ValidateSet('Production', 'Pilot')][string]$RequestedTopology
    )
    if ([string]$State.Topology -ine $RequestedTopology) {
        throw "Transaction state topology '$($State.Topology)' does not match requested topology '$RequestedTopology'."
    }
}

function Assert-TransactionBackups {
    param([Parameter(Mandatory = $true)]$State)
    if (-not (Test-Path -LiteralPath $State.PortalBackupPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $State.TrackerBackupPath -PathType Leaf)) {
        throw 'One or more secured transaction backups are missing.'
    }
    $null = Read-JsonFile $State.PortalBackupPath
    $null = Read-JsonFile $State.TrackerBackupPath
    if ((Get-FileSha256 $State.PortalBackupPath) -ne $State.PortalOriginalSha256 -or
        (Get-FileSha256 $State.TrackerBackupPath) -ne $State.TrackerOriginalSha256) {
        throw 'Secured transaction backup hashes do not match the saved originals.'
    }
}

function New-VerifiedTransactionSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$PortalConfigPath,
        [Parameter(Mandatory = $true)][string]$TrackerConfigPath,
        [Parameter(Mandatory = $true)][string]$PortalBackupPath,
        [Parameter(Mandatory = $true)][string]$TrackerBackupPath
    )
    $portalBeforeSha256 = Get-FileSha256 $PortalConfigPath
    $trackerBeforeSha256 = Get-FileSha256 $TrackerConfigPath
    Copy-Item -LiteralPath $PortalConfigPath -Destination $PortalBackupPath
    Copy-Item -LiteralPath $TrackerConfigPath -Destination $TrackerBackupPath
    $portalBackupSha256 = Get-FileSha256 $PortalBackupPath
    $trackerBackupSha256 = Get-FileSha256 $TrackerBackupPath
    $portalAfterSha256 = Get-FileSha256 $PortalConfigPath
    $trackerAfterSha256 = Get-FileSha256 $TrackerConfigPath
    if ($portalBeforeSha256 -ne $portalBackupSha256 -or $portalAfterSha256 -ne $portalBackupSha256 -or
        $trackerBeforeSha256 -ne $trackerBackupSha256 -or $trackerAfterSha256 -ne $trackerBackupSha256) {
        throw 'Production configuration changed while the secured backup snapshot was captured; transaction refused.'
    }

    $portalBackupConfig = Read-JsonFile $PortalBackupPath
    $trackerBackupConfig = Read-JsonFile $TrackerBackupPath
    $transformed = New-TransformedConfig -PortalConfig $portalBackupConfig -TrackerConfig $trackerBackupConfig
    $portalPlannedBytes = Convert-ToUtf8JsonBytes $transformed.Portal
    $trackerPlannedBytes = Convert-ToUtf8JsonBytes $transformed.Tracker
    return [pscustomobject]@{
        PortalOriginalSha256 = $portalBackupSha256
        TrackerOriginalSha256 = $trackerBackupSha256
        PortalPlannedBytes = $portalPlannedBytes
        TrackerPlannedBytes = $trackerPlannedBytes
        PortalPlannedSha256 = Get-BytesSha256 $portalPlannedBytes
        TrackerPlannedSha256 = Get-BytesSha256 $trackerPlannedBytes
    }
}

function Assert-RecoverableActiveConfiguration {
    param([Parameter(Mandatory = $true)]$State)
    $portalHash = Get-FileSha256 $State.PortalConfigPath
    $trackerHash = Get-FileSha256 $State.TrackerConfigPath
    if ($portalHash -notin @($State.PortalOriginalSha256, $State.PortalPlannedSha256) -or
        $trackerHash -notin @($State.TrackerOriginalSha256, $State.TrackerPlannedSha256)) {
        throw 'Active production configuration contains unrelated drift; recovery refused.'
    }
}

function Assert-AppliedConfiguration {
    param([Parameter(Mandatory = $true)]$State)
    if ([string]$State.Status -cne 'Applied' -or
        (Get-FileSha256 $State.PortalConfigPath) -cne [string]$State.PortalPlannedSha256 -or
        (Get-FileSha256 $State.TrackerConfigPath) -cne [string]$State.TrackerPlannedSha256) {
        throw 'Applied transaction state does not exactly match the current active production configuration.'
    }
}

function Assert-OriginalConfiguration {
    param([Parameter(Mandatory = $true)]$State)
    if ((Get-FileSha256 $State.PortalConfigPath) -ne $State.PortalOriginalSha256 -or
        (Get-FileSha256 $State.TrackerConfigPath) -ne $State.TrackerOriginalSha256) {
        throw 'Active production configuration does not match the saved originals.'
    }
}

function Assert-PoolsPresentAndStarted {
    foreach ($poolName in $poolNames) {
        if (-not (Test-Path -LiteralPath "IIS:\AppPools\$poolName")) { throw "Required IIS application pool '$poolName' is missing." }
        $state = (Get-WebAppPoolState -Name $poolName).Value
        if ($state -ne 'Started') { throw "IIS application pool '$poolName' must be Started before this transaction; current state is '$state'." }
    }
}

function Assert-PoolsPresent {
    foreach ($poolName in $poolNames) {
        if (-not (Test-Path -LiteralPath "IIS:\AppPools\$poolName")) { throw "Required IIS application pool '$poolName' is missing." }
    }
}

function Wait-PoolState {
    param([ValidateSet('Started', 'Stopped')][string]$State)
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        $pending = @($poolNames | Where-Object { (Get-WebAppPoolState -Name $_).Value -ne $State })
        if ($pending.Count -eq 0) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Application pools did not reach '$State': $($pending -join ', ')."
}

function Stop-TargetPools {
    foreach ($poolName in $poolNames) {
        if ((Get-WebAppPoolState -Name $poolName).Value -ne 'Stopped') { Stop-WebAppPool -Name $poolName }
    }
    Wait-PoolState Stopped
}

function Start-TargetPools {
    foreach ($poolName in $poolNames) {
        if ((Get-WebAppPoolState -Name $poolName).Value -ne 'Started') { Start-WebAppPool -Name $poolName }
    }
    Wait-PoolState Started
}

function Invoke-WithPoolsStopped {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Precondition,
        [Parameter(Mandatory = $true)][scriptblock]$Operation,
        [Parameter(Mandatory = $true)][scriptblock]$RecoveryOperation,
        [switch]$RecoveryCompletesOperation
    )
    $stopError = $null
    try { Stop-TargetPools }
    catch { $stopError = $_ }
    if ($stopError) {
        $restartError = $null
        try { Start-TargetPools }
        catch { $restartError = $_ }
        if ($restartError) {
            throw "Pool stop failed: $($stopError.Exception.Message) Pool recovery also failed: $($restartError.Exception.Message)"
        }
        throw $stopError
    }

    $preconditionError = $null
    try { & $Precondition }
    catch { $preconditionError = $_ }
    if ($preconditionError) {
        $restartError = $null
        try { Start-TargetPools }
        catch { $restartError = $_ }
        if ($restartError) {
            throw "Stopped-pool precondition failed without changing files: $($preconditionError.Exception.Message) Pool recovery also failed: $($restartError.Exception.Message)"
        }
        throw $preconditionError
    }

    $operationError = $null
    try {
        & $Operation
    }
    catch {
        $operationError = $_
        try { & $RecoveryOperation }
        catch {
            throw "The stopped-pool operation failed: $($operationError.Exception.Message) Consistency recovery also failed: $($_.Exception.Message) The affected pools remain stopped."
        }
        if ($RecoveryCompletesOperation) { $operationError = $null }
    }
    $restartError = $null
    try { Start-TargetPools }
    catch { $restartError = $_ }
    if ($operationError -and $restartError) {
        throw "The stopped-pool operation failed but original configuration consistency was restored: $($operationError.Exception.Message) Pool recovery then failed: $($restartError.Exception.Message)"
    }
    if ($operationError) { throw $operationError }
    if ($restartError) { throw $restartError }
}

function Wait-UriHealth {
    param([Parameter(Mandatory = $true)][string[]]$Uris)
    $pending = @($Uris)
    $lastErrors = @{}
    $deadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
    do {
        foreach ($uri in @($pending)) {
            try {
                $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec 10
                if ($response.StatusCode -eq 200) {
                    $pending = @($pending | Where-Object { $_ -ne $uri })
                    $null = $lastErrors.Remove($uri)
                }
                else { $lastErrors[$uri] = "HTTP $($response.StatusCode)" }
            }
            catch { $lastErrors[$uri] = $_.Exception.Message }
        }
        if ($pending.Count -gt 0) { Start-Sleep -Milliseconds 750 }
    } while ($pending.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($pending.Count -gt 0) {
        $details = @($pending | ForEach-Object { "$_ => $($lastErrors[$_])" }) -join '; '
        throw "Health verification timed out. Last results: $details"
    }
}

function Get-DualSchemeHealthUris {
    $uris = @(Get-RetainedHealthUris)
    if ($activeTopology -ne 'Production') { return $uris }
    foreach ($application in $applications) {
        $uris += "https://$($application.ProductionHost)/api/health"
    }
    $uris += "https://hub.son4l.local$gatewayPath/api/health"
    return $uris
}

function Get-TrackerAuthenticationState {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $configuration = $manager.GetApplicationHostConfiguration()
        return [pscustomobject]@{
            AnonymousEnabled = [bool]$configuration.GetSection(
                'system.webServer/security/authentication/anonymousAuthentication',
                $trackerSiteName).GetAttributeValue('enabled')
            WindowsEnabled = [bool]$configuration.GetSection(
                'system.webServer/security/authentication/windowsAuthentication',
                $trackerSiteName).GetAttributeValue('enabled')
        }
    }
    finally { $manager.Dispose() }
}

function Assert-TrackerAuthenticationState {
    param(
        [Parameter(Mandatory = $true)][bool]$AnonymousEnabled,
        [Parameter(Mandatory = $true)][bool]$WindowsEnabled
    )
    $actual = Get-TrackerAuthenticationState
    if ($actual.AnonymousEnabled -ne $AnonymousEnabled -or
        $actual.WindowsEnabled -ne $WindowsEnabled) {
        throw "Project Tracker IIS authentication state is not Anonymous=$AnonymousEnabled, Windows=$WindowsEnabled."
    }
}

function Assert-AnonymousTrackerApiDenied {
    param([Parameter(Mandatory = $true)][string]$Uri)
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Method Get -Uri $Uri -TimeoutSec 10
        $statusCode = [int]$response.StatusCode
    }
    catch {
        if ($null -eq $_.Exception.Response) {
            throw "Anonymous Project Tracker authorization probe failed at '$Uri': $($_.Exception.Message)"
        }
        $statusCode = [int]$_.Exception.Response.StatusCode
    }
    if ($statusCode -ne 401) {
        throw "Anonymous Project Tracker /api/me must be denied with HTTP 401; received $statusCode at '$Uri'."
    }
}

function Assert-CredentialedTrackerIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$ExpectedAccountName
    )
    try {
        $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Method Get `
            -Uri $Uri -TimeoutSec 10
        $payload = $response.Content | ConvertFrom-Json
    }
    catch { throw "Credentialed Project Tracker identity probe failed at '$Uri': $($_.Exception.Message)" }
    if ([int]$response.StatusCode -ne 200 -or
        [string]$payload.accountName -ine $ExpectedAccountName) {
        throw "Project Tracker at '$Uri' returned accountName '$($payload.accountName)', not current Windows identity '$ExpectedAccountName'."
    }
}

function Get-TrackerProbeUris {
    param([switch]$RetainedOnly)
    $uris = @(
        "https://$expectedComputerName`:6135/api/me",
        "http://$expectedComputerName`:5135/api/me"
    )
    if (-not $RetainedOnly -and $activeTopology -eq 'Production') {
        $uris = @('https://projects.hub.son4l.local/api/me') + $uris
    }
    return $uris
}

function Assert-TrackerCorsAuthenticationBoundary {
    param([switch]$RetainedOnly)
    $expectedAccountName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ([string]::IsNullOrWhiteSpace($expectedAccountName)) {
        throw 'The current Windows identity could not be determined for Project Tracker verification.'
    }
    Assert-TrackerAuthenticationState -AnonymousEnabled $true -WindowsEnabled $true
    if ($RetainedOnly) {
        Assert-CorsResponse -Origin 'https://son-iis2:6140' -Uri 'https://SON-IIS2:6135/api/me'
        Assert-CorsResponse -Origin 'http://son-iis2:5140' -Uri 'http://SON-IIS2:5135/api/me'
    }
    else { Assert-DualCors }
    foreach ($uri in @(Get-TrackerProbeUris -RetainedOnly:$RetainedOnly)) {
        Assert-AnonymousTrackerApiDenied -Uri $uri
        Assert-CredentialedTrackerIdentity -Uri $uri -ExpectedAccountName $expectedAccountName
    }
}

function Get-RetainedHealthUris {
    $uris = @()
    foreach ($application in $applications) {
        $uris += "http://$expectedComputerName`:$($application.HttpPort)/api/health"
        $uris += "https://$expectedComputerName`:$($application.HttpsPort)/api/health"
    }
    $uris += "http://$expectedComputerName`:5140$gatewayPath/api/health"
    $uris += "https://$expectedComputerName`:6140$gatewayPath/api/health"
    return $uris
}

function Get-HttpHealthUris {
    $uris = @($applications | ForEach-Object { "http://$expectedComputerName`:$($_.HttpPort)/api/health" })
    $uris += "http://$expectedComputerName`:5140$gatewayPath/api/health"
    return $uris
}

function Assert-CorsResponse {
    param([Parameter(Mandatory = $true)][string]$Uri, [Parameter(Mandatory = $true)][string]$Origin)
    try {
        # A browser preflight is anonymous even when the eventual request includes credentials.
        # Probing OPTIONS here catches IIS/authentication ordering problems that a credentialed GET
        # cannot reveal.
        $response = Invoke-WebRequest -UseBasicParsing -Method Options -Uri $Uri -TimeoutSec 10 -Headers @{
            Origin = $Origin
            'Access-Control-Request-Method' = 'POST'
            'Access-Control-Request-Headers' = 'content-type'
        }
    }
    catch { throw "CORS preflight failed for origin '$Origin' at '$Uri': $($_.Exception.Message)" }
    if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 300) {
        throw "CORS preflight returned HTTP $($response.StatusCode) for origin '$Origin' at '$Uri'."
    }
    $allowedOrigin = [string]$response.Headers['Access-Control-Allow-Origin']
    $allowedCredentials = [string]$response.Headers['Access-Control-Allow-Credentials']
    $allowedMethods = @(([string]$response.Headers['Access-Control-Allow-Methods']) -split '\s*,\s*')
    $allowedHeaders = @(([string]$response.Headers['Access-Control-Allow-Headers']) -split '\s*,\s*')
    if ($allowedOrigin -cne $Origin -or
        $allowedCredentials -ine 'true' -or
        'POST' -notin $allowedMethods -or
        'content-type' -notin $allowedHeaders) {
        throw "CORS preflight did not allow the exact credentialed POST/content-type request for origin '$Origin' at '$Uri'."
    }
}

function Assert-DualCors {
    if ($activeTopology -eq 'Production') {
        Assert-CorsResponse -Origin 'https://hub.son4l.local' -Uri 'https://projects.hub.son4l.local/api/me'
        Assert-CorsResponse -Origin 'https://son-iis2:6140' -Uri 'https://SON-IIS2:6135/api/me'
    }
    else {
        Assert-CorsResponse -Origin 'https://son-iis2:6140' -Uri 'https://SON-IIS2:6135/api/me'
    }
    Assert-CorsResponse -Origin 'http://son-iis2:5140' -Uri 'http://SON-IIS2:5135/api/me'
}

function Restore-FilesWhilePoolsStopped {
    param([Parameter(Mandatory = $true)]$State)
    Assert-TransactionBackups $State
    $portalRoot = Split-Path -Parent $State.PortalConfigPath
    $trackerRoot = Split-Path -Parent $State.TrackerConfigPath
    $portalTemporary = Join-Path $portalRoot ('.appsettings.Production.restore.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $trackerTemporary = Join-Path $trackerRoot ('.appsettings.Production.restore.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Copy-Item -LiteralPath $State.PortalBackupPath -Destination $portalTemporary
        Copy-Item -LiteralPath $State.TrackerBackupPath -Destination $trackerTemporary
        $null = Read-JsonFile $portalTemporary
        $null = Read-JsonFile $trackerTemporary
        if ((Get-FileSha256 $portalTemporary) -ne $State.PortalOriginalSha256 -or
            (Get-FileSha256 $trackerTemporary) -ne $State.TrackerOriginalSha256) {
            throw 'Prepared restore files do not match the saved original hashes.'
        }
        Move-Item -LiteralPath $portalTemporary -Destination $State.PortalConfigPath -Force
        Move-Item -LiteralPath $trackerTemporary -Destination $State.TrackerConfigPath -Force
        Assert-OriginalConfiguration $State
    }
    finally {
        Remove-Item -LiteralPath $portalTemporary -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $trackerTemporary -Force -ErrorAction SilentlyContinue
    }
}

function Restore-Configuration {
    param([Parameter(Mandatory = $true)]$State, [switch]$VerifyDualScheme)
    Assert-TransactionBackups $State
    Invoke-WithPoolsStopped `
        -Precondition { Assert-RecoverableActiveConfiguration $State } `
        -Operation { Restore-FilesWhilePoolsStopped $State } `
        -RecoveryOperation { Restore-FilesWhilePoolsStopped $State } `
        -RecoveryCompletesOperation
    # Rollback must remain verifiable after the separate 443 binding transaction is removed.
    # Its stable target is the retained HTTP + 61xx pilot baseline, never the production hosts.
    if ($VerifyDualScheme) {
        Wait-UriHealth (Get-RetainedHealthUris)
        Assert-TrackerCorsAuthenticationBoundary -RetainedOnly
    }
    else { Wait-UriHealth (Get-HttpHealthUris) }
}

if (-not [IO.Path]::IsPathRooted($StatePath)) { throw 'StatePath must be an absolute local path.' }
$StatePath = Assert-SafeStatePath $StatePath
Assert-Host
if (-not $WhatIfPreference) { Assert-Administrator }
$transactionMutex = Enter-HttpsApplicationConfigTransactionLock
try {
Import-IisAdministration
Assert-TrackerAuthenticationState -AnonymousEnabled $true -WindowsEnabled $true
$portalRoot = Get-ActiveSitePath $portalSiteName
$trackerRoot = Get-ActiveSitePath $trackerSiteName
$portalConfigPath = Join-Path $portalRoot 'appsettings.Production.json'
$trackerConfigPath = Join-Path $trackerRoot 'appsettings.Production.json'

if ($Rollback) {
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { throw "Rollback state was not found at '$StatePath'." }
    $state = Read-SecureState
    Assert-TransactionState -State $state -ExpectedPortalConfigPath $portalConfigPath -ExpectedTrackerConfigPath $trackerConfigPath
    Assert-RequestedStateTopology -State $state -RequestedTopology $Topology
    Set-TopologyConfiguration -Name ([string]$state.Topology)
    Assert-TransactionBackups $state
    $terminalStatuses = @('RolledBack', 'AutomaticallyRolledBack')
    $recoverableStatuses = @('Applied', 'Prepared', 'ApplyInProgress', 'ApplyFailedRollbackPending', 'ManualRollbackPending', 'RollbackFailed')
    if ($state.Status -in $terminalStatuses) {
        Assert-OriginalConfiguration $state
        Assert-PoolsPresentAndStarted
        Wait-UriHealth (Get-RetainedHealthUris)
        Assert-TrackerCorsAuthenticationBoundary -RetainedOnly
        Write-Output 'HTTPS_APPLICATION_CONFIG_ALREADY_ROLLED_BACK_AND_DUAL_SCHEME_HEALTHY'
        exit 0
    }
    if ($state.Status -notin $recoverableStatuses) { throw "Rollback state '$($state.Status)' is not recognized as recoverable." }
    Assert-RecoverableActiveConfiguration $state
    Assert-PoolsPresent
    if ($PSCmdlet.ShouldProcess($expectedComputerName, 'Restore original Portal and Project Tracker production configuration')) {
        Set-StateProperty -State $state -Name 'Status' -Value 'ManualRollbackPending'
        Set-StateProperty -State $state -Name 'RollbackStartedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
        Write-SecureState $state
        $restored = $false
        try {
            Restore-Configuration -State $state -VerifyDualScheme
            $restored = $true
            Set-StateProperty -State $state -Name 'Status' -Value 'RolledBack'
            Set-StateProperty -State $state -Name 'RolledBackAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
            Set-StateProperty -State $state -Name 'RollbackFailure' -Value $null
            Write-SecureState $state
        }
        catch {
            $rollbackFailure = $_.Exception.Message
            if ($restored) {
                throw "Original configuration was restored and is healthy, but rollback state persistence failed: $rollbackFailure Re-run -Rollback to verify and finish the state record."
            }
            Set-StateProperty -State $state -Name 'Status' -Value 'RollbackFailed'
            Set-StateProperty -State $state -Name 'RollbackFailure' -Value $rollbackFailure
            Set-StateProperty -State $state -Name 'RollbackFailedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
            $stateWriteFailure = Try-WriteSecureState $state
            if ($stateWriteFailure) { throw "Rollback failed: $rollbackFailure State persistence also failed: $stateWriteFailure" }
            throw "Rollback failed and remains recoverable: $rollbackFailure"
        }
        Write-Output 'HTTPS_APPLICATION_CONFIG_ROLLED_BACK_AND_DUAL_SCHEME_HEALTHY'
    }
    elseif ($WhatIfPreference) { Write-Output 'WHATIF_READY_ROLLBACK: production config backup, hashes, pools, and drift checks passed; nothing was changed.' }
    else { Write-Output 'HTTPS_APPLICATION_CONFIG_ROLLBACK_CANCELLED' }
    exit 0
}

Assert-PoolsPresentAndStarted
$portalOriginal = Read-JsonFile $portalConfigPath
$trackerOriginal = Read-JsonFile $trackerConfigPath
$null = New-TransformedConfig -PortalConfig $portalOriginal -TrackerConfig $trackerOriginal

# HTTPS must already be reachable before any production configuration is changed.
Wait-UriHealth (Get-DualSchemeHealthUris)
if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
    $oldState = Read-SecureState
    Assert-TransactionState -State $oldState -ExpectedPortalConfigPath $portalConfigPath -ExpectedTrackerConfigPath $trackerConfigPath
    Assert-RequestedStateTopology -State $oldState -RequestedTopology $Topology
    if ($oldState.Status -eq 'Applied') {
        Assert-AppliedConfiguration $oldState
        Assert-PoolsPresentAndStarted
        Wait-UriHealth (Get-DualSchemeHealthUris)
        Assert-TrackerCorsAuthenticationBoundary
        Write-Output 'HTTPS_APPLICATION_CONFIG_ALREADY_APPLIED_AND_RETAINED_ENDPOINTS_HEALTHY'
        exit 0
    }
    if ($oldState.Status -notin @('RolledBack', 'AutomaticallyRolledBack')) {
        throw "Transaction state '$($oldState.Status)' already exists at '$StatePath'; run -Rollback before applying another transaction."
    }
    Assert-OriginalConfiguration $oldState
    Set-TopologyConfiguration -Name $Topology
}

if (-not $PSCmdlet.ShouldProcess($expectedComputerName, 'Atomically switch module URLs to HTTPS and configure HTTPS-first retained-origin CORS')) {
    if ($WhatIfPreference) { Write-Output 'WHATIF_READY: active IIS paths, JSON transforms, pools, dual-scheme applications, and gateway passed preflight; nothing was changed.' }
    else { Write-Output 'HTTPS_APPLICATION_CONFIG_CANCELLED' }
    exit 0
}

$backupRoot = Join-Path $backupBaseRoot ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'))
New-SecureDirectory $stateRoot
New-SecureDirectory $backupBaseRoot
New-SecureDirectory $backupRoot
$portalBackup = Join-Path $backupRoot 'Portal.appsettings.Production.json'
$trackerBackup = Join-Path $backupRoot 'ProjectTracker.appsettings.Production.json'
$snapshot = New-VerifiedTransactionSnapshot `
    -PortalConfigPath $portalConfigPath `
    -TrackerConfigPath $trackerConfigPath `
    -PortalBackupPath $portalBackup `
    -TrackerBackupPath $trackerBackup
$portalOriginalSha256 = $snapshot.PortalOriginalSha256
$trackerOriginalSha256 = $snapshot.TrackerOriginalSha256
$portalBytes = [byte[]]$snapshot.PortalPlannedBytes
$trackerBytes = [byte[]]$snapshot.TrackerPlannedBytes
$portalPlannedSha256 = $snapshot.PortalPlannedSha256
$trackerPlannedSha256 = $snapshot.TrackerPlannedSha256
$state = [pscustomobject]@{
    Version = 3
    ComputerName = $expectedComputerName
    Topology = $Topology
    Status = 'Prepared'
    PreparedAtUtc = [DateTime]::UtcNow.ToString('o')
    PortalConfigPath = $portalConfigPath
    TrackerConfigPath = $trackerConfigPath
    PortalBackupPath = $portalBackup
    TrackerBackupPath = $trackerBackup
    PortalOriginalSha256 = $portalOriginalSha256
    TrackerOriginalSha256 = $trackerOriginalSha256
    PortalPlannedSha256 = $portalPlannedSha256
    TrackerPlannedSha256 = $trackerPlannedSha256
    PortalAppliedSha256 = ''
    TrackerAppliedSha256 = ''
    AppliedAtUtc = $null
    RolledBackAtUtc = $null
    ApplyFailure = $null
    ApplyFailedAtUtc = $null
    RollbackStartedAtUtc = $null
    RollbackFailure = $null
    RollbackFailedAtUtc = $null
}
Write-SecureState $state

$portalTemporary = Join-Path $portalRoot ('.appsettings.Production.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$trackerTemporary = Join-Path $trackerRoot ('.appsettings.Production.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllBytes($portalTemporary, $portalBytes)
    [IO.File]::WriteAllBytes($trackerTemporary, $trackerBytes)
    $null = Read-JsonFile $portalTemporary
    $null = Read-JsonFile $trackerTemporary
    if ((Get-FileSha256 $portalTemporary) -ne $state.PortalPlannedSha256 -or
        (Get-FileSha256 $trackerTemporary) -ne $state.TrackerPlannedSha256) {
        throw 'Prepared production configuration hashes do not match the planned transaction.'
    }
    $state.Status = 'ApplyInProgress'
    Write-SecureState $state
    Invoke-WithPoolsStopped `
        -Precondition {
            Assert-OriginalConfiguration $state
            if ((Get-FileSha256 $portalTemporary) -ne $state.PortalPlannedSha256 -or
                (Get-FileSha256 $trackerTemporary) -ne $state.TrackerPlannedSha256) {
                throw 'Prepared configuration drifted before the stopped-pool replacement.'
            }
        } `
        -Operation {
            Move-Item -LiteralPath $portalTemporary -Destination $portalConfigPath -Force
            Move-Item -LiteralPath $trackerTemporary -Destination $trackerConfigPath -Force
            if ((Get-FileSha256 $portalConfigPath) -ne $state.PortalPlannedSha256 -or
                (Get-FileSha256 $trackerConfigPath) -ne $state.TrackerPlannedSha256) {
                throw 'Active production configuration hashes do not match the planned transaction.'
            }
        } `
        -RecoveryOperation { Restore-FilesWhilePoolsStopped $state }
    Wait-UriHealth (Get-DualSchemeHealthUris)
    Assert-TrackerCorsAuthenticationBoundary
    $state.PortalAppliedSha256 = Get-FileSha256 $portalConfigPath
    $state.TrackerAppliedSha256 = Get-FileSha256 $trackerConfigPath
    $state.Status = 'Applied'
    $state.AppliedAtUtc = [DateTime]::UtcNow.ToString('o')
    Write-SecureState $state
    Write-Output 'HTTPS_APPLICATION_CONFIG_APPLIED_AND_DUAL_SCHEME_GATEWAY_HEALTHY'
}
catch {
    $failure = $_.Exception.Message
    Remove-Item -LiteralPath $portalTemporary -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $trackerTemporary -Force -ErrorAction SilentlyContinue
    Set-StateProperty -State $state -Name 'Status' -Value 'ApplyFailedRollbackPending'
    Set-StateProperty -State $state -Name 'ApplyFailure' -Value $failure
    Set-StateProperty -State $state -Name 'ApplyFailedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
    $pendingStateFailure = Try-WriteSecureState $state
    $restored = $false
    try {
        Assert-RecoverableActiveConfiguration $state
        Restore-Configuration -State $state -VerifyDualScheme
        $restored = $true
        Set-StateProperty -State $state -Name 'Status' -Value 'AutomaticallyRolledBack'
        Set-StateProperty -State $state -Name 'RolledBackAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
        Set-StateProperty -State $state -Name 'RollbackFailure' -Value $null
        Write-SecureState $state
    }
    catch {
        $rollbackFailure = $_.Exception.Message
        if ($restored) {
            throw "Configuration transaction failed: $failure Original configuration was restored and its retained HTTP/61xx surfaces are healthy, but final state persistence failed: $rollbackFailure Re-run -Rollback to verify and finish the state record."
        }
        Set-StateProperty -State $state -Name 'Status' -Value 'RollbackFailed'
        Set-StateProperty -State $state -Name 'RollbackFailure' -Value $rollbackFailure
        Set-StateProperty -State $state -Name 'RollbackFailedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
        $rollbackStateFailure = Try-WriteSecureState $state
        $stateFailures = @($pendingStateFailure, $rollbackStateFailure) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $stateFailureText = if ($stateFailures.Count -gt 0) { " State persistence failures: $($stateFailures -join ' | ')" } else { '' }
        throw "Configuration transaction failed: $failure Automatic rollback also failed: $rollbackFailure$stateFailureText"
    }
    $pendingStateText = if ($pendingStateFailure) { " The intermediate failure state could not be persisted: $pendingStateFailure" } else { '' }
    throw "Configuration transaction failed and the original configuration was restored with retained HTTP/61xx health: $failure$pendingStateText"
}
finally {
    Remove-Item -LiteralPath $portalTemporary -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $trackerTemporary -Force -ErrorAction SilentlyContinue
}
}
finally {
    try { $transactionMutex.ReleaseMutex() }
    finally { $transactionMutex.Dispose() }
}
