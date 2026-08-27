Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:BlockedQualityEnvironmentVariables = @(
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
$script:EnvironmentSelectorVariables = @('ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT')

function Get-RetainedQualityBooleanAttribute {
    param(
        [Parameter(Mandatory = $true)]$Element,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Label
    )

    try {
        $value = $Element.GetAttributeValue($Name)
    }
    catch {
        throw "Unable to read $Label attribute '$Name': $($_.Exception.Message)"
    }
    if ($null -eq $value) {
        throw "$Label attribute '$Name' is missing; retained-boundary evidence is incomplete."
    }
    if ($value -isnot [bool]) {
        throw "$Label attribute '$Name' must be Boolean, not '$($value.GetType().FullName)'."
    }
    return [bool]$value
}

function Get-RetainedQualityEnvironmentHash {
    param(
        [Parameter(Mandatory = $true)]$Collection,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $seenNames = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [string[]]$records = @(
        foreach ($item in $Collection) {
            $name = [string]$item.GetAttributeValue('name')
            $value = [string]$item.GetAttributeValue('value')
            if ([string]::IsNullOrWhiteSpace($name)) {
                throw "$Label contains an environment variable without a name."
            }
            if (-not $seenNames.Add($name)) {
                throw "$Label contains duplicate environment variable '$name'."
            }
            if ($name -in $script:BlockedQualityEnvironmentVariables) {
                throw "$Label contains blocked Quality database override '$name'."
            }
            if ($name -in $script:EnvironmentSelectorVariables -and $value -cne 'Production') {
                throw "$Label sets '$name' to a non-Production environment."
            }
            # Length prefixes prevent delimiter/newline ambiguity. Values are hashed and never
            # returned or written to deployment output because IIS variables may contain secrets.
            '{0}:{1}{2}:{3}' -f $name.Length, $name, $value.Length, $value
        }
    )
    [Array]::Sort($records, [StringComparer]::Ordinal)
    $canonical = [string]::Join("`n", $records)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($canonical)
        return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Assert-NoRetainedQualityMachineOverrides {
    foreach ($name in $script:BlockedQualityEnvironmentVariables) {
        $value = [Environment]::GetEnvironmentVariable(
            $name, [EnvironmentVariableTarget]::Machine)
        if ($null -ne $value) {
            throw "Machine environment contains blocked Quality database override '$name'."
        }
    }
    foreach ($name in $script:EnvironmentSelectorVariables) {
        $value = [Environment]::GetEnvironmentVariable(
            $name, [EnvironmentVariableTarget]::Machine)
        if ($null -ne $value -and $value -cne 'Production') {
            throw "Machine environment sets '$name' to a non-Production environment."
        }
    }
}

function Get-RetainedQualityBindingRecords {
    param([Parameter(Mandatory = $true)]$Site)

    [string[]]$records = @(
        foreach ($binding in $Site.Bindings) {
            $certificateHash = ''
            if ($null -ne $binding.CertificateHash -and $binding.CertificateHash.Length -gt 0) {
                $certificateHash = ([BitConverter]::ToString($binding.CertificateHash)).Replace('-', '')
            }
            '{0}|{1}|{2}|{3}|{4}' -f @(
                [string]$binding.Protocol,
                [string]$binding.BindingInformation,
                [string]$binding.CertificateStoreName,
                [string]$binding.SslFlags,
                $certificateHash
            )
        }
    )
    [Array]::Sort($records, [StringComparer]::Ordinal)
    return $records
}

function Get-RetainedQualityAclRecords {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$MainDll
    )

    $criticalPaths = [ordered]@{
        Root = $RootPath
        ProductionConfiguration = Join-Path $RootPath 'appsettings.Production.json'
        WebConfiguration = Join-Path $RootPath 'web.config'
        MainAssembly = Join-Path $RootPath $MainDll
    }
    [string[]]$records = @(
        foreach ($entry in $criticalPaths.GetEnumerator()) {
            if (-not (Test-Path -LiteralPath $entry.Value)) {
                throw "Retained Quality critical path is missing: '$($entry.Value)'."
            }
            $acl = Get-Acl -LiteralPath $entry.Value
            '{0}|{1}' -f $entry.Key, [string]$acl.Sddl
        }
    )
    return $records
}

function Get-HubRetainedQualityBoundarySnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$SiteName,
        [Parameter(Mandatory = $true)][string]$PoolName,
        [Parameter(Mandatory = $true)][string]$MainDll,
        [Parameter(Mandatory = $true)][string]$HealthUri
    )

    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$SiteName]
        $pool = $manager.ApplicationPools[$PoolName]
        if ($null -eq $site -or $null -eq $pool) {
            throw "Retained Quality site or application pool is missing."
        }
        $rootApplication = $site.Applications['/']
        if ($null -eq $rootApplication) {
            throw "Retained Quality site '$SiteName' has no root application."
        }
        if ([string]$rootApplication.ApplicationPoolName -ine $PoolName) {
            throw "Retained Quality root application must use pool '$PoolName'."
        }
        $rootVirtualDirectory = $rootApplication.VirtualDirectories['/']
        if ($null -eq $rootVirtualDirectory) {
            throw "Retained Quality site '$SiteName' has no root virtual directory."
        }
        $qualityPath = [IO.Path]::GetFullPath(
            [Environment]::ExpandEnvironmentVariables([string]$rootVirtualDirectory.PhysicalPath)).TrimEnd('\')
        if (-not (Test-Path -LiteralPath $qualityPath -PathType Container)) {
            throw "Retained Quality physical path does not exist: '$qualityPath'."
        }

        $configuration = $manager.GetApplicationHostConfiguration()
        $anonymousEnabled = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/anonymousAuthentication',
            $SiteName).GetAttributeValue('enabled')
        $windowsEnabled = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/windowsAuthentication',
            $SiteName).GetAttributeValue('enabled')
        if ($anonymousEnabled -or -not $windowsEnabled) {
            throw 'Retained Quality authentication must remain Anonymous=False and Windows=True.'
        }

        $aspNetCoreSection = $configuration.GetSection('system.webServer/aspNetCore', $SiteName)
        $aspNetCoreEnvironmentHash = Get-RetainedQualityEnvironmentHash `
            -Collection $aspNetCoreSection.GetCollection('environmentVariables') `
            -Label 'Retained Quality aspNetCore configuration'

        $applicationPoolsSection = $configuration.GetSection('system.applicationHost/applicationPools')
        $poolConfigurationElement = $null
        foreach ($candidatePoolElement in $applicationPoolsSection.GetCollection()) {
            if ([string]$candidatePoolElement.GetAttributeValue('name') -ieq $PoolName) {
                $poolConfigurationElement = $candidatePoolElement
                break
            }
        }
        if ($null -eq $poolConfigurationElement) {
            throw "Retained Quality application-pool configuration '$PoolName' is missing."
        }
        $poolEnvironmentHash = Get-RetainedQualityEnvironmentHash `
            -Collection $poolConfigurationElement.GetCollection('environmentVariables') `
            -Label 'Retained Quality application-pool configuration'
        $poolDefaultsElement = $applicationPoolsSection.GetChildElement('applicationPoolDefaults')
        if ($null -eq $poolDefaultsElement) {
            throw 'IIS application-pool defaults configuration is missing.'
        }
        $poolDefaultsEnvironmentHash = Get-RetainedQualityEnvironmentHash `
            -Collection $poolDefaultsElement.GetCollection('environmentVariables') `
            -Label 'IIS application-pool defaults configuration'
        Assert-NoRetainedQualityMachineOverrides

        $productionConfigurationPath = Join-Path $qualityPath 'appsettings.Production.json'
        if (-not (Test-Path -LiteralPath $productionConfigurationPath -PathType Leaf)) {
            throw "Retained Quality Production configuration is missing: '$productionConfigurationPath'."
        }

        [string[]]$poolConfiguration = @(
            "ManagedRuntimeVersion=$([string]$pool.ManagedRuntimeVersion)",
            "ManagedPipelineMode=$([string]$pool.ManagedPipelineMode)",
            "AutoStart=$([bool]$pool.AutoStart)",
            "StartMode=$([string]$pool.StartMode)",
            "Enable32BitAppOnWin64=$([bool]$pool.Enable32BitAppOnWin64)",
            "QueueLength=$([uint32]$pool.QueueLength)",
            "IdentityType=$([string]$pool.ProcessModel.IdentityType)",
            "UserName=$([string]$pool.ProcessModel.UserName)",
            "LoadUserProfile=$([bool]$pool.ProcessModel.LoadUserProfile)",
            "IdleTimeoutSeconds=$([int64]$pool.ProcessModel.IdleTimeout.TotalSeconds)",
            "MaxProcesses=$([uint32]$pool.ProcessModel.MaxProcesses)",
            "PingEnabled=$([bool]$pool.ProcessModel.PingingEnabled)",
            "PingIntervalSeconds=$([int64]$pool.ProcessModel.PingInterval.TotalSeconds)",
            "PingResponseTimeSeconds=$([int64]$pool.ProcessModel.PingResponseTime.TotalSeconds)",
            "StartupTimeLimitSeconds=$([int64]$pool.ProcessModel.StartupTimeLimit.TotalSeconds)",
            "ShutdownTimeLimitSeconds=$([int64]$pool.ProcessModel.ShutdownTimeLimit.TotalSeconds)",
            "RapidFailProtection=$([bool]$pool.Failure.RapidFailProtection)",
            "RapidFailProtectionIntervalSeconds=$([int64]$pool.Failure.RapidFailProtectionInterval.TotalSeconds)",
            "RapidFailProtectionMaxCrashes=$([uint32]$pool.Failure.RapidFailProtectionMaxCrashes)",
            "PeriodicRestartSeconds=$([int64]$pool.Recycling.PeriodicRestart.Time.TotalSeconds)",
            "DisallowOverlappingRotation=$([bool]$pool.Recycling.DisallowOverlappingRotation)",
            "DisallowRotationOnConfigChange=$([bool]$pool.Recycling.DisallowRotationOnConfigChange)"
        )

        $siteState = [string](Get-WebsiteState -Name $SiteName).Value
        $poolState = [string](Get-WebAppPoolState -Name $PoolName).Value
        if ($siteState -cne 'Started' -or $poolState -cne 'Started') {
            throw "Retained Quality site and pool must both be Started."
        }

        try {
            $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $HealthUri -TimeoutSec 10
        }
        catch {
            throw "Retained Quality health verification failed at '$HealthUri'."
        }
        if ([int]$response.StatusCode -ne 200) {
            throw "Retained Quality health verification did not return HTTP 200 at '$HealthUri'."
        }

        $rootPreloadEnabled = Get-RetainedQualityBooleanAttribute `
            -Element $rootApplication `
            -Name 'preloadEnabled' `
            -Label 'Retained Quality root application'

        return [pscustomobject]@{
            QualityPath = $qualityPath
            SiteId = [uint64]$site.Id
            SiteState = $siteState
            SiteServerAutoStart = [bool]$site.ServerAutoStart
            PoolState = $poolState
            RootApplicationPool = [string]$rootApplication.ApplicationPoolName
            RootEnabledProtocols = [string]$rootApplication.EnabledProtocols
            RootPreloadEnabled = $rootPreloadEnabled
            Bindings = @(Get-RetainedQualityBindingRecords -Site $site)
            AnonymousEnabled = $anonymousEnabled
            WindowsEnabled = $windowsEnabled
            AspNetCoreEnvironmentHash = $aspNetCoreEnvironmentHash
            PoolEnvironmentHash = $poolEnvironmentHash
            PoolDefaultsEnvironmentHash = $poolDefaultsEnvironmentHash
            MachineOverrideState = 'Absent'
            PoolConfiguration = @($poolConfiguration)
            ProductionConfigurationHash = [string](
                Get-FileHash -Algorithm SHA256 -LiteralPath $productionConfigurationPath).Hash
            CriticalAcls = @(Get-RetainedQualityAclRecords -RootPath $qualityPath -MainDll $MainDll)
            HealthStatus = 'HTTP 200'
        }
    }
    finally {
        $manager.Dispose()
    }
}

function Test-RetainedQualitySequenceEqual {
    param(
        [AllowNull()][object[]]$Expected,
        [AllowNull()][object[]]$Actual
    )

    $expectedValues = @($Expected)
    $actualValues = @($Actual)
    if ($expectedValues.Count -ne $actualValues.Count) { return $false }
    for ($index = 0; $index -lt $expectedValues.Count; $index++) {
        if ([string]$expectedValues[$index] -cne [string]$actualValues[$index]) { return $false }
    }
    return $true
}

function Assert-HubRetainedQualityBoundaryUnchanged {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$ExpectedSnapshot,
        [Parameter(Mandatory = $true)][string]$SiteName,
        [Parameter(Mandatory = $true)][string]$PoolName,
        [Parameter(Mandatory = $true)][string]$MainDll,
        [Parameter(Mandatory = $true)][string]$HealthUri,
        [Parameter(Mandatory = $true)][string]$Phase
    )

    $actualSnapshot = Get-HubRetainedQualityBoundarySnapshot `
        -SiteName $SiteName `
        -PoolName $PoolName `
        -MainDll $MainDll `
        -HealthUri $HealthUri
    $changedFields = New-Object System.Collections.Generic.List[string]
    foreach ($field in @(
        'QualityPath',
        'SiteId',
        'SiteState',
        'SiteServerAutoStart',
        'PoolState',
        'RootApplicationPool',
        'RootEnabledProtocols',
        'RootPreloadEnabled',
        'AnonymousEnabled',
        'WindowsEnabled',
        'AspNetCoreEnvironmentHash',
        'PoolEnvironmentHash',
        'PoolDefaultsEnvironmentHash',
        'MachineOverrideState',
        'ProductionConfigurationHash',
        'HealthStatus'
    )) {
        if ([string]$ExpectedSnapshot.$field -cne [string]$actualSnapshot.$field) {
            $changedFields.Add($field)
        }
    }
    foreach ($field in @('Bindings', 'PoolConfiguration', 'CriticalAcls')) {
        if (-not (Test-RetainedQualitySequenceEqual `
                -Expected @($ExpectedSnapshot.$field) `
                -Actual @($actualSnapshot.$field))) {
            $changedFields.Add($field)
        }
    }
    if ($changedFields.Count -gt 0) {
        throw "Retained Quality boundary changed during $Phase. Changed fields: $($changedFields -join ', ')."
    }
}

Export-ModuleMember -Function @(
    'Get-HubRetainedQualityBoundarySnapshot',
    'Assert-HubRetainedQualityBoundaryUnchanged'
)
