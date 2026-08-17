<#
    Hides the Engineering Hub and Quality Assurance cards in the production Portal catalog.

    This is a presentation control, not an authorization boundary. The module sites and their
    independent authorization remain unchanged. The production template carries the same policy
    so later Hub releases do not accidentally re-enable the cards.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('SON-IIS2')]
    [string]$ExpectedComputerName = 'SON-IIS2',

    [ValidateSet('SonAeroPortal')]
    [string]$SiteName = 'SonAeroPortal',

    [ValidateSet('SonAeroPortal')]
    [string]$AppPoolName = 'SonAeroPortal',

    [ValidateSet('https://hub.son4l.local/api/apps')]
    [string]$VerificationUri = 'https://hub.son4l.local/api/apps',

    [ValidateRange(10, 180)]
    [int]$HealthTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$hiddenApplicationIds = @('engineering-hub', 'quality-assurance')
$requiredVisibleApplicationIds = @('project-tracker', 'admin-console')
$disabledRoleSentinel = '__production-disabled__'

function Assert-InteractiveAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity -or $identity.IsSystem -or
        $identity.Name -ieq 'NT AUTHORITY\SYSTEM') {
        throw 'This script rejects Local System. Run it interactively as an authorized SON4L domain user.'
    }
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated Windows PowerShell session.'
    }
}

function Import-IisAdministration {
    $assemblyPath = Join-Path $env:WINDIR 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "IIS administration assembly was not found at '$assemblyPath'."
    }
    if (-not ('Microsoft.Web.Administration.ServerManager' -as [type])) {
        Add-Type -Path $assemblyPath -ErrorAction Stop
    }
    $priorWhatIfPreference = $WhatIfPreference
    try {
        $WhatIfPreference = $false
        Import-Module WebAdministration -ErrorAction Stop
    }
    finally { $WhatIfPreference = $priorWhatIfPreference }
}

function Read-PortalProductionConfiguration {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($raw)) { throw 'The file is empty.' }
        return $raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Invalid Portal production JSON at '$Path': $($_.Exception.Message)"
    }
}

function Get-ApplicationMap {
    param([Parameter(Mandatory = $true)]$Configuration)

    if (-not $Configuration.PSObject.Properties['Portal'] -or
        $null -eq $Configuration.Portal -or
        -not $Configuration.Portal.PSObject.Properties['Applications']) {
        throw 'Portal production configuration has no Portal.Applications catalog.'
    }
    $applications = @($Configuration.Portal.Applications)
    if ($applications.Count -eq 0) {
        throw 'Portal production configuration has an empty Portal.Applications catalog.'
    }

    $map = @{}
    foreach ($application in $applications) {
        $id = [string]$application.Id
        if ([string]::IsNullOrWhiteSpace($id)) {
            throw 'Portal production configuration contains an application without an Id.'
        }
        if ($map.ContainsKey($id)) {
            throw "Portal production configuration contains more than one '$id' application."
        }
        $map[$id] = $application
    }
    return $map
}

function Get-NormalizedAllowedRoles {
    param(
        [Parameter(Mandatory = $true)]$Application,
        [Parameter(Mandatory = $true)][string]$ApplicationId
    )

    $property = $Application.PSObject.Properties['AllowedRoles']
    if (-not $property -or $null -eq $property.Value) { return @() }
    if ($property.Value -is [string] -or $property.Value -isnot [System.Collections.IEnumerable]) {
        throw "Portal application '$ApplicationId' has a non-array AllowedRoles value."
    }
    $roles = @($property.Value)
    foreach ($role in $roles) {
        if ($role -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$role)) {
            throw "Portal application '$ApplicationId' has a blank or non-string AllowedRoles entry."
        }
    }
    return @($roles | ForEach-Object { ([string]$_).Trim() })
}

function Set-HiddenApplicationPolicy {
    param(
        [Parameter(Mandatory = $true)]$Configuration,
        [Parameter(Mandatory = $true)][string[]]$ApplicationIds,
        [Parameter(Mandatory = $true)][string]$Sentinel
    )

    $map = Get-ApplicationMap -Configuration $Configuration
    foreach ($id in $ApplicationIds) {
        if (-not $map.ContainsKey($id)) {
            throw "Portal production configuration is missing required application '$id'."
        }
        $application = $map[$id]
        [void](Get-NormalizedAllowedRoles -Application $application -ApplicationId $id)
        $property = $application.PSObject.Properties['AllowedRoles']
        if ($property) { $property.Value = @($Sentinel) }
        else {
            $application | Add-Member -MemberType NoteProperty -Name AllowedRoles -Value @($Sentinel)
        }
    }
}

function Test-HiddenApplicationPolicy {
    param(
        [Parameter(Mandatory = $true)]$Configuration,
        [Parameter(Mandatory = $true)][string[]]$ApplicationIds,
        [Parameter(Mandatory = $true)][string]$Sentinel
    )

    $map = Get-ApplicationMap -Configuration $Configuration
    foreach ($id in $ApplicationIds) {
        if (-not $map.ContainsKey($id)) { return $false }
        $roles = @(Get-NormalizedAllowedRoles -Application $map[$id] -ApplicationId $id)
        if ($roles.Count -ne 1 -or $roles[0] -cne $Sentinel) { return $false }
    }
    return $true
}

function ConvertTo-Utf8JsonBytes {
    param([Parameter(Mandatory = $true)]$Configuration)
    $json = $Configuration | ConvertTo-Json -Depth 100
    return (New-Object Text.UTF8Encoding($false)).GetBytes($json + [Environment]::NewLine)
}

function Assert-PortalCatalogVisibility {
    param(
        [AllowNull()][object[]]$Applications,
        [Parameter(Mandatory = $true)][string[]]$HiddenIds,
        [Parameter(Mandatory = $true)][string[]]$RequiredVisibleIds
    )

    $visibleIds = @($Applications | ForEach-Object { [string]$_.id })
    $stillVisible = @($HiddenIds | Where-Object { $visibleIds -contains $_ })
    if ($stillVisible.Count -gt 0) {
        throw "Deferred Portal cards are still visible: $($stillVisible -join ', ')."
    }
    $missingRequired = @($RequiredVisibleIds | Where-Object { $visibleIds -notcontains $_ })
    if ($missingRequired.Count -gt 0) {
        throw "Required Portal cards are missing: $($missingRequired -join ', ')."
    }
}

function Wait-ForPortalVisibility {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string[]]$HiddenIds,
        [Parameter(Mandatory = $true)][string[]]$RequiredVisibleIds,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = 'No response received.'
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials `
                -Uri $Uri -TimeoutSec 15
            if ([int]$response.StatusCode -ne 200) {
                throw "HTTP $($response.StatusCode)"
            }
            $applications = @($response.Content | ConvertFrom-Json)
            Assert-PortalCatalogVisibility -Applications $applications -HiddenIds $HiddenIds `
                -RequiredVisibleIds $RequiredVisibleIds
            return
        }
        catch { $lastError = $_.Exception.Message }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Portal did not hide the deferred modules at '$Uri'. Last error: $lastError"
}

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is '$env:COMPUTERNAME'."
}
Assert-InteractiveAdministrator
Import-IisAdministration

$manager = New-Object Microsoft.Web.Administration.ServerManager
try {
    $site = $manager.Sites[$SiteName]
    if ($null -eq $site) { throw "Required IIS site '$SiteName' does not exist." }
    if ($null -eq $manager.ApplicationPools[$AppPoolName]) {
        throw "Required IIS application pool '$AppPoolName' does not exist."
    }
    $rootApplication = $site.Applications['/']
    $rootDirectory = if ($rootApplication) { $rootApplication.VirtualDirectories['/'] } else { $null }
    if ($null -eq $rootDirectory) { throw "IIS site '$SiteName' has no root virtual directory." }
    $physicalPath = [Environment]::ExpandEnvironmentVariables([string]$rootDirectory.PhysicalPath)
    $physicalPath = [IO.Path]::GetFullPath($physicalPath)
}
finally { $manager.Dispose() }

$configurationPath = Join-Path $physicalPath 'appsettings.Production.json'
if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
    throw "Active Portal production configuration was not found: $configurationPath"
}

$originalBytes = [IO.File]::ReadAllBytes($configurationPath)
$configuration = Read-PortalProductionConfiguration -Path $configurationPath
$alreadyHidden = Test-HiddenApplicationPolicy -Configuration $configuration `
    -ApplicationIds $hiddenApplicationIds -Sentinel $disabledRoleSentinel

if ($alreadyHidden) {
    if (-not $PSCmdlet.ShouldProcess(
            "$ExpectedComputerName/$SiteName",
            'Recycle the Portal pool and verify the existing production module visibility policy')) {
        Write-Host 'WHATIF_READY_PORTAL_PRODUCTION_MODULE_VISIBILITY: active configuration and IIS preflight passed; nothing was changed.'
        return
    }
    Restart-WebAppPool -Name $AppPoolName
    Wait-ForPortalVisibility -Uri $VerificationUri -HiddenIds $hiddenApplicationIds `
        -RequiredVisibleIds $requiredVisibleApplicationIds -TimeoutSeconds $HealthTimeoutSeconds
    Write-Host 'PORTAL_PRODUCTION_MODULES_ALREADY_HIDDEN_AND_VERIFIED'
    return
}

Set-HiddenApplicationPolicy -Configuration $configuration `
    -ApplicationIds $hiddenApplicationIds -Sentinel $disabledRoleSentinel
$updatedBytes = ConvertTo-Utf8JsonBytes -Configuration $configuration

if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName/$SiteName",
        'Hide Engineering Hub and Quality Assurance cards in the production Portal catalog')) {
    Write-Host 'WHATIF_READY_PORTAL_PRODUCTION_MODULE_VISIBILITY: active configuration and IIS preflight passed; nothing was changed.'
    return
}

$changeMayHaveOccurred = $false
$operationId = [Guid]::NewGuid().ToString('N')
$temporaryPath = "$configurationPath.visibility-$operationId.tmp"
$backupPath = "$configurationPath.visibility-$operationId.backup"
try {
    [IO.File]::WriteAllBytes($temporaryPath, $updatedBytes)
    $prepared = Read-PortalProductionConfiguration -Path $temporaryPath
    if (-not (Test-HiddenApplicationPolicy -Configuration $prepared `
            -ApplicationIds $hiddenApplicationIds -Sentinel $disabledRoleSentinel)) {
        throw 'The prepared Portal production configuration did not retain the hidden-module policy.'
    }
    [IO.File]::Copy($configurationPath, $backupPath, $false)
    $changeMayHaveOccurred = $true
    [IO.File]::Replace($temporaryPath, $configurationPath, $null)
    $written = Read-PortalProductionConfiguration -Path $configurationPath
    if (-not (Test-HiddenApplicationPolicy -Configuration $written `
            -ApplicationIds $hiddenApplicationIds -Sentinel $disabledRoleSentinel)) {
        throw 'The active Portal production configuration did not retain the hidden-module policy.'
    }
    Restart-WebAppPool -Name $AppPoolName
    Wait-ForPortalVisibility -Uri $VerificationUri -HiddenIds $hiddenApplicationIds `
        -RequiredVisibleIds $requiredVisibleApplicationIds -TimeoutSeconds $HealthTimeoutSeconds
    Write-Host "Prior Portal configuration backup retained at: $backupPath"
    Write-Host 'PORTAL_PRODUCTION_MODULES_HIDDEN_AND_VERIFIED'
}
catch {
    $failure = $_.Exception.Message
    if (-not $changeMayHaveOccurred) { throw }
    $rollbackFailure = $null
    try {
        $restorePath = "$configurationPath.restore-$operationId.tmp"
        [IO.File]::WriteAllBytes($restorePath, $originalBytes)
        [IO.File]::Replace($restorePath, $configurationPath, $null)
        $restoredBytes = [IO.File]::ReadAllBytes($configurationPath)
        if ([Convert]::ToBase64String($restoredBytes) -cne [Convert]::ToBase64String($originalBytes)) {
            throw 'The restored Portal configuration does not exactly match the prior file.'
        }
        Restart-WebAppPool -Name $AppPoolName
    }
    catch { $rollbackFailure = $_.Exception.Message }
    if ($rollbackFailure) {
        throw "Portal visibility apply failed, and exact rollback could not be verified: $rollbackFailure. Original failure: $failure"
    }
    throw "Portal visibility apply failed and the exact prior configuration was restored. $failure"
}
finally {
    foreach ($transientPath in @($temporaryPath, "$configurationPath.restore-$operationId.tmp")) {
        if (Test-Path -LiteralPath $transientPath -PathType Leaf) {
            Remove-Item -LiteralPath $transientPath -Force -ErrorAction SilentlyContinue
        }
    }
}
