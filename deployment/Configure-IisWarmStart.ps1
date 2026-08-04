<#
    Keeps the four SON-AERO Hub applications warm after reboot and IIS recycle.
    Run from an elevated Windows PowerShell 5.1 session on SON-IIS2.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,62}$')]
    [string]$ExpectedComputerName = 'SON-IIS2',

    [ValidateSet('http', 'https')]
    [string]$Scheme = 'http',

    [ValidateRange(5, 600)]
    [int]$HealthTimeoutSeconds = 120,

    [switch]$StartupRecoveryOnly
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-ForHealth {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = $null
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials `
                -Uri $Uri -TimeoutSec ([Math]::Min(10, $TimeoutSeconds))
            if ($response.StatusCode -eq 200) {
                return $response
            }
            $lastError = "HTTP $($response.StatusCode)"
        }
        catch {
            $lastError = $_.Exception.Message
        }

        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Health warmup failed for $Uri within $TimeoutSeconds seconds. Last error: $lastError"
}

$currentComputer = [string]$env:COMPUTERNAME
if ([string]::IsNullOrWhiteSpace($currentComputer) -or $currentComputer -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is '$currentComputer'."
}
if (-not $WhatIfPreference -and -not (Test-IsAdministrator)) {
    throw 'Run this script from an elevated Windows PowerShell session.'
}

$sites = @(
    [pscustomobject]@{ Name = 'ProjectTracker'; Port = 5135 },
    [pscustomobject]@{ Name = 'SonAeroPortal'; Port = 5140 },
    [pscustomobject]@{ Name = 'EngineeringHub'; Port = 5150 },
    [pscustomobject]@{ Name = 'EstimatingDashboard'; Port = 5160 }
)

$getWindowsFeature = Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue
if ($null -eq $getWindowsFeature) {
    throw 'Get-WindowsFeature is unavailable. Run this script on Windows Server with Server Manager installed.'
}

$appInitFeature = Get-WindowsFeature -Name Web-AppInit
if ($null -eq $appInitFeature) {
    throw 'The IIS Application Initialization feature (Web-AppInit) is unavailable on this server.'
}
if (-not $StartupRecoveryOnly -and $appInitFeature.InstallState -ne 'Installed') {
    if ($PSCmdlet.ShouldProcess($ExpectedComputerName, 'Install IIS Application Initialization (Web-AppInit)')) {
        $featureResult = Install-WindowsFeature -Name Web-AppInit -IncludeManagementTools
        if (-not $featureResult.Success) {
            throw 'IIS Application Initialization installation failed.'
        }
        if ([string]$featureResult.RestartNeeded -eq 'Yes') {
            throw 'Application Initialization requires a restart. Restart SON-IIS2, then run this script again.'
        }
    }
}

$webAdministration = Get-Module -ListAvailable -Name WebAdministration
if ($null -eq $webAdministration) {
    throw 'The IIS WebAdministration module is unavailable.'
}
Import-Module WebAdministration

foreach ($site in $sites) {
    if (-not (Test-Path -LiteralPath "IIS:\AppPools\$($site.Name)")) {
        throw "Required IIS application pool '$($site.Name)' does not exist."
    }
    if (-not (Test-Path -LiteralPath "IIS:\Sites\$($site.Name)")) {
        throw "Required IIS site '$($site.Name)' does not exist."
    }
}

if (-not $StartupRecoveryOnly) {
    foreach ($site in $sites) {
        $poolPath = "IIS:\AppPools\$($site.Name)"
        $sitePath = "IIS:\Sites\$($site.Name)"

        if ($PSCmdlet.ShouldProcess($site.Name, 'Enable IIS always-running application-pool and preload settings')) {
            Set-ItemProperty -LiteralPath $poolPath -Name autoStart -Value $true
            Set-ItemProperty -LiteralPath $poolPath -Name startMode -Value 'AlwaysRunning'
            Set-ItemProperty -LiteralPath $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
            Set-ItemProperty -LiteralPath $sitePath -Name serverAutoStart -Value $true

            $applicationFilter = "system.applicationHost/sites/site[@name='$($site.Name)']/application[@path='/']"
            Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
                -Filter $applicationFilter -Name preloadEnabled -Value $true
            Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
                -Location $site.Name -Filter 'system.webServer/applicationInitialization' `
                -Name doAppInitAfterRestart -Value $true
        }
    }

    $operationsDirectory = Join-Path $env:ProgramData 'SonAero\Operations'
    $installedScriptPath = Join-Path $operationsDirectory 'Configure-IisWarmStart.ps1'
    $taskName = 'SonAero Hub Startup Recovery'
    if ($PSCmdlet.ShouldProcess($taskName, 'Install bounded startup health-recovery task')) {
        New-Item -ItemType Directory -Path $operationsDirectory -Force | Out-Null
        if ([IO.Path]::GetFullPath($PSCommandPath) -ine [IO.Path]::GetFullPath($installedScriptPath)) {
            Copy-Item -LiteralPath $PSCommandPath -Destination $installedScriptPath -Force
        }

        $powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -ExpectedComputerName "{1}" -Scheme {2} -HealthTimeoutSeconds 300 -StartupRecoveryOnly' -f `
            $installedScriptPath, $ExpectedComputerName, $Scheme
        $action = New-ScheduledTaskAction -Execute $powerShellPath -Argument $arguments
        $trigger = New-ScheduledTaskTrigger -AtStartup
        $trigger.Delay = 'PT45S'
        $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
            -ExecutionTimeLimit ([TimeSpan]::FromMinutes(30)) `
            -RestartCount 3 -RestartInterval ([TimeSpan]::FromMinutes(2))
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
            -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
    }
}

if ($WhatIfPreference) {
    Write-Host 'WHATIF_READY: no IIS features, settings, files, or scheduled tasks were changed.'
    return
}

foreach ($site in $sites) {
    if ((Get-WebAppPoolState -Name $site.Name).Value -ne 'Started') {
        Start-WebAppPool -Name $site.Name
    }
    if ((Get-Website -Name $site.Name).State -ne 'Started') {
        Start-Website -Name $site.Name
    }
}

$results = foreach ($site in $sites) {
    $healthUri = '{0}://{1}:{2}/api/health' -f $Scheme, $ExpectedComputerName, $site.Port
    $response = Wait-ForHealth -Uri $healthUri -TimeoutSeconds $HealthTimeoutSeconds
    [pscustomobject]@{
        Site = $site.Name
        AppPoolState = (Get-WebAppPoolState -Name $site.Name).Value
        SiteState = (Get-Website -Name $site.Name).State
        HealthUri = $healthUri
        StatusCode = $response.StatusCode
    }
}

$results | Format-Table -AutoSize
if ($StartupRecoveryOnly) {
    Write-Host 'STARTUP_RECOVERY_HEALTHY'
} else {
    Write-Host 'WARM_START_CONFIGURED_AND_HEALTHY'
}
