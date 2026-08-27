<#
    Deploys one immutable Quality Assurance release on SON-IIS2 without changing any other site.

    Use -FirstActivation only when the current Quality application cannot become healthy before the
    corrected SQL Server migration chain is installed. Normal updates require the current endpoint
    to be healthy. The active Production settings are never modified; normal candidates preserve
    them byte-for-byte, repair candidates add only the two reviewed missing database leaves, and
    the explicit server-local SQLite transition changes only the three reviewed Quality-store
    leaves. The candidate must become healthy, and a failed cutover restores the prior IIS path
    and state.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$ReleaseId,

    [string]$ReleaseRoot = 'C:\SonAero\releases\quality-assurance',

    [ValidateSet('SON-IIS2')]
    [string]$ExpectedComputerName = 'SON-IIS2',

    [switch]$FirstActivation,

    [switch]$RepairMissingProductionDatabaseSettings,

    [switch]$UseServerLocalSqlite,

    [switch]$ResumeServerLocalSqlitePreparation,

    [ValidateRange(30, 600)]
    [int]$HealthTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$siteName = 'QualityAssurance'
$poolName = 'QualityAssurance'
$packageFolder = 'QualityAssurance'
$mainDll = 'QualityAssurance.Api.dll'
$healthUri = 'https://quality.hub.son4l.local/api/health'
$httpPort = 5170
$blockedOverrideNames = @(
    'Authentication__Mode',
    'Authentication:Mode',
    'Database__Provider',
    'Database:Provider',
    'QualityDatabase__Provider',
    'QualityDatabase:Provider',
    'QualityDatabase__StorageMode',
    'QualityDatabase:StorageMode',
    'ConnectionStrings__ModuleAccessStore',
    'ConnectionStrings:ModuleAccessStore',
    'ConnectionStrings__QualityStore',
    'ConnectionStrings:QualityStore',
    'SQLCONNSTR_ModuleAccessStore',
    'SQLAZURECONNSTR_ModuleAccessStore',
    'MYSQLCONNSTR_ModuleAccessStore',
    'CUSTOMCONNSTR_ModuleAccessStore',
    'SQLCONNSTR_QualityStore',
    'SQLAZURECONNSTR_QualityStore',
    'MYSQLCONNSTR_QualityStore',
    'CUSTOMCONNSTR_QualityStore'
)
$environmentSelectorNames = @('ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT')
$script:QualitySqliteStorageValidationRequired = $false
$configurationModule = Join-Path $PSScriptRoot 'QualityAssuranceProductionConfiguration.psm1'
$productionTemplate = Join-Path $PSScriptRoot 'templates\quality-assurance.appsettings.Production.json'
if (-not (Test-Path -LiteralPath $configurationModule -PathType Leaf)) {
    throw "Quality Production configuration module is missing: $configurationModule"
}
Import-Module $configurationModule -Force -ErrorAction Stop

function Assert-DeploymentIdentity {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity -or $identity.IsSystem -or $identity.Name -ieq 'NT AUTHORITY\SYSTEM') {
        throw 'Run this script interactively as an authorized domain user, not Local System.'
    }
    if ($identity.Name -notlike 'SON4L\*') {
        throw "Run this script as an authorized SON4L domain user, not '$($identity.Name)'."
    }
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated Windows PowerShell session.'
    }
}

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path)).TrimEnd('\')
}

function Test-PathContainmentOverlap {
    param(
        [Parameter(Mandatory = $true)][string]$FirstPath,
        [Parameter(Mandatory = $true)][string]$SecondPath
    )
    $first = Get-FullPath -Path $FirstPath
    $second = Get-FullPath -Path $SecondPath
    return $first -ieq $second -or
        $first.StartsWith($second + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $second.StartsWith($first + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-QualityEnvironmentVariable {
    param(
        [AllowEmptyString()][string]$Name,
        [AllowEmptyString()][string]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Name)) {
        throw "$Label contains an environment variable without a name."
    }
    if ($Name -in $blockedOverrideNames) {
        throw "$Label must not override '$Name'."
    }
    if ($Name -in $environmentSelectorNames -and $Value -cne 'Production') {
        throw "$Label must not set '$Name' to a non-Production environment."
    }
}

function Assert-ValidWebConfig {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedMainDll
    )
    try { [xml]$configuration = Get-Content -LiteralPath $Path -Raw }
    catch { throw "Invalid web.config XML at '$Path': $($_.Exception.Message)" }
    $nodes = @($configuration.SelectNodes('//aspNetCore'))
    if ($nodes.Count -ne 1) { throw "'$Path' must contain exactly one aspNetCore element." }
    if ([string]$nodes[0].processPath -ine 'dotnet' -or
        [string]$nodes[0].hostingModel -ine 'inprocess' -or
        ([string]$nodes[0].arguments).Trim() -cne ".\$ExpectedMainDll") {
        throw "'$Path' must launch only '$ExpectedMainDll' with the approved in-process dotnet command and no application arguments."
    }
    $environmentNodes = @($configuration.SelectNodes('//aspNetCore/environmentVariables/environmentVariable'))
    foreach ($environmentNode in $environmentNodes) {
        Assert-QualityEnvironmentVariable `
            -Name ([string]$environmentNode.name) `
            -Value ([string]$environmentNode.value) `
            -Label "Quality web.config '$Path'"
    }
}

function Copy-SanitizedApplication {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    New-Item -ItemType Directory -Path $Destination | Out-Null
    $sourcePrefix = $Source.TrimEnd('\')
    foreach ($file in Get-ChildItem -LiteralPath $sourcePrefix -File -Recurse -Force) {
        if ($file.Name -ieq 'appsettings.Production.json') { continue }
        if ($file.Name -like 'appsettings.Development*.json') { continue }
        $relativePath = $file.FullName.Substring($sourcePrefix.Length).TrimStart('\')
        $destinationFile = Join-Path $Destination $relativePath
        $destinationDirectory = Split-Path -Parent $destinationFile
        if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destinationFile
    }
}

function Get-HealthResult {
    param([Parameter(Mandatory = $true)][string]$Uri)
    try {
        $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $Uri -TimeoutSec 10
        return [pscustomobject]@{
            Healthy = ([int]$response.StatusCode -eq 200)
            Detail = "HTTP $($response.StatusCode)"
        }
    }
    catch {
        $status = $null
        if ($null -ne $_.Exception.Response) {
            try { $status = [int]$_.Exception.Response.StatusCode } catch {}
        }
        $detail = if ($null -ne $status) { "HTTP $status" } else { $_.Exception.Message }
        return [pscustomobject]@{ Healthy = $false; Detail = $detail }
    }
}

function Wait-QualityHealth {
    param([Parameter(Mandatory = $true)][int]$TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastResult = $null
    do {
        $lastResult = Get-HealthResult -Uri $healthUri
        if ($lastResult.Healthy) { return }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Quality health verification timed out at '$healthUri'. Last result: $($lastResult.Detail)"
}

function Get-PoolStateValue {
    return (Get-WebAppPoolState -Name $poolName).Value
}

function Assert-QualityActivationState {
    param(
        [Parameter(Mandatory = $true)][string]$PoolState,
        [Parameter(Mandatory = $true)][bool]$Healthy,
        [Parameter(Mandatory = $true)][bool]$FirstActivationRequested,
        [Parameter(Mandatory = $true)][string]$Phase
    )
    if ($PoolState -cne 'Started') {
        throw "Quality pool must already be Started during $Phase; found '$PoolState'."
    }
    if ($FirstActivationRequested -and $Healthy) {
        throw "Quality is already healthy during $Phase; omit -FirstActivation for a normal update."
    }
    if (-not $FirstActivationRequested -and -not $Healthy) {
        throw "Quality is not healthy during $Phase. Use -FirstActivation only for the reviewed first SQL Server activation."
    }
}

function Request-QualityPoolState {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Started', 'Stopped')][string]$State,
        [int]$TimeoutSeconds = 120
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $current = Get-PoolStateValue
        if ($current -eq $State) { return }
        if ($State -eq 'Stopped' -and $current -eq 'Started') {
            try { Stop-WebAppPool -Name $poolName } catch { Start-Sleep -Milliseconds 500 }
        }
        elseif ($State -eq 'Started' -and $current -eq 'Stopped') {
            try { Start-WebAppPool -Name $poolName } catch { Start-Sleep -Milliseconds 500 }
        }
        else { Start-Sleep -Milliseconds 500 }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Quality pool '$poolName' did not become '$State'; its current state is '$current'."
}

function Get-QualityIisBoundary {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$siteName]
        $pool = $manager.ApplicationPools[$poolName]
        if ($null -eq $site -or $null -eq $pool) { throw 'Required Quality IIS site or pool is missing.' }
        if ($pool.ProcessModel.IdentityType -ne
            [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity) {
            throw "Quality pool '$poolName' must use ApplicationPoolIdentity."
        }
        if ([long]$pool.ProcessModel.MaxProcesses -ne 1) {
            throw "Quality pool '$poolName' must use exactly one worker process."
        }
        $application = $site.Applications['/']
        if ($null -eq $application -or $application.ApplicationPoolName -ine $poolName) {
            throw "Quality root application must use pool '$poolName'."
        }
        $virtualDirectory = $application.VirtualDirectories['/']
        if ($null -eq $virtualDirectory) { throw 'Quality root virtual directory is missing.' }
        $httpBindings = @($site.Bindings | Where-Object Protocol -EQ 'http')
        if ($httpBindings.Count -ne 1 -or $httpBindings[0].BindingInformation -ne "*:${httpPort}:") {
            throw "Quality must retain exactly one HTTP binding '*:${httpPort}:'."
        }
        $configuration = $manager.GetApplicationHostConfiguration()
        $poolSection = $configuration.GetSection('system.applicationHost/applicationPools')
        $poolElement = @($poolSection.GetCollection() | Where-Object {
            [string]$_.GetAttributeValue('name') -ieq $poolName
        })[0]
        if ($null -eq $poolElement) { throw "Quality pool '$poolName' is missing from IIS configuration." }
        foreach ($environmentVariable in @($poolElement.GetCollection('environmentVariables'))) {
            Assert-QualityEnvironmentVariable `
                -Name ([string]$environmentVariable.GetAttributeValue('name')) `
                -Value ([string]$environmentVariable.GetAttributeValue('value')) `
                -Label "Quality IIS pool '$poolName'"
        }
        $poolDefaultsElement = $poolSection.GetChildElement('applicationPoolDefaults')
        if ($null -eq $poolDefaultsElement) {
            throw 'IIS application-pool defaults configuration is missing.'
        }
        foreach ($environmentVariable in @($poolDefaultsElement.GetCollection('environmentVariables'))) {
            Assert-QualityEnvironmentVariable `
                -Name ([string]$environmentVariable.GetAttributeValue('name')) `
                -Value ([string]$environmentVariable.GetAttributeValue('value')) `
                -Label 'IIS application-pool defaults configuration'
        }
        $aspNetCoreSection = $configuration.GetSection('system.webServer/aspNetCore', $siteName)
        foreach ($environmentVariable in @($aspNetCoreSection.GetCollection('environmentVariables'))) {
            Assert-QualityEnvironmentVariable `
                -Name ([string]$environmentVariable.GetAttributeValue('name')) `
                -Value ([string]$environmentVariable.GetAttributeValue('value')) `
                -Label "Quality IIS application '$siteName'"
        }
        $anonymousEnabled = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/anonymousAuthentication', $siteName).GetAttributeValue('enabled')
        $windowsEnabled = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/windowsAuthentication', $siteName).GetAttributeValue('enabled')
        if ($anonymousEnabled -or -not $windowsEnabled) {
            throw 'Quality authentication must remain Anonymous=False and Windows=True.'
        }
        $allApplicationPaths = @(
            foreach ($iisSite in $manager.Sites) {
                foreach ($iisApplication in $iisSite.Applications) {
                    foreach ($iisVirtualDirectory in $iisApplication.VirtualDirectories) {
                        if (-not [string]::IsNullOrWhiteSpace([string]$iisVirtualDirectory.PhysicalPath)) {
                            Get-FullPath -Path $iisVirtualDirectory.PhysicalPath
                        }
                    }
                }
            }
        )
        return [pscustomobject]@{
            QualityPath = Get-FullPath -Path $virtualDirectory.PhysicalPath
            AllApplicationPaths = @($allApplicationPaths | Sort-Object -Unique)
            DisallowOverlappingRotation =
                [bool]$pool.Recycling.DisallowOverlappingRotation
        }
    }
    finally { $manager.Dispose() }
}

function Assert-QualityProtectedDeploymentStateRoot {
    $protectedRoot = 'C:\ProgramData\SonAero\deployment-state'
    $item = Get-Item -LiteralPath $protectedRoot -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Protected deployment-state root is missing, is not a directory, or is a reparse point: $protectedRoot"
    }
    $acl = Get-Acl -LiteralPath $protectedRoot -ErrorAction Stop
    $allowedSids = @('S-1-5-18', 'S-1-5-32-544')
    $ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    $rules = @($acl.GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier]))
    $expectedInheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    if (-not $acl.AreAccessRulesProtected -or $ownerSid -notin $allowedSids -or
        $rules.Count -ne 2) {
        throw "Protected deployment-state root has an unsafe owner, inheritance state, or rule count: $protectedRoot"
    }
    foreach ($rule in $rules) {
        if ($rule.IdentityReference.Value -notin $allowedSids -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.IsInherited -or
            [long]$rule.FileSystemRights -ne
                [long][Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne $expectedInheritance -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
            throw "Protected deployment-state root contains an unsafe access rule: $protectedRoot"
        }
    }
}

function Assert-QualitySqliteDataPathBoundary {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$RequireAbsent,
        [switch]$RequireExistingEmpty
    )

    if ($RequireAbsent -and $RequireExistingEmpty) {
        throw 'Quality SQLite path cannot be required to be both absent and an existing empty directory.'
    }
    $resolvedPath = Get-FullPath -Path $Path
    if ($resolvedPath -ine 'C:\ProgramData\SonAero\deployment-state\quality-assurance-data') {
        throw "Quality SQLite data directory is not the approved ProgramData path: $resolvedPath"
    }
    Assert-QualityProtectedDeploymentStateRoot

    $currentPath = [IO.Path]::GetPathRoot($resolvedPath)
    $relativePath = $resolvedPath.Substring($currentPath.Length)
    foreach ($segment in @($relativePath.Split('\') | Where-Object { $_.Length -gt 0 })) {
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) { break }
        $item = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Quality SQLite data path contains a reparse point: $currentPath"
        }
        if ($currentPath -ine $resolvedPath -and -not $item.PSIsContainer) {
            throw "Quality SQLite data path contains a non-directory component: $currentPath"
        }
    }

    if (Test-Path -LiteralPath $resolvedPath -PathType Leaf) {
        throw "Quality SQLite data path is a file: $resolvedPath"
    }
    if ($RequireAbsent -and (Test-Path -LiteralPath $resolvedPath)) {
        throw "Initial Quality SQLite data directory must not already exist: $resolvedPath"
    }
    if ($RequireExistingEmpty) {
        if (-not (Test-Path -LiteralPath $resolvedPath -PathType Container)) {
            throw "Resumed Quality SQLite data directory does not exist: $resolvedPath"
        }
        Assert-QualitySqliteDataDirectoryEmpty -Path $resolvedPath
        Assert-QualitySqliteDataDirectoryAcl -Path $resolvedPath
    }

    try {
        [void](New-Object Security.Principal.NTAccount('IIS AppPool', $poolName)).Translate(
            [Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "Unable to resolve the Quality IIS application-pool identity: $($_.Exception.Message)"
    }
    return $resolvedPath
}

function Assert-QualitySqliteDataDirectoryEmpty {
    param([Parameter(Mandatory = $true)][string]$Path)

    $items = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop)
    if ($items.Count -ne 0) {
        throw "Resumed Quality SQLite data directory must be empty; found '$($items[0].Name)'."
    }
}

function Assert-QualitySqliteDataDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $poolIdentity = (New-Object Security.Principal.NTAccount(
        'IIS AppPool', $poolName)).Translate([Security.Principal.SecurityIdentifier])
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $expectedRights = @{
        $administrators.Value = [Security.AccessControl.FileSystemRights]::FullControl
        $system.Value = [Security.AccessControl.FileSystemRights]::FullControl
        $poolIdentity.Value = Get-QualityServerLocalSqliteModifyRights
    }
    $actualAcl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    if (-not $actualAcl.AreAccessRulesProtected -or
        $actualAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne
            $administrators.Value) {
        throw 'Quality SQLite data directory ownership or inheritance protection is incorrect.'
    }
    $actualRules = @($actualAcl.GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier]))
    if ($actualRules.Count -ne $expectedRights.Count) {
        throw 'Quality SQLite data directory must contain exactly three protected access rules.'
    }
    foreach ($rule in $actualRules) {
        $sid = $rule.IdentityReference.Value
        if (-not $expectedRights.ContainsKey($sid) -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.IsInherited -or
            [long]$rule.FileSystemRights -ne [long]$expectedRights[$sid] -or
            $rule.InheritanceFlags -ne $inheritance -or
            $rule.PropagationFlags -ne $propagation) {
            throw "Quality SQLite data directory contains an unexpected access rule for '$sid'."
        }
    }
}

function Initialize-QualitySqliteDataDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$ResumePreparedDirectory
    )

    $resolvedPath = if ($ResumePreparedDirectory) {
        Assert-QualitySqliteDataPathBoundary -Path $Path -RequireExistingEmpty
    }
    else {
        Assert-QualitySqliteDataPathBoundary -Path $Path -RequireAbsent
    }
    $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $poolIdentity = (New-Object Security.Principal.NTAccount(
        'IIS AppPool', $poolName)).Translate([Security.Principal.SecurityIdentifier])
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $expectedRights = @{
        $administrators.Value = [Security.AccessControl.FileSystemRights]::FullControl
        $system.Value = [Security.AccessControl.FileSystemRights]::FullControl
        $poolIdentity.Value = Get-QualityServerLocalSqliteModifyRights
    }

    if (-not $ResumePreparedDirectory) {
        $directorySecurity = New-Object Security.AccessControl.DirectorySecurity
        $directorySecurity.SetAccessRuleProtection($true, $false)
        $directorySecurity.SetOwner($administrators)
        foreach ($grant in @(
            [pscustomobject]@{ Identity = $administrators; Rights = [Security.AccessControl.FileSystemRights]::FullControl },
            [pscustomobject]@{ Identity = $system; Rights = [Security.AccessControl.FileSystemRights]::FullControl },
            [pscustomobject]@{ Identity = $poolIdentity; Rights = [Security.AccessControl.FileSystemRights]::Modify }
        )) {
            $rule = New-Object Security.AccessControl.FileSystemAccessRule(
                $grant.Identity, $grant.Rights, $inheritance, $propagation, $allow)
            $directorySecurity.SetAccessRule($rule)
        }
        [void][IO.Directory]::CreateDirectory($resolvedPath, $directorySecurity)
        [void](Assert-QualitySqliteDataPathBoundary -Path $resolvedPath)
        Set-Acl -LiteralPath $resolvedPath -AclObject $directorySecurity
    }
    Assert-QualitySqliteDataDirectoryEmpty -Path $resolvedPath
    Assert-QualitySqliteDataDirectoryAcl -Path $resolvedPath

    $dataFile = Join-Path $resolvedPath 'quality-assurance.db'
    $fileStream = $null
    try {
        $fileStream = [IO.File]::Open(
            $dataFile,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    }
    finally {
        if ($null -ne $fileStream) { $fileStream.Dispose() }
    }
    $dataFileItem = Get-Item -LiteralPath $dataFile -Force -ErrorAction Stop
    if (($dataFileItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $dataFileItem.PSIsContainer -or $dataFileItem.Length -ne 0) {
        throw "The initial Quality SQLite data file is not a new empty regular file: $dataFile"
    }

    $fileSecurity = New-Object Security.AccessControl.FileSecurity
    $fileSecurity.SetAccessRuleProtection($true, $false)
    $fileSecurity.SetOwner($administrators)
    foreach ($grant in @(
        [pscustomobject]@{ Identity = $administrators; Rights = [Security.AccessControl.FileSystemRights]::FullControl },
        [pscustomobject]@{ Identity = $system; Rights = [Security.AccessControl.FileSystemRights]::FullControl },
        [pscustomobject]@{ Identity = $poolIdentity; Rights = [Security.AccessControl.FileSystemRights]::Modify }
    )) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $grant.Identity, $grant.Rights, $allow)
        $fileSecurity.SetAccessRule($rule)
    }
    Set-Acl -LiteralPath $dataFile -AclObject $fileSecurity

    $actualFileAcl = Get-Acl -LiteralPath $dataFile
    if (-not $actualFileAcl.AreAccessRulesProtected -or
        $actualFileAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne
            $administrators.Value) {
        throw 'Quality SQLite data file ownership or inheritance protection is incorrect.'
    }
    $actualFileRules = @($actualFileAcl.GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier]))
    if ($actualFileRules.Count -ne $expectedRights.Count) {
        throw 'Quality SQLite data file must contain exactly three protected access rules.'
    }
    foreach ($rule in $actualFileRules) {
        $sid = $rule.IdentityReference.Value
        if (-not $expectedRights.ContainsKey($sid) -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.IsInherited -or
            [long]$rule.FileSystemRights -ne [long]$expectedRights[$sid] -or
            $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
            throw "Quality SQLite data file contains an unexpected access rule for '$sid'."
        }
    }
    return $dataFile
}

function Set-QualityPhysicalPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$siteName]
        if ($null -eq $site) { throw "Quality site '$siteName' disappeared during deployment." }
        $site.Applications['/'].VirtualDirectories['/'].PhysicalPath = $Path
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}

function Set-QualityDisallowOverlappingRotation {
    param([Parameter(Mandatory = $true)][bool]$Enabled)

    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $pool = $manager.ApplicationPools[$poolName]
        if ($null -eq $pool) { throw "Quality pool '$poolName' disappeared during deployment." }
        $pool.Recycling.DisallowOverlappingRotation = $Enabled
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }

    $verificationManager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $actual = [bool]$verificationManager.ApplicationPools[$poolName].Recycling.DisallowOverlappingRotation
        if ($actual -ne $Enabled) {
            throw "Quality pool '$poolName' overlapping-rotation setting did not become '$Enabled'."
        }
    }
    finally { $verificationManager.Dispose() }
}

function Assert-QualityPhysicalPath {
    param([Parameter(Mandatory = $true)][string]$ExpectedPath)
    $actualPath = (Get-QualityIisBoundary).QualityPath
    if ($actualPath -ine (Get-FullPath -Path $ExpectedPath)) {
        throw "Quality IIS path is '$actualPath', expected '$ExpectedPath'."
    }
}

function Restore-PriorQualityRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$PriorPath,
        [Parameter(Mandatory = $true)][ValidateSet('Started', 'Stopped')][string]$PriorPoolState,
        [Parameter(Mandatory = $true)][bool]$PriorWasHealthy,
        [Parameter(Mandatory = $true)][bool]$PriorDisallowOverlappingRotation
    )
    Request-QualityPoolState -State Stopped
    Set-QualityPhysicalPath -Path $PriorPath
    Assert-QualityPhysicalPath -ExpectedPath $PriorPath
    Set-QualityDisallowOverlappingRotation `
        -Enabled $PriorDisallowOverlappingRotation
    Request-QualityPoolState -State $PriorPoolState
    if ($PriorPoolState -eq 'Started' -and $PriorWasHealthy) {
        Wait-QualityHealth -TimeoutSeconds $HealthTimeoutSeconds
    }
    Assert-QualityPhysicalPath -ExpectedPath $PriorPath
}

function Invoke-QualityIisSwitch {
    param(
        [Parameter(Mandatory = $true)][string]$CurrentPath,
        [Parameter(Mandatory = $true)][string]$CandidatePath,
        [Parameter(Mandatory = $true)][ValidateSet('Started', 'Stopped')][string]$PriorPoolState,
        [Parameter(Mandatory = $true)][bool]$PriorWasHealthy,
        [Parameter(Mandatory = $true)][bool]$PriorDisallowOverlappingRotation
    )
    try {
        Request-QualityPoolState -State Stopped
        if ($script:QualitySqliteStorageValidationRequired) {
            Set-QualityDisallowOverlappingRotation -Enabled $true
        }
        Set-QualityPhysicalPath -Path $CandidatePath
        Assert-QualityPhysicalPath -ExpectedPath $CandidatePath
        Request-QualityPoolState -State Started
        Wait-QualityHealth -TimeoutSeconds $HealthTimeoutSeconds
        if ($script:QualitySqliteStorageValidationRequired) {
            [void](Assert-QualityServerLocalSqliteStorage -PoolName $poolName)
        }
        Assert-QualityPhysicalPath -ExpectedPath $CandidatePath
    }
    catch {
        $deploymentFailure = $_.Exception.Message
        $rollbackErrors = New-Object System.Collections.Generic.List[string]
        try {
            Restore-PriorQualityRuntime -PriorPath $CurrentPath -PriorPoolState $PriorPoolState `
                -PriorWasHealthy $PriorWasHealthy `
                -PriorDisallowOverlappingRotation $PriorDisallowOverlappingRotation
        }
        catch { $rollbackErrors.Add($_.Exception.Message) }
        if ($rollbackErrors.Count -eq 0) {
            throw "Quality release failed and the exact prior IIS path and pool state were restored. The failed candidate was retained at '$CandidatePath'. $deploymentFailure"
        }
        throw "Quality release failed. Rollback also reported: $($rollbackErrors -join ' | '). The failed candidate was retained at '$CandidatePath'. Original failure: $deploymentFailure"
    }
}

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
}
Assert-DeploymentIdentity
if ($FirstActivation -and $RepairMissingProductionDatabaseSettings) {
    throw '-FirstActivation and -RepairMissingProductionDatabaseSettings are mutually exclusive.'
}
if ($UseServerLocalSqlite -and
    ($FirstActivation -or $RepairMissingProductionDatabaseSettings)) {
    throw '-UseServerLocalSqlite is mutually exclusive with -FirstActivation and -RepairMissingProductionDatabaseSettings.'
}
if ($ResumeServerLocalSqlitePreparation -and -not $UseServerLocalSqlite) {
    throw '-ResumeServerLocalSqlitePreparation requires -UseServerLocalSqlite.'
}
foreach ($blockedOverrideName in $blockedOverrideNames) {
    if ($null -ne [Environment]::GetEnvironmentVariable(
            $blockedOverrideName, [EnvironmentVariableTarget]::Machine)) {
        throw "Machine environment variable '$blockedOverrideName' must not override Quality Production settings."
    }
}
foreach ($environmentSelectorName in $environmentSelectorNames) {
    $environmentSelectorValue = [Environment]::GetEnvironmentVariable(
        $environmentSelectorName, [EnvironmentVariableTarget]::Machine)
    if ($null -ne $environmentSelectorValue -and $environmentSelectorValue -cne 'Production') {
        throw "Machine environment variable '$environmentSelectorName' must not select a non-Production environment."
    }
}
if ($ReleaseId -in @('.', '..')) { throw 'ReleaseId cannot be a relative-path marker.' }
$packagePath = Get-FullPath -Path $PackageRoot
$sourcePath = Join-Path $packagePath $packageFolder
$releaseRootPath = Get-FullPath -Path $ReleaseRoot
if ($releaseRootPath -eq [IO.Path]::GetPathRoot($releaseRootPath)) { throw 'ReleaseRoot cannot be a drive root.' }
if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
    throw "Quality package folder is missing: $sourcePath"
}
$releasePath = Get-FullPath -Path (Join-Path $releaseRootPath $ReleaseId)
if (-not $releasePath.StartsWith($releaseRootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The resolved release destination escaped ReleaseRoot.'
}
if (Test-Path -LiteralPath $releasePath) {
    throw "Release destination already exists and will not be overwritten: $releasePath"
}
if (Test-PathContainmentOverlap -FirstPath $sourcePath -SecondPath $releasePath) {
    throw 'Package source and release destination cannot contain one another.'
}
$sourceItem = Get-Item -LiteralPath $sourcePath
if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Package '$sourcePath' is a reparse point and cannot be deployed."
}
$reparsePoints = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -Force | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
})
if ($reparsePoints.Count -gt 0) { throw "Package '$sourcePath' contains a reparse point and cannot be deployed." }
Assert-ValidWebConfig -Path (Join-Path $sourcePath 'web.config') -ExpectedMainDll $mainDll
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath $mainDll) -PathType Leaf)) {
    throw "Package DLL is missing: $mainDll"
}
[void](Get-QualitySanitizedApplicationManifest -Root $sourcePath)

$priorWhatIfPreference = $WhatIfPreference
try { $WhatIfPreference = $false; Import-Module WebAdministration -ErrorAction Stop }
finally { $WhatIfPreference = $priorWhatIfPreference }
if (-not ('Microsoft.Web.Administration.ServerManager' -as [type])) {
    $assemblyPath = Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) { throw "IIS assembly not found: $assemblyPath" }
    Add-Type -Path $assemblyPath -ErrorAction Stop
}
if ((Get-WebsiteState -Name $siteName).Value -ne 'Started') {
    throw "Quality IIS site '$siteName' must be started. No changes were made."
}
$boundary = Get-QualityIisBoundary
$currentPath = $boundary.QualityPath
$priorDisallowOverlappingRotation = [bool]$boundary.DisallowOverlappingRotation
foreach ($activePath in $boundary.AllApplicationPaths) {
    if (Test-PathContainmentOverlap -FirstPath $releasePath -SecondPath $activePath) {
        throw "Quality release destination '$releasePath' overlaps active IIS path '$activePath'."
    }
}
if (-not (Test-Path -LiteralPath $currentPath -PathType Container)) {
    throw "Current Quality IIS path does not exist: $currentPath"
}
$currentProductionSettings = Join-Path $currentPath 'appsettings.Production.json'
if (-not (Test-Path -LiteralPath $currentProductionSettings -PathType Leaf)) {
    throw "Quality Production settings are missing: $currentProductionSettings"
}
$currentProductionHash = (Get-FileHash -LiteralPath $currentProductionSettings -Algorithm SHA256).Hash
$activeProductionConfiguration = $null
$repairPlan = if ($RepairMissingProductionDatabaseSettings) {
    New-QualityProductionDatabaseConfigurationRepair -ActivePath $currentProductionSettings `
        -TemplatePath $productionTemplate
}
else {
    $activeProductionConfiguration = Read-QualityProductionConfiguration `
        -Path $currentProductionSettings
    $null
}
$sqlitePlan = if ($UseServerLocalSqlite) {
    New-QualityServerLocalSqliteConfiguration -ActivePath $currentProductionSettings
}
else { $null }
$script:QualitySqliteStorageValidationRequired = [bool]$UseServerLocalSqlite -or
    ($null -ne $activeProductionConfiguration -and
        (Test-QualityProductionConfigurationUsesServerLocalSqlite `
            -Configuration $activeProductionConfiguration))
if (-not $UseServerLocalSqlite -and $script:QualitySqliteStorageValidationRequired) {
    [void](Assert-QualityServerLocalSqliteStorage -PoolName $poolName)
}
if ($UseServerLocalSqlite) {
    if ($ResumeServerLocalSqlitePreparation) {
        [void](Assert-QualitySqliteDataPathBoundary `
            -Path $sqlitePlan.DataDirectory -RequireExistingEmpty)
    }
    else {
        [void](Assert-QualitySqliteDataPathBoundary `
            -Path $sqlitePlan.DataDirectory -RequireAbsent)
    }
}
$priorPoolState = Get-PoolStateValue
$currentHealth = Get-HealthResult -Uri $healthUri
$currentHealthy = [bool]$currentHealth.Healthy
Assert-QualityActivationState -PoolState $priorPoolState -Healthy $currentHealthy `
    -FirstActivationRequested ([bool]$FirstActivation) -Phase 'preflight'

if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName Quality release '$releasePath'",
        $(if ($RepairMissingProductionDatabaseSettings) {
            'Create an immutable Quality release with only the two missing Production database settings, switch only its IIS path, and verify candidate health with exact path/state rollback'
        }
        elseif ($ResumeServerLocalSqlitePreparation) {
            'Resume the reviewed server-local SQLite transition from only the exact empty protected directory left by a failed pre-IIS preparation, create a fresh immutable Quality release, enforce non-overlapping single-worker execution, switch only its IIS path, and verify candidate health with exact path/state rollback'
        }
        elseif ($UseServerLocalSqlite) {
            'Create an immutable Quality release using the reviewed persistent server-local SQLite bridge, enforce non-overlapping single-worker execution, switch only its IIS path, and verify candidate health with exact path/state rollback'
        }
        else {
            'Create an immutable Quality release, switch only its IIS path, and verify candidate health with exact path/state rollback'
        }))) {
    if ($RepairMissingProductionDatabaseSettings) {
        Write-Output 'WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_PRODUCTION_DATABASE_SETTINGS_REPAIRED'
    }
    elseif ($ResumeServerLocalSqlitePreparation) {
        Write-Output 'WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_SERVER_LOCAL_SQLITE_RESUME'
    }
    elseif ($UseServerLocalSqlite) {
        Write-Output 'WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_SERVER_LOCAL_SQLITE'
    }
    else { Write-Output 'WHATIF_READY_QUALITY_ASSURANCE_RELEASE' }
    return
}
if (Test-Path -LiteralPath $releasePath) { throw "Release destination appeared after preflight: $releasePath" }

try {
    New-Item -ItemType Directory -Path $releaseRootPath -Force | Out-Null
    Copy-SanitizedApplication -Source $sourcePath -Destination $releasePath
    $candidateProductionSettings = Join-Path $releasePath 'appsettings.Production.json'
    if ($RepairMissingProductionDatabaseSettings) {
        [IO.File]::WriteAllBytes($candidateProductionSettings, [byte[]]$repairPlan.Utf8Bytes)
    }
    elseif ($UseServerLocalSqlite) {
        [IO.File]::WriteAllBytes($candidateProductionSettings, [byte[]]$sqlitePlan.Utf8Bytes)
    }
    else {
        Copy-Item -LiteralPath $currentProductionSettings -Destination $candidateProductionSettings
        if ($currentProductionHash -ne
            (Get-FileHash -Algorithm SHA256 -LiteralPath $candidateProductionSettings).Hash) {
            throw 'Copied Quality Production settings hash mismatch.'
        }
    }
    [void](Read-QualityProductionConfiguration -Path $candidateProductionSettings)
    $candidateProductionHash = (Get-FileHash -LiteralPath $candidateProductionSettings -Algorithm SHA256).Hash
    Assert-QualitySanitizedApplicationManifestEqual -SourceRoot $sourcePath -CandidateRoot $releasePath
    Assert-ValidWebConfig -Path (Join-Path $releasePath 'web.config') -ExpectedMainDll $mainDll
    if (-not (Test-Path -LiteralPath (Join-Path $releasePath $mainDll) -PathType Leaf)) {
        throw "Candidate application DLL is missing: $mainDll"
    }
    $developmentSettings = @(Get-ChildItem -LiteralPath $releasePath -File -Recurse -Force |
        Where-Object Name -Like 'appsettings.Development*.json')
    if ($developmentSettings.Count -gt 0) {
        throw 'Development configuration was found in the Quality candidate release.'
    }
    if ($UseServerLocalSqlite) {
        [void](Initialize-QualitySqliteDataDirectory `
            -Path $sqlitePlan.DataDirectory `
            -ResumePreparedDirectory:$ResumeServerLocalSqlitePreparation)
    }
    & icacls.exe $releasePath /grant "IIS AppPool\$poolName`:(OI)(CI)RX" /t /c | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Read/execute permission assignment failed for '$poolName'." }
}
catch {
    throw "Quality release preparation failed before IIS was changed. The incomplete candidate was retained at '$releasePath'. $($_.Exception.Message)"
}

$cutoverBoundary = Get-QualityIisBoundary
if ($cutoverBoundary.QualityPath -ine $currentPath) {
    throw "Quality IIS path changed during candidate preparation. No IIS state was changed; the candidate was retained at '$releasePath'."
}
if ((Get-PoolStateValue) -ne $priorPoolState) {
    throw "Quality pool state changed during candidate preparation. The candidate was retained at '$releasePath'."
}
if ((Get-WebsiteState -Name $siteName).Value -ne 'Started') {
    throw "Quality IIS site stopped during candidate preparation. The candidate was retained at '$releasePath'."
}
$cutoverHealth = Get-HealthResult -Uri $healthUri
Assert-QualityActivationState -PoolState (Get-PoolStateValue) -Healthy ([bool]$cutoverHealth.Healthy) `
    -FirstActivationRequested ([bool]$FirstActivation) -Phase 'cutover preflight'
Assert-QualitySanitizedApplicationManifestEqual -SourceRoot $sourcePath -CandidateRoot $releasePath
if ((Get-FileHash -LiteralPath $currentProductionSettings -Algorithm SHA256).Hash -ne $currentProductionHash) {
    throw "Active Quality Production settings changed during candidate preparation. No IIS state was changed; the candidate was retained at '$releasePath'."
}
if ((Get-FileHash -LiteralPath $candidateProductionSettings -Algorithm SHA256).Hash -ne $candidateProductionHash) {
    throw "Candidate Quality Production settings changed after validation. No IIS state was changed; the candidate was retained at '$releasePath'."
}

Invoke-QualityIisSwitch -CurrentPath $currentPath -CandidatePath $releasePath `
    -PriorPoolState $priorPoolState -PriorWasHealthy $currentHealthy `
    -PriorDisallowOverlappingRotation $priorDisallowOverlappingRotation

[pscustomobject]@{
    Status = if ($RepairMissingProductionDatabaseSettings) {
        'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_PRODUCTION_DATABASE_SETTINGS_REPAIRED'
    }
    elseif ($ResumeServerLocalSqlitePreparation) {
        'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SERVER_LOCAL_SQLITE_RESUME'
    }
    elseif ($UseServerLocalSqlite) {
        'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SERVER_LOCAL_SQLITE'
    }
    else { 'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY' }
    ReleaseId = $ReleaseId
    ReleasePath = $releasePath
    FirstActivation = [bool]$FirstActivation
    ProductionDatabaseSettingsRepaired = [bool]$RepairMissingProductionDatabaseSettings
    ServerLocalSqliteEnabled = [bool]$UseServerLocalSqlite
    ServerLocalSqlitePreparationResumed = [bool]$ResumeServerLocalSqlitePreparation
} | Format-List
if ($RepairMissingProductionDatabaseSettings) {
    Write-Output 'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_PRODUCTION_DATABASE_SETTINGS_REPAIRED'
}
elseif ($ResumeServerLocalSqlitePreparation) {
    Write-Output 'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SERVER_LOCAL_SQLITE_RESUME'
}
elseif ($UseServerLocalSqlite) {
    Write-Output 'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SERVER_LOCAL_SQLITE'
}
else { Write-Output 'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY' }
