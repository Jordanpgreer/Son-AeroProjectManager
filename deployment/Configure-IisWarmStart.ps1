<#
    Keeps the SON-AERO Hub applications and same-origin admin gateway warm after reboot and IIS recycle.
    Run from an elevated Windows PowerShell 5.1 session on SON-IIS2.

    Permanent production hostnames:
      .\Configure-IisWarmStart.ps1 -Scheme https -PermanentHttps -WhatIf

    The existing -Scheme https invocation remains the SON-IIS2:61xx pilot profile.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,62}$')]
    [string]$ExpectedComputerName = 'SON-IIS2',

    [ValidateSet('http', 'https')]
    [string]$Scheme = 'http',

    # Keeps the existing SON-IIS2:61xx HTTPS pilot as the default. Select this switch only after
    # the permanent SNI bindings and DNS records have passed their workstation checks.
    [switch]$PermanentHttps,

    [ValidateRange(1, 65535)]
    [int]$ProjectTrackerHttpsPort = 6135,

    [ValidateRange(1, 65535)]
    [int]$PortalHttpsPort = 6140,

    [ValidateRange(1, 65535)]
    [int]$EngineeringHttpsPort = 6150,

    [ValidateRange(1, 65535)]
    [int]$EstimatingHttpsPort = 6160,

    [ValidateRange(1, 65535)]
    [int]$QualityAssuranceHttpsPort = 6170,

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

function Resolve-WarmStartEndpoint {
    param(
        [Parameter(Mandatory = $true)][ValidateSet(
            'ProjectTracker', 'SonAeroPortal', 'EngineeringHub',
            'EstimatingDashboard', 'QualityAssurance')][string]$Site,
        [Parameter(Mandatory = $true)][ValidateSet('http', 'https')][string]$SelectedScheme,
        [Parameter(Mandatory = $true)][string]$DefaultHostName,
        [Parameter(Mandatory = $true)][int]$HttpPort,
        [Parameter(Mandatory = $true)][int]$HttpsPort,
        [switch]$UsePermanentHttps
    )

    if ($SelectedScheme -eq 'http') {
        return [pscustomobject]@{ Scheme = 'http'; HostName = $DefaultHostName; Port = $HttpPort }
    }

    if ($UsePermanentHttps) {
        $permanentHostNames = @{
            ProjectTracker = 'projects.hub.son4l.local'
            SonAeroPortal = 'hub.son4l.local'
            EngineeringHub = 'engineering.hub.son4l.local'
            EstimatingDashboard = 'estimating.hub.son4l.local'
            QualityAssurance = 'quality.hub.son4l.local'
        }
        return [pscustomobject]@{
            Scheme = 'https'
            HostName = [string]$permanentHostNames[$Site]
            Port = 443
        }
    }

    return [pscustomobject]@{ Scheme = 'https'; HostName = $DefaultHostName; Port = $HttpsPort }
}

function New-HubEndpointUri {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('http', 'https')][string]$SelectedScheme,
        [Parameter(Mandatory = $true)][string]$HostName,
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $builder = New-Object UriBuilder($SelectedScheme, $HostName, $Port)
    $builder.Path = $Path.TrimStart('/')
    return $builder.Uri.AbsoluteUri
}

function New-StartupRecoveryArguments {
    param(
        [Parameter(Mandatory = $true)][string]$InstalledScriptPath,
        [Parameter(Mandatory = $true)][string]$ComputerName,
        [Parameter(Mandatory = $true)][ValidateSet('http', 'https')][string]$SelectedScheme,
        [switch]$UsePermanentHttps,
        [Parameter(Mandatory = $true)][int]$ProjectTrackerPort,
        [Parameter(Mandatory = $true)][int]$PortalPort,
        [Parameter(Mandatory = $true)][int]$EngineeringPort,
        [Parameter(Mandatory = $true)][int]$EstimatingPort,
        [Parameter(Mandatory = $true)][int]$QualityAssurancePort
    )

    $argumentParts = @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        ('-File "{0}"' -f $InstalledScriptPath),
        ('-ExpectedComputerName "{0}"' -f $ComputerName),
        ('-Scheme {0}' -f $SelectedScheme),
        '-HealthTimeoutSeconds 300', '-StartupRecoveryOnly'
    )
    if ($UsePermanentHttps) {
        $argumentParts += '-PermanentHttps'
    } else {
        $argumentParts += @(
            ('-ProjectTrackerHttpsPort {0}' -f $ProjectTrackerPort),
            ('-PortalHttpsPort {0}' -f $PortalPort),
            ('-EngineeringHttpsPort {0}' -f $EngineeringPort),
            ('-EstimatingHttpsPort {0}' -f $EstimatingPort),
            ('-QualityAssuranceHttpsPort {0}' -f $QualityAssurancePort)
        )
    }
    return $argumentParts -join ' '
}

function New-WarmStartFileSystemSecurity {
    param([switch]$Directory, [switch]$BrandingRoot)
    if ($BrandingRoot -and -not $Directory) {
        throw 'BrandingRoot is valid only for a directory ACL.'
    }
    $security = if ($Directory) { New-Object Security.AccessControl.DirectorySecurity }
        else { New-Object Security.AccessControl.FileSecurity }
    $security.SetAccessRuleProtection($true, $false)
    $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $security.SetOwner($administrators)
    $inheritance = if ($Directory) {
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    } else { [Security.AccessControl.InheritanceFlags]::None }
    foreach ($identity in @($system, $administrators)) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $identity, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
    }
    if ($BrandingRoot) {
        # Employees read the shared icon here; Operations has a separate privileged-only ACL.
        $users = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
        $readRule = New-Object Security.AccessControl.FileSystemAccessRule(
            $users, [Security.AccessControl.FileSystemRights]::ReadAndExecute, $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($readRule)
    }
    return $security
}

function Assert-ProtectedWarmStartPath {
    param([Parameter(Mandatory = $true)][string]$Path, [switch]$Directory, [switch]$BrandingRoot)
    if ($BrandingRoot -and -not $Directory) {
        throw 'BrandingRoot is valid only for a directory ACL.'
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Warm-start path '$Path' must not be a reparse point."
    }
    if ($Directory -and -not $item.PSIsContainer) { throw "Warm-start directory '$Path' is not a directory." }
    if (-not $Directory -and $item.PSIsContainer) { throw "Warm-start file '$Path' is not a file." }
    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) { throw "Warm-start path '$Path' still inherits access rules." }
    $privilegedSids = @('S-1-5-18', 'S-1-5-32-544')
    $usersSid = 'S-1-5-32-545'
    $allowedSids = if ($BrandingRoot) { @($privilegedSids + $usersSid) } else { $privilegedSids }
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin $allowedSids) { throw "Warm-start path '$Path' has unexpected owner '$owner'." }
    $fullControlSids = @()
    foreach ($rule in @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))) {
        $sid = $rule.IdentityReference.Value
        if ($sid -notin $allowedSids -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) {
            throw "Warm-start path '$Path' grants access to unexpected identity '$sid'."
        }
        if (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq
            [Security.AccessControl.FileSystemRights]::FullControl) { $fullControlSids += $sid }
    }
    foreach ($sid in $privilegedSids) {
        if ($fullControlSids -notcontains $sid) {
            throw "Warm-start path '$Path' does not grant full control to '$sid'."
        }
    }
    if ($BrandingRoot) {
        $userRules = @($acl.GetAccessRules(
            $true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
                $_.IdentityReference.Value -eq $usersSid
            })
        $readMask = [Security.AccessControl.FileSystemRights]::ReadAndExecute
        $writeMask = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor
            [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
        if ($userRules.Count -ne 1 -or
            ($userRules[0].FileSystemRights -band $readMask) -ne $readMask -or
            ($userRules[0].FileSystemRights -band $writeMask) -ne 0) {
            throw "Warm-start branding directory '$Path' must grant Users inherited read/execute access without write or ACL-control rights."
        }
    }
}

function Initialize-ProtectedWarmStartDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$ProgramDataRoot,
        [Parameter(Mandatory = $true)][string]$OperationsDirectory
    )
    $root = [IO.Path]::GetFullPath($ProgramDataRoot).TrimEnd('\')
    $expectedOperations = [IO.Path]::GetFullPath((Join-Path $root 'SonAero\Operations')).TrimEnd('\')
    $requestedOperations = [IO.Path]::GetFullPath($OperationsDirectory).TrimEnd('\')
    if ($requestedOperations -ine $expectedOperations) {
        throw "Warm-start operations directory must be exactly '$expectedOperations'."
    }
    $rootItem = Get-Item -LiteralPath $root -Force -ErrorAction Stop
    if (-not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "ProgramData root '$root' must be a non-reparse directory."
    }
    $sonAeroDirectory = Join-Path $root 'SonAero'
    foreach ($directory in @($sonAeroDirectory, $expectedOperations)) {
        if (-not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory | Out-Null
        }
        $item = Get-Item -LiteralPath $directory -Force -ErrorAction Stop
        if (-not $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Warm-start directory '$directory' must be a non-reparse directory."
        }
    }
    Set-Acl -LiteralPath $sonAeroDirectory -AclObject (New-WarmStartFileSystemSecurity -Directory -BrandingRoot)
    Assert-ProtectedWarmStartPath -Path $sonAeroDirectory -Directory -BrandingRoot
    Set-Acl -LiteralPath $expectedOperations -AclObject (New-WarmStartFileSystemSecurity -Directory)
    Assert-ProtectedWarmStartPath -Path $expectedOperations -Directory
    return $expectedOperations
}

function Install-ProtectedWarmStartScript {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )
    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "Warm-start source script was not found at '$SourcePath'."
    }
    if (Test-Path -LiteralPath $DestinationPath) {
        $destination = Get-Item -LiteralPath $DestinationPath -Force -ErrorAction Stop
        if ($destination.PSIsContainer -or
            ($destination.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Installed warm-start script '$DestinationPath' must be a non-reparse file."
        }
    }
    if ([IO.Path]::GetFullPath($SourcePath) -ine [IO.Path]::GetFullPath($DestinationPath)) {
        Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force
    }
    Set-Acl -LiteralPath $DestinationPath -AclObject (New-WarmStartFileSystemSecurity)
    Assert-ProtectedWarmStartPath -Path $DestinationPath
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $SourcePath).Hash -ne
        (Get-FileHash -Algorithm SHA256 -LiteralPath $DestinationPath).Hash) {
        throw 'Installed warm-start script hash does not match the reviewed source.'
    }
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

if ($PermanentHttps) {
    if ($Scheme -ne 'https') {
        throw 'PermanentHttps requires -Scheme https.'
    }
    $conflictingParameters = @(
        'ProjectTrackerHttpsPort', 'PortalHttpsPort', 'EngineeringHttpsPort',
        'EstimatingHttpsPort', 'QualityAssuranceHttpsPort'
    ) | Where-Object { $PSBoundParameters.ContainsKey($_) }
    if ($conflictingParameters.Count -gt 0) {
        throw "PermanentHttps uses port 443; do not also supply: $($conflictingParameters -join ', ')."
    }
}

$sites = @(
    [pscustomobject]@{ Name = 'ProjectTracker'; HttpPort = 5135; HttpsPort = $ProjectTrackerHttpsPort },
    [pscustomobject]@{ Name = 'SonAeroPortal'; HttpPort = 5140; HttpsPort = $PortalHttpsPort },
    [pscustomobject]@{ Name = 'EngineeringHub'; HttpPort = 5150; HttpsPort = $EngineeringHttpsPort },
    [pscustomobject]@{ Name = 'EstimatingDashboard'; HttpPort = 5160; HttpsPort = $EstimatingHttpsPort },
    [pscustomobject]@{ Name = 'QualityAssurance'; HttpPort = 5170; HttpsPort = $QualityAssuranceHttpsPort }
)
$gateway = [pscustomobject]@{
    Pool = 'ProjectTrackerAdminGateway'
    Site = 'SonAeroPortal'
    ApplicationPath = '/project-tracker-api'
    HealthPath = '/project-tracker-api/api/health'
    HttpPort = 5140
    HttpsPort = $PortalHttpsPort
}

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
$priorWhatIfPreference = $WhatIfPreference
try {
    $WhatIfPreference = $false
    Import-Module WebAdministration -ErrorAction Stop
}
finally {
    $WhatIfPreference = $priorWhatIfPreference
}

foreach ($site in $sites) {
    if (-not (Test-Path -LiteralPath "IIS:\AppPools\$($site.Name)")) {
        throw "Required IIS application pool '$($site.Name)' does not exist."
    }
    if (-not (Test-Path -LiteralPath "IIS:\Sites\$($site.Name)")) {
        throw "Required IIS site '$($site.Name)' does not exist."
    }
}
if (-not (Test-Path -LiteralPath "IIS:\AppPools\$($gateway.Pool)")) {
    throw "Required IIS application pool '$($gateway.Pool)' does not exist. Run Configure-PortalProjectTrackerGateway.ps1 first."
}
if (-not (Test-Path -LiteralPath "IIS:\Sites\$($gateway.Site)\$($gateway.ApplicationPath.TrimStart('/'))")) {
    throw "Required IIS application '$($gateway.ApplicationPath)' does not exist. Run Configure-PortalProjectTrackerGateway.ps1 first."
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

    if ($PSCmdlet.ShouldProcess($gateway.Pool, 'Enable IIS always-running application-pool and preload settings')) {
        $gatewayPoolPath = "IIS:\AppPools\$($gateway.Pool)"
        Set-ItemProperty -LiteralPath $gatewayPoolPath -Name autoStart -Value $true
        Set-ItemProperty -LiteralPath $gatewayPoolPath -Name startMode -Value 'AlwaysRunning'
        Set-ItemProperty -LiteralPath $gatewayPoolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
        $gatewayFilter = "system.applicationHost/sites/site[@name='$($gateway.Site)']/application[@path='$($gateway.ApplicationPath)']"
        Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
            -Filter $gatewayFilter -Name preloadEnabled -Value $true
    }

    $operationsDirectory = Join-Path $env:ProgramData 'SonAero\Operations'
    $installedScriptPath = Join-Path $operationsDirectory 'Configure-IisWarmStart.ps1'
    $taskName = 'SonAero Hub Startup Recovery'
    if ($PSCmdlet.ShouldProcess($taskName, 'Install bounded startup health-recovery task')) {
        $operationsDirectory = Initialize-ProtectedWarmStartDirectory `
            -ProgramDataRoot $env:ProgramData -OperationsDirectory $operationsDirectory
        $installedScriptPath = Join-Path $operationsDirectory 'Configure-IisWarmStart.ps1'
        Install-ProtectedWarmStartScript -SourcePath $PSCommandPath -DestinationPath $installedScriptPath

        $powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $arguments = New-StartupRecoveryArguments -InstalledScriptPath $installedScriptPath `
            -ComputerName $ExpectedComputerName -SelectedScheme $Scheme `
            -UsePermanentHttps:$PermanentHttps `
            -ProjectTrackerPort $ProjectTrackerHttpsPort -PortalPort $PortalHttpsPort `
            -EngineeringPort $EngineeringHttpsPort -EstimatingPort $EstimatingHttpsPort `
            -QualityAssurancePort $QualityAssuranceHttpsPort
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
if ((Get-WebAppPoolState -Name $gateway.Pool).Value -ne 'Started') {
    Start-WebAppPool -Name $gateway.Pool
}

$results = foreach ($site in $sites) {
    $endpoint = Resolve-WarmStartEndpoint -Site $site.Name -SelectedScheme $Scheme `
        -DefaultHostName $ExpectedComputerName -HttpPort $site.HttpPort `
        -HttpsPort $site.HttpsPort -UsePermanentHttps:$PermanentHttps
    $healthUri = New-HubEndpointUri -SelectedScheme $endpoint.Scheme `
        -HostName $endpoint.HostName -Port $endpoint.Port -Path '/api/health'
    $response = Wait-ForHealth -Uri $healthUri -TimeoutSeconds $HealthTimeoutSeconds
    [pscustomobject]@{
        Site = $site.Name
        AppPoolState = (Get-WebAppPoolState -Name $site.Name).Value
        SiteState = (Get-Website -Name $site.Name).State
        HealthUri = $healthUri
        StatusCode = $response.StatusCode
    }
}

$gatewayEndpoint = Resolve-WarmStartEndpoint -Site $gateway.Site -SelectedScheme $Scheme `
    -DefaultHostName $ExpectedComputerName -HttpPort $gateway.HttpPort `
    -HttpsPort $gateway.HttpsPort -UsePermanentHttps:$PermanentHttps
$gatewayHealthUri = New-HubEndpointUri -SelectedScheme $gatewayEndpoint.Scheme `
    -HostName $gatewayEndpoint.HostName -Port $gatewayEndpoint.Port -Path $gateway.HealthPath
$gatewayResponse = Wait-ForHealth -Uri $gatewayHealthUri -TimeoutSeconds $HealthTimeoutSeconds
$results += [pscustomobject]@{
    Site = $gateway.Pool
    AppPoolState = (Get-WebAppPoolState -Name $gateway.Pool).Value
    SiteState = (Get-Website -Name $gateway.Site).State
    HealthUri = $gatewayHealthUri
    StatusCode = $gatewayResponse.StatusCode
}

$results | Format-Table -AutoSize
if ($StartupRecoveryOnly) {
    Write-Host 'STARTUP_RECOVERY_HEALTHY'
} else {
    Write-Host 'WARM_START_CONFIGURED_AND_HEALTHY'
}
