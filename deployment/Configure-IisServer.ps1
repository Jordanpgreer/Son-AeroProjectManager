<#
    One-time IIS setup after all four published folders and Production settings are in place.
    Run from an elevated PowerShell session on SON-IIS2 only.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$ExpectedComputerName = 'SON-IIS2',
    [string]$SiteRoot = 'C:\SonAero\sites',
    [ValidateRange(15, 300)]
    [int]$HealthTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
}
if (-not $WhatIfPreference) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated Windows PowerShell session.'
    }
}

$sites = @(
    [pscustomobject]@{ Name = 'ProjectTracker'; Port = 5135; Folder = 'ProjectTracker' },
    [pscustomobject]@{ Name = 'SonAeroPortal'; Port = 5140; Folder = 'Portal' },
    [pscustomobject]@{ Name = 'EngineeringHub'; Port = 5150; Folder = 'EngineeringHub' },
    [pscustomobject]@{ Name = 'EstimatingDashboard'; Port = 5160; Folder = 'EstimatingDashboard' }
)

foreach ($site in $sites) {
    $path = Join-Path $SiteRoot $site.Folder
    if (-not (Test-Path -LiteralPath (Join-Path $path 'web.config'))) {
        throw "Published application is missing: $path\web.config"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $path 'appsettings.Production.json'))) {
        throw "Production configuration is missing: $path\appsettings.Production.json"
    }
}

$hostingModule = Get-ChildItem 'C:\Program Files\IIS\Asp.Net Core Module\V2' -Filter aspnetcorev2.dll -ErrorAction SilentlyContinue
if (-not $hostingModule) {
    throw 'The ASP.NET Core 8 Hosting Bundle must be installed before configuring the sites.'
}

if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName IIS",
        'Install Windows Authentication/Application Initialization and create four SON-AERO Hub sites')) {
    return
}

$feature = Install-WindowsFeature -Name Web-Windows-Auth,Web-AppInit -IncludeManagementTools
if (-not $feature.Success) { throw 'Required IIS feature installation failed.' }
if ([string]$feature.RestartNeeded -eq 'Yes') {
    throw 'IIS feature installation requires a restart. Restart SON-IIS2, then run this script again. No site changes were made.'
}

Import-Module WebAdministration

# Validate binding ownership before changing any pools or sites. Existing sites must already use
# the expected all-unassigned HTTP binding; the script will not guess which old binding to remove.
foreach ($site in $sites) {
    $portPattern = ":$($site.Port):"
    $conflicts = @(Get-Website | Where-Object Name -NE $site.Name | ForEach-Object {
        Get-WebBinding -Name $_.Name | Where-Object {
            $_.bindingInformation -match [regex]::Escape($portPattern)
        }
    })
    if ($conflicts.Count -gt 0) {
        throw "Port $($site.Port) is already bound by another IIS site. No IIS site changes were made."
    }

    $existingSite = Get-Website -Name $site.Name -ErrorAction SilentlyContinue
    if ($existingSite) {
        $expectedBinding = @(Get-WebBinding -Name $site.Name -Protocol http | Where-Object {
            $_.bindingInformation -eq "*:$($site.Port):"
        })
        if ($expectedBinding.Count -ne 1) {
            throw "Existing site '$($site.Name)' must have exactly one HTTP binding '*:$($site.Port):'. No IIS site changes were made."
        }
    }
}

foreach ($site in $sites) {
    $path = Join-Path $SiteRoot $site.Folder
    if (-not (Test-Path "IIS:\AppPools\$($site.Name)")) {
        New-WebAppPool -Name $site.Name | Out-Null
    }
    Set-ItemProperty "IIS:\AppPools\$($site.Name)" -Name managedRuntimeVersion -Value ''
    Set-ItemProperty "IIS:\AppPools\$($site.Name)" -Name processModel.identityType -Value ApplicationPoolIdentity
    Set-ItemProperty "IIS:\AppPools\$($site.Name)" -Name processModel.loadUserProfile -Value $true
    Set-ItemProperty "IIS:\AppPools\$($site.Name)" -Name autoStart -Value $true
    Set-ItemProperty "IIS:\AppPools\$($site.Name)" -Name startMode -Value AlwaysRunning
    Set-ItemProperty "IIS:\AppPools\$($site.Name)" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)

    & icacls.exe $path /grant "IIS AppPool\$($site.Name):(OI)(CI)RX" /t /c | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Site-folder permission assignment failed for $path." }

    $existingSite = Get-Website -Name $site.Name -ErrorAction SilentlyContinue
    if (-not $existingSite) {
        New-Website -Name $site.Name -PhysicalPath $path -Port $site.Port `
            -ApplicationPool $site.Name | Out-Null
    }
    else {
        Set-ItemProperty "IIS:\Sites\$($site.Name)" -Name physicalPath -Value $path
        Set-ItemProperty "IIS:\Sites\$($site.Name)" -Name applicationPool -Value $site.Name
    }
    Set-ItemProperty "IIS:\Sites\$($site.Name)" -Name serverAutoStart -Value $true

    $appCmd = Join-Path $env:WINDIR 'System32\inetsrv\appcmd.exe'
    & $appCmd set app "/app.name:$($site.Name)/" /preloadEnabled:true | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "IIS preload configuration failed for $($site.Name)." }
    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
        -Location $site.Name -Filter 'system.webServer/applicationInitialization' `
        -Name doAppInitAfterRestart -Value true

    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
        -Location $site.Name -Filter 'system.webServer/security/authentication/anonymousAuthentication' `
        -Name enabled -Value false
    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
        -Location $site.Name -Filter 'system.webServer/security/authentication/windowsAuthentication' `
        -Name enabled -Value true
}

$gatewayPoolName = 'ProjectTrackerAdminGateway'
$gatewayApplicationName = 'project-tracker-api'
$trackerPath = Join-Path $SiteRoot 'ProjectTracker'
if (-not (Test-Path "IIS:\AppPools\$gatewayPoolName")) {
    New-WebAppPool -Name $gatewayPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$gatewayPoolName" -Name managedRuntimeVersion -Value ''
Set-ItemProperty "IIS:\AppPools\$gatewayPoolName" -Name enable32BitAppOnWin64 -Value $false
Set-ItemProperty "IIS:\AppPools\$gatewayPoolName" -Name processModel.identityType -Value ApplicationPoolIdentity
Set-ItemProperty "IIS:\AppPools\$gatewayPoolName" -Name processModel.loadUserProfile -Value $true
Set-ItemProperty "IIS:\AppPools\$gatewayPoolName" -Name autoStart -Value $true
Set-ItemProperty "IIS:\AppPools\$gatewayPoolName" -Name startMode -Value AlwaysRunning
Set-ItemProperty "IIS:\AppPools\$gatewayPoolName" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)

$gatewayIisPath = "IIS:\Sites\SonAeroPortal\$gatewayApplicationName"
if (-not (Test-Path $gatewayIisPath)) {
    New-WebApplication -Site 'SonAeroPortal' -Name $gatewayApplicationName `
        -PhysicalPath $trackerPath -ApplicationPool $gatewayPoolName | Out-Null
}
else {
    Set-ItemProperty $gatewayIisPath -Name physicalPath -Value $trackerPath
    Set-ItemProperty $gatewayIisPath -Name applicationPool -Value $gatewayPoolName
}
& icacls.exe $trackerPath /grant "IIS AppPool\$gatewayPoolName`:(OI)(CI)RX" /t /c | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Project Tracker gateway permission assignment failed.' }
& $appCmd set app "/app.name:SonAeroPortal/$gatewayApplicationName" /preloadEnabled:true | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Project Tracker gateway preload configuration failed.' }
Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
    -Location "SonAeroPortal/$gatewayApplicationName" `
    -Filter 'system.webServer/security/authentication/anonymousAuthentication' `
    -Name enabled -Value false
Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
    -Location "SonAeroPortal/$gatewayApplicationName" `
    -Filter 'system.webServer/security/authentication/windowsAuthentication' `
    -Name enabled -Value true

$firewallName = 'SON-AERO Hub IIS ports'
$firewallRules = @(Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue)
if ($firewallRules.Count -gt 1) {
    throw "More than one firewall rule is named '$firewallName'. Resolve the duplicate rules before continuing."
}
if ($firewallRules.Count -eq 0) {
    New-NetFirewallRule -DisplayName $firewallName -Direction Inbound -Action Allow `
        -Enabled True -Profile Domain,Private -Protocol TCP `
        -LocalPort 5135,5140,5150,5160 -RemoteAddress LocalSubnet | Out-Null
}
else {
    $firewallRule = $firewallRules[0]
    $firewallRule | Set-NetFirewallRule -Direction Inbound -Action Allow -Enabled True `
        -Profile Domain,Private | Out-Null
    $firewallRule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter `
        -Protocol TCP -LocalPort 5135,5140,5150,5160 | Out-Null
    $firewallRule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter `
        -RemoteAddress LocalSubnet | Out-Null
}

foreach ($site in $sites) {
    if ((Get-WebAppPoolState -Name $site.Name).Value -ne 'Started') {
        Start-WebAppPool -Name $site.Name
    }
    if ((Get-Website -Name $site.Name).State -ne 'Started') {
        Start-Website -Name $site.Name
    }
}
if ((Get-WebAppPoolState -Name $gatewayPoolName).Value -ne 'Started') {
    Start-WebAppPool -Name $gatewayPoolName
}

$deadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
$pending = @($sites)
$healthResults = [System.Collections.Generic.List[object]]::new()
do {
    foreach ($site in @($pending)) {
        $uri = "http://$ExpectedComputerName`:$($site.Port)/api/health"
        try {
            $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                $healthResults.Add([pscustomobject]@{ Site = $site.Name; Uri = $uri; StatusCode = 200 })
                $pending = @($pending | Where-Object Name -NE $site.Name)
            }
        }
        catch { }
    }
    if ($pending.Count -gt 0) { Start-Sleep -Milliseconds 750 }
} while ($pending.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)

if ($pending.Count -gt 0) {
    throw "IIS configuration was applied, but health verification timed out for: $($pending.Name -join ', '). Review Event Viewer before retrying."
}

$gatewayUri = "http://$ExpectedComputerName`:5140/$gatewayApplicationName/api/health"
$gatewayDeadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
do {
    try {
        $gatewayResponse = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $gatewayUri -TimeoutSec 5
        if ($gatewayResponse.StatusCode -eq 200) { break }
    }
    catch { }
    Start-Sleep -Milliseconds 750
} while ([DateTime]::UtcNow -lt $gatewayDeadline)
if ($null -eq $gatewayResponse -or $gatewayResponse.StatusCode -ne 200) {
    throw "IIS configuration was applied, but the Project Tracker gateway did not become healthy at $gatewayUri."
}

$healthResults | Format-Table -AutoSize
Write-Host 'IIS_CONFIGURED_AND_HEALTHY'
Write-Host "Portal: http://$ExpectedComputerName`:5140"
