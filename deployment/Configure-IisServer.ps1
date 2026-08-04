<#
    One-time IIS setup after all four published folders and Production settings are in place.
    Run from an elevated PowerShell session on SON-IIS2 only.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$ExpectedComputerName = 'SON-IIS2',
    [string]$SiteRoot = 'C:\SonAero\sites'
)

$ErrorActionPreference = 'Stop'
if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
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
        'Install Windows Authentication and create four SON-AERO Hub sites')) {
    return
}

$feature = Install-WindowsFeature Web-Windows-Auth -IncludeManagementTools
if (-not $feature.Success) { throw 'IIS Windows Authentication installation failed.' }

Import-Module WebAdministration
foreach ($site in $sites) {
    $path = Join-Path $SiteRoot $site.Folder
    if (-not (Test-Path "IIS:\AppPools\$($site.Name)")) {
        New-WebAppPool -Name $site.Name | Out-Null
    }
    Set-ItemProperty "IIS:\AppPools\$($site.Name)" -Name managedRuntimeVersion -Value ''
    Set-ItemProperty "IIS:\AppPools\$($site.Name)" -Name processModel.identityType -Value ApplicationPoolIdentity
    Set-ItemProperty "IIS:\AppPools\$($site.Name)" -Name processModel.loadUserProfile -Value $true

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

    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
        -Location $site.Name -Filter 'system.webServer/security/authentication/anonymousAuthentication' `
        -Name enabled -Value false
    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
        -Location $site.Name -Filter 'system.webServer/security/authentication/windowsAuthentication' `
        -Name enabled -Value true
}

$firewallName = 'SON-AERO Hub IIS ports'
if (-not (Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $firewallName -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort 5135,5140,5150,5160 -RemoteAddress LocalSubnet | Out-Null
}

foreach ($site in $sites) {
    Start-WebAppPool -Name $site.Name
    Start-Website -Name $site.Name
}

Write-Host 'IIS configuration completed.'
Write-Host 'Portal: http://SON-IIS2:5140'
