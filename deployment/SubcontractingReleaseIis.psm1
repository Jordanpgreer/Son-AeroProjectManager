$ErrorActionPreference = 'Stop'
$script:SiteName = 'SmallBusinessSubcontracting'
$script:HostName = 'subcontracting.hub.son4l.local'
Import-Module (Join-Path $PSScriptRoot 'SubcontractingProductionConfiguration.psm1')
Import-Module (Join-Path $PSScriptRoot 'SubcontractingReleaseFiles.psm1')
Import-Module (Join-Path $PSScriptRoot 'HubProductionHttps.Common.psm1')

function Assert-SubcontractingIisEnvironment {
    param($Manager, [bool]$Existing)
    foreach ($scope in @('Machine','Process')) {
        foreach ($entry in [Environment]::GetEnvironmentVariables($scope).GetEnumerator()) {
            Assert-SubcontractingEnvironmentVariable -Name $entry.Key -Value ([string]$entry.Value) -Label "$scope environment"
        }
    }
    $configuration = $Manager.GetApplicationHostConfiguration()
    $pools = $configuration.GetSection('system.applicationHost/applicationPools')
    $elements = @($pools.GetChildElement('applicationPoolDefaults'))
    if ($Existing) { $elements += @($pools.GetCollection() | Where-Object { $_.GetAttributeValue('name') -ieq $script:SiteName }) }
    $elements += if ($Existing) { $configuration.GetSection('system.webServer/aspNetCore', $script:SiteName) }
        else { $configuration.GetSection('system.webServer/aspNetCore') }
    foreach ($element in $elements) {
        foreach ($entry in @($element.GetCollection('environmentVariables'))) {
            Assert-SubcontractingEnvironmentVariable -Name ([string]$entry.GetAttributeValue('name')) `
                -Value ([string]$entry.GetAttributeValue('value')) -Label 'Effective IIS configuration'
        }
    }
}

function Assert-SubcontractingBindingConflicts {
    param([object[]]$Bindings, [string]$Thumbprint, [bool]$FirstInstall)
    $target = [pscustomobject]@{ Site=$script:SiteName; HostName=$script:HostName; HttpPort=5180 }
    Assert-HubProductionBindingAvailability -Snapshot $Bindings -Applications @($target) -Thumbprint $Thumbprint
    foreach ($binding in $Bindings) {
        $parts = Split-HubBindingInformation $binding.BindingInformation
        if (($parts.Port -eq 5180 -or $parts.HostName.TrimEnd('.') -ieq $script:HostName) -and
            ($binding.Site -ine $script:SiteName -or $FirstInstall)) {
            throw "A conflicting binding already reserves Subcontracting's hostname or port on '$($binding.Site)'."
        }
    }
}

function Get-SubcontractingIisBoundary {
    param([string]$Thumbprint, [switch]$AllowAbsent, [switch]$SkipEnvironment)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$script:SiteName]
        $pool = $manager.ApplicationPools[$script:SiteName]
        if (-not $SkipEnvironment) { Assert-SubcontractingIisEnvironment $manager ([bool]$site) }
        if (-not $site -and -not $pool -and $AllowAbsent) { return $null }
        if (-not $site -or -not $pool) { throw 'Site/pool must both exist or both be absent; investigate partial installation.' }
        if ($site.Applications.Count -ne 1 -or $site.Applications['/'].VirtualDirectories.Count -ne 1 -or
            $site.Applications['/'].ApplicationPoolName -ine $script:SiteName -or
            $pool.ProcessModel.IdentityType -ne [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity -or
            $pool.ProcessModel.MaxProcesses -ne 1 -or $pool.ManagedRuntimeVersion -ne '' -or
            $pool.Enable32BitAppOnWin64 -or -not $pool.Recycling.DisallowOverlappingRotation) {
            throw 'Subcontracting IIS resource ownership or worker configuration differs from the dedicated-site contract.'
        }
        foreach ($other in $manager.Sites) {
            foreach ($application in $other.Applications) {
                if ($other.Name -ine $script:SiteName -and $application.ApplicationPoolName -ieq $script:SiteName) {
                    throw 'Subcontracting pool is shared by another application.'
                }
            }
        }
        $http = @($site.Bindings | Where-Object Protocol -EQ 'http')
        $https = @($site.Bindings | Where-Object Protocol -EQ 'https')
        if ($site.Bindings.Count -ne 2 -or $http.Count -ne 1 -or $http[0].BindingInformation -cne '*:5180:' -or
            $https.Count -ne 1 -or $https[0].BindingInformation -ine "*:443:$script:HostName" -or
            [int]$https[0].SslFlags -ne 1 -or $https[0].CertificateStoreName -ine 'My' -or
            (ConvertFrom-HubCertificateHash $https[0].CertificateHash) -ine $Thumbprint) {
            throw 'Subcontracting must use only HTTP5180 and the selected SNI443 certificate binding.'
        }
        $configuration = $manager.GetApplicationHostConfiguration()
        if (-not [bool]$configuration.GetSection('system.webServer/security/authentication/windowsAuthentication', $script:SiteName).GetAttributeValue('enabled') -or
            [bool]$configuration.GetSection('system.webServer/security/authentication/anonymousAuthentication', $script:SiteName).GetAttributeValue('enabled')) {
            throw 'Subcontracting must use Windows authentication with Anonymous disabled.'
        }
        $path = Get-SubcontractingFullPath $site.Applications['/'].VirtualDirectories['/'].PhysicalPath
        return [pscustomobject]@{ Path=$path; SiteState=[string]$site.State; PoolState=[string]$pool.State; SiteId=[long]$site.Id }
    }
    finally { $manager.Dispose() }
}

function Assert-SubcontractingCandidateIsolated {
    param([string]$Candidate, [string]$Source)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        foreach ($site in $manager.Sites) {
            foreach ($application in $site.Applications) {
                foreach ($directory in $application.VirtualDirectories) {
                    if ((Test-SubcontractingPathOverlap $Candidate $directory.PhysicalPath) -or
                        (Test-SubcontractingPathOverlap $Source $directory.PhysicalPath)) {
                        throw 'Candidate/package overlaps an active IIS physical directory.'
                    }
                }
            }
        }
    }
    finally { $manager.Dispose() }
}

function New-SubcontractingIisSite {
    param([string]$Candidate, [string]$Thumbprint, [long]$SiteId)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        if ($manager.Sites[$script:SiteName] -or $manager.ApplicationPools[$script:SiteName] -or
            @($manager.Sites | Where-Object Id -EQ $SiteId).Count -gt 0) { throw 'IIS changed after preview; target resources are no longer absent.' }
        $pool = $manager.ApplicationPools.Add($script:SiteName)
        $pool.AutoStart = $false
        $pool.ManagedRuntimeVersion = ''
        $pool.Enable32BitAppOnWin64 = $false
        $pool.ProcessModel.IdentityType = [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity
        $pool.ProcessModel.MaxProcesses = 1
        $pool.ProcessModel.IdleTimeout = [TimeSpan]::Zero
        $pool.Recycling.DisallowOverlappingRotation = $true
        $pool.StartMode = [Microsoft.Web.Administration.StartMode]::AlwaysRunning
        $envCollection = $pool.GetCollection('environmentVariables')
        $variable = $envCollection.CreateElement('add')
        $variable.SetAttributeValue('name','ASPNETCORE_ENVIRONMENT')
        $variable.SetAttributeValue('value','Production')
        $envCollection.Add($variable)
        $site = $manager.Sites.Add($script:SiteName, 'http', '*:5180:', $Candidate)
        $site.Id = $SiteId
        $site.ServerAutoStart = $false
        $site.Applications['/'].ApplicationPoolName = $script:SiteName
        $binding = $site.Bindings.Add("*:443:$script:HostName", (ConvertTo-HubCertificateHashBytes $Thumbprint), 'My')
        $binding.Protocol = 'https'
        $binding.SslFlags = [Microsoft.Web.Administration.SslFlags]::Sni
        $config = $manager.GetApplicationHostConfiguration()
        $config.GetSection('system.webServer/security/authentication/windowsAuthentication', $script:SiteName).SetAttributeValue('enabled',$true)
        $config.GetSection('system.webServer/security/authentication/anonymousAuthentication', $script:SiteName).SetAttributeValue('enabled',$false)
        $config.GetSection('system.webServer/security/requestFiltering', $script:SiteName).GetChildElement('requestLimits').SetAttributeValue('maxAllowedContentLength',[uint32]26214400)
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}

function Set-SubcontractingRuntimeState {
    param([ValidateSet('Started','Stopped')][string]$State)
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        $manager = New-Object Microsoft.Web.Administration.ServerManager
        try {
            $site = $manager.Sites[$script:SiteName]; $pool = $manager.ApplicationPools[$script:SiteName]
            if (-not $site -or -not $pool) { throw 'Subcontracting runtime resources are missing.' }
            if ([string]$site.State -eq $State -and [string]$pool.State -eq $State) { return }
            if ($State -eq 'Stopped') {
                if ([string]$site.State -eq 'Started') { $null = $site.Stop() }
                if ([string]$pool.State -eq 'Started') { $null = $pool.Stop() }
            }
            else {
                if ([string]$pool.State -eq 'Stopped') { $null = $pool.Start() }
                if ([string]$site.State -eq 'Stopped') { $null = $site.Start() }
            }
        }
        finally { $manager.Dispose() }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Subcontracting runtime did not become $State."
}

function Set-SubcontractingPhysicalPath {
    param([string]$Expected, [string]$Destination)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $directory = $manager.Sites[$script:SiteName].Applications['/'].VirtualDirectories['/']
        if ((Get-SubcontractingFullPath $directory.PhysicalPath) -ine $Expected) { throw 'Active IIS path changed concurrently.' }
        $directory.PhysicalPath = $Destination
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}

function Enable-SubcontractingAutoStart {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $manager.Sites[$script:SiteName].ServerAutoStart = $true
        $manager.ApplicationPools[$script:SiteName].AutoStart = $true
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}

function Remove-SubcontractingOwnedSite {
    param($State)
    $boundary = Get-SubcontractingIisBoundary -Thumbprint $State.Thumbprint -AllowAbsent -SkipEnvironment
    if (-not $boundary) { return }
    if ($boundary.Path -ine $State.Candidate -or $boundary.SiteId -ne $State.SiteId) { throw 'Refusing removal: resources differ from recorded transaction ownership.' }
    Set-SubcontractingRuntimeState -State Stopped
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$script:SiteName]
        if ($site.Id -ne $State.SiteId -or $site.Applications['/'].VirtualDirectories['/'].PhysicalPath -ine $State.Candidate) { throw 'IIS ownership changed during rollback.' }
        $manager.Sites.Remove($site)
        $manager.ApplicationPools.Remove($manager.ApplicationPools[$script:SiteName])
        $manager.GetApplicationHostConfiguration().RemoveLocationPath($script:SiteName)
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}

function Wait-SubcontractingHealth {
    param([int]$TimeoutSeconds=180)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $pending = @('http://SON-IIS2:5180/api/health','https://subcontracting.hub.son4l.local/api/health')
    do {
        foreach ($uri in @($pending)) {
            try {
                $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec 10 -MaximumRedirection 0
                if ($response.StatusCode -ne 200) { throw 'Non-200 health response.' }
                Assert-SubcontractingHealthBody ($response.Content | ConvertFrom-Json)
                $pending = @($pending | Where-Object { $_ -ne $uri })
            }
            catch { $lastFailure = $_.Exception.Message }
        }
        if ($pending.Count -eq 0) { return }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Subcontracting health failed on $($pending -join ', '): $lastFailure"
}

Export-ModuleMember -Function Assert-SubcontractingBindingConflicts, Get-SubcontractingIisBoundary,
    Assert-SubcontractingCandidateIsolated, New-SubcontractingIisSite, Set-SubcontractingRuntimeState,
    Set-SubcontractingPhysicalPath, Enable-SubcontractingAutoStart, Remove-SubcontractingOwnedSite, Wait-SubcontractingHealth
