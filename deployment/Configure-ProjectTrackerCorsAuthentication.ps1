<#
    Enables the IIS authentication boundary required by browser CORS preflight on the direct
    Project Tracker site. Anonymous OPTIONS must reach ASP.NET Core's CORS middleware, while every
    protected /api endpoint still challenges through Windows Authentication and authorization.

    The same-origin Portal gateway deliberately remains Windows-only. This direct-site setting is
    topology-neutral and must remain enabled for permanent HTTPS, the 61xx pilot, and HTTP rollback.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('SON-IIS2')]
    [string]$ExpectedComputerName = 'SON-IIS2',
    [string]$ProjectTrackerSiteName = 'ProjectTracker',
    [string]$PortalSiteName = 'SonAeroPortal',
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

function Assert-DirectSiteBoundary {
    $expectedAccountName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ([string]::IsNullOrWhiteSpace($expectedAccountName)) {
        throw 'The current Windows identity could not be determined for credentialed verification.'
    }
    $probes = @(
        [pscustomobject]@{
            Uri = "https://$ExpectedComputerName`:6135/api/me"
            Origin = 'https://son-iis2:6140'
        },
        [pscustomobject]@{
            Uri = "http://$ExpectedComputerName`:5135/api/me"
            Origin = 'http://son-iis2:5140'
        }
    )
    foreach ($probe in $probes) {
        Assert-CorsPreflight -Uri $probe.Uri -Origin $probe.Origin
        Assert-AnonymousApiDenied -Uri $probe.Uri
        Assert-CredentialedIdentity -Uri $probe.Uri -ExpectedAccountName $expectedAccountName
    }
}

function Wait-DirectSiteBoundary {
    $deadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
    $lastFailure = $null
    do {
        try {
            Assert-DirectSiteBoundary
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
if (-not $WhatIfPreference) { Assert-Administrator }
Import-IisAdministration

$manager = New-Object Microsoft.Web.Administration.ServerManager
try {
    if ($null -eq $manager.Sites[$ProjectTrackerSiteName]) {
        throw "Required IIS site '$ProjectTrackerSiteName' is missing."
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

if ($prior.AnonymousEnabled -and $prior.WindowsEnabled) {
    Wait-DirectSiteBoundary
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

$applyManager = New-Object Microsoft.Web.Administration.ServerManager
try {
    Set-AuthenticationState -Manager $applyManager -Location $ProjectTrackerSiteName `
        -AnonymousEnabled $true -WindowsEnabled $true
    $applyManager.CommitChanges()
}
finally { $applyManager.Dispose() }

try {
    $verifyManager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        Assert-AuthenticationState -Manager $verifyManager -Location $ProjectTrackerSiteName `
            -AnonymousEnabled $true -WindowsEnabled $true
        Assert-AuthenticationState -Manager $verifyManager -Location "$PortalSiteName$GatewayPath" `
            -AnonymousEnabled $false -WindowsEnabled $true
    }
    finally { $verifyManager.Dispose() }
    Wait-DirectSiteBoundary
}
catch {
    $failure = $_.Exception.Message
    $rollbackManager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        Set-AuthenticationState -Manager $rollbackManager -Location $ProjectTrackerSiteName `
            -AnonymousEnabled $prior.AnonymousEnabled -WindowsEnabled $prior.WindowsEnabled
        $rollbackManager.CommitChanges()
    }
    finally { $rollbackManager.Dispose() }
    throw "Project Tracker CORS authentication verification failed and the prior IIS authentication state was restored. $failure"
}

Write-Output 'PROJECT_TRACKER_CORS_AUTHENTICATION_CONFIGURED_AND_VERIFIED'
