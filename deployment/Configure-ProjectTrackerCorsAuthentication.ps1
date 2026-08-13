<#
    Enables the IIS authentication boundary required by browser CORS preflight on the direct
    Project Tracker site. Anonymous OPTIONS must reach ASP.NET Core's CORS middleware, while every
    protected /api endpoint still challenges through Windows Authentication and authorization.

    The same-origin Portal gateway deliberately remains Windows-only. This direct-site setting is
    topology-neutral and must remain enabled for permanent HTTPS, the 61xx pilot, and HTTP rollback.

    Because this bootstrap runs before the application-config transaction, it reads the active
    Project Tracker Cors.HubOrigins array, rejects origins outside the retained/pilot/permanent
    allowlist, and verifies only the approved origins that are configured at that point. The later
    application-config transaction remains responsible for verifying every newly installed origin.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('SON-IIS2')]
    [string]$ExpectedComputerName = 'SON-IIS2',
    [ValidateSet('ProjectTracker')]
    [string]$ProjectTrackerSiteName = 'ProjectTracker',
    [ValidateSet('SonAeroPortal')]
    [string]$PortalSiteName = 'SonAeroPortal',
    [ValidateSet('/project-tracker-api')]
    [string]$GatewayPath = '/project-tracker-api',
    [ValidateRange(10, 300)]
    [int]$HealthTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated Windows PowerShell session.'
    }
}

function Get-DeploymentAccountName {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity -or $identity.IsSystem -or
        $identity.Name -ieq 'NT AUTHORITY\SYSTEM') {
        throw 'This repair rejects Local System. Run it interactively as an authorized SON4L domain user.'
    }
    $accountName = [string]$identity.Name
    if ([string]::IsNullOrWhiteSpace($accountName)) {
        throw 'The current Windows identity could not be determined for credentialed verification.'
    }
    return $accountName
}

function Import-IisAdministration {
    $assemblyPath = Join-Path $env:WINDIR 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "IIS administration assembly was not found at '$assemblyPath'."
    }
    if (-not ('Microsoft.Web.Administration.ServerManager' -as [type])) {
        Add-Type -Path $assemblyPath -ErrorAction Stop
    }
}

function Get-AuthenticationState {
    param(
        [Parameter(Mandatory = $true)]$Manager,
        [Parameter(Mandatory = $true)][string]$Location
    )
    $configuration = $Manager.GetApplicationHostConfiguration()
    return [pscustomobject]@{
        AnonymousEnabled = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/anonymousAuthentication',
            $Location).GetAttributeValue('enabled')
        WindowsEnabled = [bool]$configuration.GetSection(
            'system.webServer/security/authentication/windowsAuthentication',
            $Location).GetAttributeValue('enabled')
    }
}

function Set-AuthenticationState {
    param(
        [Parameter(Mandatory = $true)]$Manager,
        [Parameter(Mandatory = $true)][string]$Location,
        [Parameter(Mandatory = $true)][bool]$AnonymousEnabled,
        [Parameter(Mandatory = $true)][bool]$WindowsEnabled
    )
    $configuration = $Manager.GetApplicationHostConfiguration()
    $configuration.GetSection(
        'system.webServer/security/authentication/anonymousAuthentication',
        $Location).SetAttributeValue('enabled', $AnonymousEnabled)
    $configuration.GetSection(
        'system.webServer/security/authentication/windowsAuthentication',
        $Location).SetAttributeValue('enabled', $WindowsEnabled)
}

function Assert-AuthenticationState {
    param(
        [Parameter(Mandatory = $true)]$Manager,
        [Parameter(Mandatory = $true)][string]$Location,
        [Parameter(Mandatory = $true)][bool]$AnonymousEnabled,
        [Parameter(Mandatory = $true)][bool]$WindowsEnabled
    )
    $actual = Get-AuthenticationState -Manager $Manager -Location $Location
    if ($actual.AnonymousEnabled -ne $AnonymousEnabled -or
        $actual.WindowsEnabled -ne $WindowsEnabled) {
        throw "IIS authentication at '$Location' is not Anonymous=$AnonymousEnabled, Windows=$WindowsEnabled."
    }
}

function Assert-CorsPreflight {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Origin
    )
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Method Options -Uri $Uri -TimeoutSec 10 -Headers @{
            Origin = $Origin
            'Access-Control-Request-Method' = 'POST'
            'Access-Control-Request-Headers' = 'content-type'
        }
    }
    catch { throw "Anonymous CORS preflight failed for '$Origin' at '$Uri': $($_.Exception.Message)" }
    $methods = @(([string]$response.Headers['Access-Control-Allow-Methods']) -split '\s*,\s*')
    $headers = @(([string]$response.Headers['Access-Control-Allow-Headers']) -split '\s*,\s*')
    if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 300 -or
        [string]$response.Headers['Access-Control-Allow-Origin'] -cne $Origin -or
        [string]$response.Headers['Access-Control-Allow-Credentials'] -ine 'true' -or
        'POST' -notin $methods -or 'content-type' -notin $headers) {
        throw "CORS preflight did not allow the exact credentialed POST/content-type request for '$Origin' at '$Uri'."
    }
}

function Assert-AnonymousApiDenied {
    param([Parameter(Mandatory = $true)][string]$Uri)
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Method Get -Uri $Uri -TimeoutSec 10
        $statusCode = [int]$response.StatusCode
    }
    catch {
        if ($null -eq $_.Exception.Response) {
            throw "Anonymous authorization probe failed at '$Uri': $($_.Exception.Message)"
        }
        $statusCode = [int]$_.Exception.Response.StatusCode
    }
    if ($statusCode -ne 401) {
        throw "Anonymous Project Tracker /api/me must return HTTP 401; received $statusCode at '$Uri'."
    }
}

function Assert-CredentialedIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$ExpectedAccountName
    )
    try {
        $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Method Get -Uri $Uri -TimeoutSec 10
        $payload = $response.Content | ConvertFrom-Json
    }
    catch { throw "Credentialed Windows identity probe failed at '$Uri': $($_.Exception.Message)" }
    if ([int]$response.StatusCode -ne 200 -or
        [string]$payload.accountName -ine $ExpectedAccountName) {
        throw "Credentialed Project Tracker /api/me at '$Uri' returned accountName '$($payload.accountName)', not current Windows identity '$ExpectedAccountName'."
    }
}

function ConvertTo-ApprovedTrackerCorsProbe {
    param([Parameter(Mandatory = $true)][string]$Origin)

    $candidate = $Origin.Trim()
    switch -Regex ($candidate) {
        '(?i)^http://son-iis2:5140/?$' {
            return [pscustomobject]@{
                Origin = 'http://son-iis2:5140'
                Uri = "http://$ExpectedComputerName`:5135/api/me"
            }
        }
        '(?i)^https://son-iis2:6140/?$' {
            return [pscustomobject]@{
                Origin = 'https://son-iis2:6140'
                Uri = "https://$ExpectedComputerName`:6135/api/me"
            }
        }
        '(?i)^https://hub\.son4l\.local/?$' {
            return [pscustomobject]@{
                Origin = 'https://hub.son4l.local'
                Uri = 'https://projects.hub.son4l.local/api/me'
            }
        }
        default {
            throw "Project Tracker Cors.HubOrigins contains unapproved origin '$Origin'."
        }
    }
}

function Get-ConfiguredTrackerCorsProbes {
    param([Parameter(Mandatory = $true)][string]$ConfigurationPath)

    if (-not (Test-Path -LiteralPath $ConfigurationPath -PathType Leaf)) {
        throw "Project Tracker production configuration was not found at '$ConfigurationPath'."
    }
    try { $configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json }
    catch { throw "Project Tracker production configuration is not valid JSON: $($_.Exception.Message)" }

    $corsProperty = @($configuration.PSObject.Properties | Where-Object { $_.Name -ceq 'Cors' })
    if ($corsProperty.Count -ne 1 -or $null -eq $corsProperty[0].Value) {
        throw 'Project Tracker production configuration must contain one Cors object.'
    }
    $originsProperty = @($corsProperty[0].Value.PSObject.Properties | Where-Object {
        $_.Name -ceq 'HubOrigins'
    })
    if ($originsProperty.Count -ne 1 -or -not ($originsProperty[0].Value -is [Array])) {
        throw 'Project Tracker Cors.HubOrigins must be a JSON array.'
    }
    $origins = @($originsProperty[0].Value)
    if ($origins.Count -eq 0) {
        throw 'Project Tracker Cors.HubOrigins must contain at least one approved origin.'
    }

    $seen = New-Object 'Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    $probes = @()
    foreach ($origin in $origins) {
        if (-not ($origin -is [string]) -or [string]::IsNullOrWhiteSpace([string]$origin)) {
            throw 'Every Project Tracker Cors.HubOrigins entry must be a non-empty string.'
        }
        $probe = ConvertTo-ApprovedTrackerCorsProbe -Origin ([string]$origin)
        if (-not $seen.Add($probe.Origin)) {
            throw "Project Tracker Cors.HubOrigins contains duplicate origin '$($probe.Origin)'."
        }
        $probes += $probe
    }
    return $probes
}

function Assert-DirectSiteBoundary {
    param(
        [Parameter(Mandatory = $true)][object[]]$CorsProbes,
        [Parameter(Mandatory = $true)][string]$ExpectedAccountName
    )
    foreach ($probe in $CorsProbes) {
        Assert-CorsPreflight -Uri $probe.Uri -Origin $probe.Origin
    }

    # Authentication is site-scoped, so prove both retained direct bindings independently of which
    # Portal origins are already present. The later application-config transaction may add the
    # permanent origin only after this bootstrap has established the anonymous-preflight boundary.
    $authorizationUris = @(
        "http://$ExpectedComputerName`:5135/api/me",
        "https://$ExpectedComputerName`:6135/api/me"
    )
    if (@($CorsProbes | Where-Object {
        $_.Origin -ceq 'https://hub.son4l.local'
    }).Count -eq 1) {
        $authorizationUris += 'https://projects.hub.son4l.local/api/me'
    }
    foreach ($uri in $authorizationUris) {
        Assert-AnonymousApiDenied -Uri $uri
        Assert-CredentialedIdentity -Uri $uri -ExpectedAccountName $expectedAccountName
    }
}

function Wait-DirectSiteBoundary {
    param(
        [Parameter(Mandatory = $true)][object[]]$CorsProbes,
        [Parameter(Mandatory = $true)][string]$ExpectedAccountName
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
    $lastFailure = $null
    do {
        try {
            Assert-DirectSiteBoundary -CorsProbes $CorsProbes `
                -ExpectedAccountName $ExpectedAccountName
            return
        }
        catch {
            $lastFailure = $_.Exception.Message
            if ([DateTime]::UtcNow -ge $deadline) { break }
            Start-Sleep -Milliseconds 750
        }
    } while ($true)
    throw "Project Tracker did not reach the verified CORS/authentication boundary within $HealthTimeoutSeconds seconds. $lastFailure"
}

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is restricted to $ExpectedComputerName; current computer is '$env:COMPUTERNAME'."
}
if ($GatewayPath -notmatch '^/[a-z0-9][a-z0-9-]{0,62}$') {
    throw 'GatewayPath must be one lowercase root application segment.'
}
$deploymentAccountName = Get-DeploymentAccountName
if (-not $WhatIfPreference) { Assert-Administrator }
Import-IisAdministration

$manager = New-Object Microsoft.Web.Administration.ServerManager
try {
    if ($null -eq $manager.Sites[$ProjectTrackerSiteName]) {
        throw "Required IIS site '$ProjectTrackerSiteName' is missing."
    }
    $projectTrackerRoot = [Environment]::ExpandEnvironmentVariables(
        [string]$manager.Sites[$ProjectTrackerSiteName].Applications['/'].VirtualDirectories['/'].PhysicalPath)
    if ([string]::IsNullOrWhiteSpace($projectTrackerRoot) -or
        -not [IO.Path]::IsPathRooted($projectTrackerRoot)) {
        throw "Required IIS site '$ProjectTrackerSiteName' has an invalid physical path."
    }
    $portalSite = $manager.Sites[$PortalSiteName]
    if ($null -eq $portalSite -or $null -eq $portalSite.Applications[$GatewayPath]) {
        throw "Required same-origin gateway '$PortalSiteName$GatewayPath' is missing."
    }
    Assert-AuthenticationState -Manager $manager -Location "$PortalSiteName$GatewayPath" `
        -AnonymousEnabled $false -WindowsEnabled $true
    $prior = Get-AuthenticationState -Manager $manager -Location $ProjectTrackerSiteName
}
finally { $manager.Dispose() }
$trackerConfigurationPath = Join-Path $projectTrackerRoot 'appsettings.Production.json'
$configuredCorsProbes = @(Get-ConfiguredTrackerCorsProbes -ConfigurationPath $trackerConfigurationPath)

if ($prior.AnonymousEnabled -and $prior.WindowsEnabled) {
    Wait-DirectSiteBoundary -CorsProbes $configuredCorsProbes `
        -ExpectedAccountName $deploymentAccountName
    if ($WhatIfPreference) {
        Write-Output 'WHATIF_READY_PROJECT_TRACKER_CORS_AUTHENTICATION: the topology-neutral boundary is already configured and verified; nothing was changed.'
    }
    else { Write-Output 'PROJECT_TRACKER_CORS_AUTHENTICATION_ALREADY_CONFIGURED_AND_VERIFIED' }
    exit 0
}

if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName/$ProjectTrackerSiteName",
        'Enable Anonymous and Windows Authentication for CORS preflight plus protected API challenge')) {
    if ($WhatIfPreference) {
        Write-Output 'WHATIF_READY_PROJECT_TRACKER_CORS_AUTHENTICATION: IIS ownership and gateway authentication passed; nothing was changed.'
    }
    else { Write-Output 'PROJECT_TRACKER_CORS_AUTHENTICATION_CANCELLED' }
    exit 0
}

$changeMayHaveOccurred = $false
try {
    $applyManager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $changeMayHaveOccurred = $true
        Set-AuthenticationState -Manager $applyManager -Location $ProjectTrackerSiteName `
            -AnonymousEnabled $true -WindowsEnabled $true
        $applyManager.CommitChanges()
    }
    finally { $applyManager.Dispose() }

    $verifyManager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        Assert-AuthenticationState -Manager $verifyManager -Location $ProjectTrackerSiteName `
            -AnonymousEnabled $true -WindowsEnabled $true
        Assert-AuthenticationState -Manager $verifyManager -Location "$PortalSiteName$GatewayPath" `
            -AnonymousEnabled $false -WindowsEnabled $true
    }
    finally { $verifyManager.Dispose() }
    Wait-DirectSiteBoundary -CorsProbes $configuredCorsProbes `
        -ExpectedAccountName $deploymentAccountName
}
catch {
    $failure = $_.Exception.Message
    if (-not $changeMayHaveOccurred) {
        throw "Project Tracker CORS authentication failed before IIS authentication could be changed. $failure"
    }
    $rollbackFailure = $null
    try {
        $rollbackManager = New-Object Microsoft.Web.Administration.ServerManager
        try {
            Set-AuthenticationState -Manager $rollbackManager -Location $ProjectTrackerSiteName `
                -AnonymousEnabled $prior.AnonymousEnabled -WindowsEnabled $prior.WindowsEnabled
            $rollbackManager.CommitChanges()
        }
        finally { $rollbackManager.Dispose() }

        $rollbackVerifyManager = New-Object Microsoft.Web.Administration.ServerManager
        try {
            Assert-AuthenticationState -Manager $rollbackVerifyManager `
                -Location $ProjectTrackerSiteName `
                -AnonymousEnabled $prior.AnonymousEnabled -WindowsEnabled $prior.WindowsEnabled
            Assert-AuthenticationState -Manager $rollbackVerifyManager `
                -Location "$PortalSiteName$GatewayPath" `
                -AnonymousEnabled $false -WindowsEnabled $true
        }
        finally { $rollbackVerifyManager.Dispose() }
    }
    catch { $rollbackFailure = $_.Exception.Message }
    if ($null -ne $rollbackFailure) {
        throw "Project Tracker CORS authentication failed, and automatic rollback could not be verified. Original failure: $failure Rollback failure: $rollbackFailure"
    }
    throw "Project Tracker CORS authentication failed and the prior IIS authentication state was restored and verified. $failure"
}

Write-Output 'PROJECT_TRACKER_CORS_AUTHENTICATION_CONFIGURED_AND_VERIFIED'
