<#
    Additive pilot HTTPS transaction for the four SON-AERO Hub IIS sites on SON-IIS2.

    Preview:
      .\Configure-HubHttpsPilot.ps1 -CertificateThumbprint <LEAF> -PilotRootThumbprint <ROOT> -PilotRemoteAddress 10.50.10.25 -WhatIf
    Apply:
      .\Configure-HubHttpsPilot.ps1 -CertificateThumbprint <LEAF> -PilotRootThumbprint <ROOT> -PilotRemoteAddress 10.50.10.25 -Confirm:$false
    Roll back the last successful apply:
      .\Configure-HubHttpsPilot.ps1 -Rollback -Confirm:$false

    HTTP bindings on ports 5135-5160 are never removed. The pilot firewall rule is separate
    from the existing HTTP rule and never permits Any or LocalSubnet.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'Apply')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Apply')]
    [ValidatePattern('^(?:[A-Fa-f0-9]{2}\s*){20}$')]
    [string]$CertificateThumbprint,

    [Parameter(Mandatory = $true, ParameterSetName = 'Apply')]
    [ValidatePattern('^(?:[A-Fa-f0-9]{2}\s*){20}$')]
    [string]$PilotRootThumbprint,

    [Parameter(Mandatory = $true, ParameterSetName = 'Apply')]
    [ValidateNotNullOrEmpty()]
    [string[]]$PilotRemoteAddress,

    [Parameter(Mandatory = $true, ParameterSetName = 'Rollback')]
    [switch]$Rollback,

    [ValidateRange(7, 365)]
    [int]$MinimumRemainingDays = 30,

    [ValidateRange(30, 600)]
    [int]$HealthTimeoutSeconds = 180,

    [string]$StatePath = 'C:\ProgramData\SonAero\deployment-state\https-pilot.json'
)

$ErrorActionPreference = 'Stop'
$expectedComputerName = 'SON-IIS2'
$firewallRuleName = 'SON-AERO Hub HTTPS pilot'
$certificateStoreName = 'My'
$requiredDnsNames = @('SON-IIS2', 'SON-IIS2.SON4L.LOCAL')
$applications = @(
    [pscustomobject]@{ Site = 'ProjectTracker'; HttpPort = 5135; HttpsPort = 6135 },
    [pscustomobject]@{ Site = 'SonAeroPortal'; HttpPort = 5140; HttpsPort = 6140 },
    [pscustomobject]@{ Site = 'EngineeringHub'; HttpPort = 5150; HttpsPort = 6150 },
    [pscustomobject]@{ Site = 'EstimatingDashboard'; HttpPort = 5160; HttpsPort = 6160 }
)

function Assert-Host {
    if ($env:COMPUTERNAME -ine $expectedComputerName) {
        throw "This transaction is restricted to $expectedComputerName; current computer is $env:COMPUTERNAME."
    }
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated Windows PowerShell session.'
    }
}

function Import-IisAdministration {
    $priorWhatIf = $WhatIfPreference
    try {
        $WhatIfPreference = $false
        Import-Module WebAdministration -Global -ErrorAction Stop
    }
    finally { $WhatIfPreference = $priorWhatIf }
    $assemblyPath = Join-Path $env:WINDIR 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "IIS administration assembly was not found at '$assemblyPath'."
    }
    Add-Type -Path $assemblyPath -ErrorAction Stop
}

function Convert-HashToHex {
    param($Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [byte[]]) { return ([BitConverter]::ToString($Value)).Replace('-', '') }
    return ([string]$Value).Replace(' ', '').Replace('-', '').ToUpperInvariant()
}

function Convert-HexToBytes {
    param([Parameter(Mandatory = $true)][string]$Value)
    $hex = (Convert-HashToHex $Value)
    if ($hex -notmatch '^(?:[A-F0-9]{2})+$') { throw "Invalid certificate hash '$Value'." }
    [byte[]]$bytes = @(for ($index = 0; $index -lt $hex.Length; $index += 2) {
        [Convert]::ToByte($hex.Substring($index, 2), 16)
    })
    return $bytes
}

function Convert-BindingToSnapshot {
    param([Parameter(Mandatory = $true)]$Binding, [Parameter(Mandatory = $true)][string]$Site)
    return [pscustomobject]@{
        Site = $Site
        Protocol = [string]$Binding.Protocol
        BindingInformation = [string]$Binding.BindingInformation
        CertificateHash = Convert-HashToHex $Binding.CertificateHash
        CertificateStoreName = [string]$Binding.CertificateStoreName
        SslFlags = [int]$Binding.SslFlags
    }
}

function Get-IisBindingSnapshot {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $snapshot = @()
        foreach ($site in $manager.Sites) {
            foreach ($binding in $site.Bindings) {
                $snapshot += Convert-BindingToSnapshot -Binding $binding -Site $site.Name
            }
        }
        return @($snapshot)
    }
    finally { $manager.Dispose() }
}

function Get-TargetBindingSnapshot {
    param([Parameter(Mandatory = $true)][object[]]$Snapshot)
    $ports = @($applications.HttpsPort)
    return @($Snapshot | Where-Object {
        $parts = $_.BindingInformation -split ':', 3
        $parts.Count -ge 2 -and [int]$parts[1] -in $ports
    })
}

function Assert-RequiredHttpBindings {
    param([Parameter(Mandatory = $true)][object[]]$Snapshot)
    foreach ($application in $applications) {
        $expected = "*:$($application.HttpPort):"
        $matches = @($Snapshot | Where-Object {
            $_.Site -eq $application.Site -and $_.Protocol -eq 'http' -and $_.BindingInformation -eq $expected
        })
        if ($matches.Count -ne 1) {
            throw "Site '$($application.Site)' must retain exactly one HTTP binding '$expected'; found $($matches.Count)."
        }
    }
}

function Assert-TargetBindingsAvailable {
    param(
        [Parameter(Mandatory = $true)][object[]]$Snapshot,
        [Parameter(Mandatory = $true)][string]$Thumbprint
    )
    foreach ($application in $applications) {
        $expectedInformation = "*:$($application.HttpsPort):"
        $onPort = @($Snapshot | Where-Object {
            $parts = $_.BindingInformation -split ':', 3
            $parts.Count -ge 2 -and [int]$parts[1] -eq $application.HttpsPort
        })
        if ($onPort.Count -gt 1) {
            throw "HTTPS pilot port $($application.HttpsPort) has multiple IIS bindings."
        }
        if ($onPort.Count -eq 1) {
            $binding = $onPort[0]
            $exact = $binding.Site -eq $application.Site -and
                $binding.Protocol -eq 'https' -and
                $binding.BindingInformation -eq $expectedInformation -and
                $binding.CertificateHash -eq $Thumbprint -and
                $binding.CertificateStoreName -eq $certificateStoreName -and
                $binding.SslFlags -eq 0
            if (-not $exact) {
                throw "Port $($application.HttpsPort) is already assigned to a conflicting IIS binding."
            }
        }
        else {
            $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $application.HttpsPort -ErrorAction SilentlyContinue)
            if ($listeners.Count -gt 0) {
                throw "TCP port $($application.HttpsPort) already has a listener that is not represented by the expected IIS binding."
            }
        }
    }
}

function Assert-Certificate {
    param(
        [Parameter(Mandatory = $true)][string]$Thumbprint,
        [Parameter(Mandatory = $true)][string]$RootThumbprint
    )
    $certificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$Thumbprint" -ErrorAction SilentlyContinue
    if (-not $certificate) { throw "Certificate $Thumbprint was not found in Cert:\LocalMachine\My." }
    $rootCertificate = Get-Item -LiteralPath "Cert:\LocalMachine\Root\$RootThumbprint" -ErrorAction SilentlyContinue
    if (-not $rootCertificate) { throw "Pilot root $RootThumbprint was not found in Cert:\LocalMachine\Root." }
    $now = Get-Date
    if (-not $certificate.HasPrivateKey) { throw 'The certificate does not have a private key.' }
    if ($certificate.NotBefore -gt $now -or $certificate.NotAfter -lt $now.AddDays($MinimumRemainingDays)) {
        throw "The certificate is not currently valid for the required $MinimumRemainingDays-day safety window."
    }
    $basicExtension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' } | Select-Object -First 1
    if (-not $basicExtension) { throw 'The certificate has no Basic Constraints extension.' }
    $basic = [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
        $basicExtension, $basicExtension.Critical)
    if ($basic.CertificateAuthority) { throw 'The selected certificate is a CA certificate, not a leaf certificate.' }
    $rootBasicExtension = $rootCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' } | Select-Object -First 1
    if (-not $rootBasicExtension) { throw 'The selected pilot root has no Basic Constraints extension.' }
    $rootBasic = [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
        $rootBasicExtension, $rootBasicExtension.Critical)
    if (-not $rootBasic.CertificateAuthority) { throw 'The selected pilot root is not a CA certificate.' }
    if ($rootCertificate.NotBefore -gt $now -or $rootCertificate.NotAfter -lt $certificate.NotAfter) {
        throw 'The pilot root validity period does not cover the leaf validity period.'
    }
    if ($certificate.Issuer -ne $rootCertificate.Subject) { throw 'The pilot leaf issuer does not match the explicit pilot root subject.' }
    $eku = @($certificate.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId.Value })
    if ($eku -notcontains '1.3.6.1.5.5.7.3.1') { throw 'The certificate lacks the Server Authentication EKU.' }
    if ($certificate.PSObject.Properties.Name -notcontains 'DnsNameList') {
        throw 'DnsNameList is unavailable, so SAN validation cannot be completed safely.'
    }
    $dnsNames = @($certificate.DnsNameList | ForEach-Object {
        if ($_.PSObject.Properties.Name -contains 'Punycode' -and $_.Punycode) { $_.Punycode }
        elseif ($_.PSObject.Properties.Name -contains 'Unicode' -and $_.Unicode) { $_.Unicode }
    } | Where-Object { $_ })
    foreach ($requiredName in $requiredDnsNames) {
        if (-not ($dnsNames | Where-Object { $_ -ieq $requiredName })) {
            throw "Certificate SAN does not include '$requiredName'."
        }
    }
    $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
    try {
        # PILOT LIMITATION: this private pilot CA intentionally publishes no CRL/CDP/OCSP.
        # NoCheck is restricted to revocation; Build must still produce a trusted, valid chain
        # terminating at the explicit LocalMachine root thumbprint supplied by the operator.
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $chain.ChainPolicy.RevocationFlag = [Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
        if (-not $chain.Build($certificate)) {
            $details = @($chain.ChainStatus | ForEach-Object { "$($_.Status): $($_.StatusInformation.Trim())" }) -join '; '
            throw "Certificate chain validation failed: $details"
        }
        $elements = @($chain.ChainElements)
        if ($elements.Count -lt 2 -or
            (Convert-HashToHex $elements[0].Certificate.Thumbprint) -ne $Thumbprint -or
            (Convert-HashToHex $elements[$elements.Count - 1].Certificate.Thumbprint) -ne $RootThumbprint) {
            throw 'The trusted chain does not terminate at the explicit pilot root thumbprint.'
        }
    }
    finally { $chain.Dispose() }
    return $certificate
}

function Convert-ToPilotAddress {
    param([Parameter(Mandatory = $true)][string[]]$Address)
    $result = @()
    foreach ($raw in $Address) {
        $value = $raw.Trim()
        if ([string]::IsNullOrWhiteSpace($value) -or $value -match '^(Any|LocalSubnet|Internet|Intranet|DNS|DHCP|WINS|DefaultGateway)$') {
            throw "Pilot remote address '$raw' is not an explicit IP address or constrained CIDR."
        }
        $parts = $value -split '/', 2
        $ip = $null
        if (-not [Net.IPAddress]::TryParse($parts[0], [ref]$ip)) { throw "Invalid pilot IP address '$value'." }
        if ($ip.IsIPv6Multicast -or $ip.Equals([Net.IPAddress]::IPv6Any) -or
            $ip.Equals([Net.IPAddress]::IPv6Loopback) -or $ip.Equals([Net.IPAddress]::Any) -or
            $ip.Equals([Net.IPAddress]::Loopback)) {
            throw "Unsafe pilot IP address '$value'."
        }
        if ($ip.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) {
            $octets = $ip.GetAddressBytes()
            if ($octets[0] -ge 224 -or ($octets[0] -eq 169 -and $octets[1] -eq 254) -or
                ($octets | Where-Object { $_ -ne 255 }).Count -eq 0) { throw "Unsafe pilot IPv4 address '$value'." }
        }
        if ($parts.Count -eq 2) {
            $prefix = 0
            if (-not [int]::TryParse($parts[1], [ref]$prefix)) { throw "Invalid CIDR prefix '$value'." }
            if ($ip.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) {
                if ($prefix -lt 24 -or $prefix -gt 32) { throw "IPv4 pilot CIDR '$value' must use a pilot-scoped /24 through /32 prefix." }
            }
            elseif ($prefix -lt 64 -or $prefix -gt 128) { throw "IPv6 pilot CIDR '$value' must use a pilot-scoped /64 through /128 prefix." }
        }
        $result += $value
    }
    return @($result | Sort-Object -Unique)
}

function Get-FirewallSnapshot {
    $rules = @(Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue)
    if ($rules.Count -gt 1) { throw "Multiple firewall rules are named '$firewallRuleName'." }
    if ($rules.Count -eq 0) { return [pscustomobject]@{ Existed = $false } }
    $rule = $rules[0]
    $port = $rule | Get-NetFirewallPortFilter
    $address = $rule | Get-NetFirewallAddressFilter
    return [pscustomobject]@{
        Existed = $true
        Enabled = [string]$rule.Enabled
        Direction = [string]$rule.Direction
        Action = [string]$rule.Action
        Profile = [string]$rule.Profile
        Protocol = [string]$port.Protocol
        LocalPort = @($port.LocalPort)
        RemoteAddress = @($address.RemoteAddress)
    }
}

function Assert-FirewallAvailable {
    param([Parameter(Mandatory = $true)]$Snapshot, [Parameter(Mandatory = $true)][string[]]$RemoteAddress)
    if (-not $Snapshot.Existed) { return }
    $expectedPorts = @($applications.HttpsPort | ForEach-Object { [string]$_ } | Sort-Object)
    $actualPorts = @($Snapshot.LocalPort | ForEach-Object { ([string]$_) -split ',' } | ForEach-Object { $_.Trim() } | Sort-Object)
    $actualRemotes = @($Snapshot.RemoteAddress | ForEach-Object { ([string]$_) -split ',' } | ForEach-Object { $_.Trim() } | Sort-Object)
    $expectedRemotes = @($RemoteAddress | Sort-Object)
    $profile = ([string]$Snapshot.Profile -replace '\s', '')
    $exact = $Snapshot.Enabled -eq 'True' -and $Snapshot.Direction -eq 'Inbound' -and
        $Snapshot.Action -eq 'Allow' -and $profile -in @('Domain,Private', 'Private,Domain') -and
        $Snapshot.Protocol -eq 'TCP' -and
        (($actualPorts -join ',') -eq ($expectedPorts -join ',')) -and
        (($actualRemotes -join ',') -eq ($expectedRemotes -join ','))
    if (-not $exact) { throw "Existing firewall rule '$firewallRuleName' is not the exact requested pilot rule; it was not modified." }
}

function Write-State {
    param([Parameter(Mandatory = $true)]$State)
    $directory = Split-Path -Parent $StatePath
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    $temporary = "$StatePath.tmp"
    $State | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $StatePath -Force
}

function Set-TargetBindingsFromSnapshot {
    param([Parameter(Mandatory = $true)][object[]]$TargetBindings)
    $targetPorts = @($applications.HttpsPort)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        foreach ($site in $manager.Sites) {
            foreach ($binding in @($site.Bindings)) {
                $parts = $binding.BindingInformation -split ':', 3
                if ($parts.Count -ge 2 -and [int]$parts[1] -in $targetPorts) { $site.Bindings.Remove($binding) }
            }
        }
        foreach ($snapshot in @($TargetBindings)) {
            $site = $manager.Sites[$snapshot.Site]
            if (-not $site) { throw "Cannot restore missing IIS site '$($snapshot.Site)'." }
            $binding = $site.Bindings.Add($snapshot.BindingInformation, $snapshot.Protocol)
            if ($snapshot.Protocol -eq 'https') {
                $binding.CertificateHash = Convert-HexToBytes $snapshot.CertificateHash
                $binding.CertificateStoreName = $snapshot.CertificateStoreName
                $binding.SslFlags = [Microsoft.Web.Administration.SslFlags]$snapshot.SslFlags
            }
        }
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}

function Add-HttpsBindings {
    param([Parameter(Mandatory = $true)][string]$Thumbprint)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        foreach ($application in $applications) {
            $site = $manager.Sites[$application.Site]
            if (-not $site) { throw "IIS site '$($application.Site)' does not exist." }
            $information = "*:$($application.HttpsPort):"
            $existing = @($site.Bindings | Where-Object { $_.Protocol -eq 'https' -and $_.BindingInformation -eq $information })
            if ($existing.Count -eq 0) {
                $binding = $site.Bindings.Add($information, 'https')
                $binding.CertificateHash = Convert-HexToBytes $Thumbprint
                $binding.CertificateStoreName = $certificateStoreName
                $binding.SslFlags = [Microsoft.Web.Administration.SslFlags]::None
            }
        }
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}

function Wait-Health {
    param([ValidateSet('http', 'https')][string]$Scheme, [int[]]$Ports)
    $pending = @($Ports)
    $deadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
    do {
        foreach ($port in @($pending)) {
            try {
                $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri "${Scheme}://$expectedComputerName`:$port/api/health" -TimeoutSec 10
                if ($response.StatusCode -eq 200) { $pending = @($pending | Where-Object { $_ -ne $port }) }
            }
            catch { }
        }
        if ($pending.Count -gt 0) { Start-Sleep -Milliseconds 750 }
    } while ($pending.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($pending.Count -gt 0) { throw "${Scheme} health verification timed out on ports: $($pending -join ', ')." }
}

function Invoke-AutomaticRollback {
    param([Parameter(Mandatory = $true)]$State)
    try {
        Set-TargetBindingsFromSnapshot -TargetBindings @($State.PriorTargetBindings)
        if ($State.FirewallRuleAdded) {
            Remove-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue
        }
        Wait-Health -Scheme http -Ports @($applications.HttpPort)
        $State.Status = 'AutomaticallyRolledBack'
        $State.RolledBackAtUtc = [DateTime]::UtcNow.ToString('o')
        Write-State $State
    }
    catch { throw "Automatic rollback failed: $($_.Exception.Message)" }
}

if (-not [IO.Path]::IsPathRooted($StatePath)) { throw 'StatePath must be an absolute local path.' }
$StatePath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($StatePath))
Assert-Host
if (-not $WhatIfPreference) { Assert-Administrator }
Import-IisAdministration

if ($Rollback) {
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { throw "Rollback state was not found at '$StatePath'." }
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if ($state.ComputerName -ine $expectedComputerName -or $state.Version -ne 1) { throw 'Rollback state does not match this transaction.' }
    if ($state.Status -ne 'Applied') { throw "Rollback state is '$($state.Status)', not Applied." }
    $currentTarget = @(Get-TargetBindingSnapshot (Get-IisBindingSnapshot)) | ConvertTo-Json -Depth 8 -Compress
    $appliedTarget = @($state.AppliedTargetBindings) | ConvertTo-Json -Depth 8 -Compress
    if ($currentTarget -ne $appliedTarget) { throw 'Current pilot HTTPS bindings have drifted since apply; rollback refused.' }
    if ($state.FirewallRuleAdded) {
        $currentFirewall = Get-FirewallSnapshot
        if (-not $currentFirewall.Existed) { throw 'The pilot firewall rule was removed after apply; rollback refused due to drift.' }
        Assert-FirewallAvailable -Snapshot $currentFirewall -RemoteAddress @($state.PilotRemoteAddress)
    }
    if ($PSCmdlet.ShouldProcess($expectedComputerName, 'Restore the prior pilot HTTPS bindings and firewall state')) {
        Set-TargetBindingsFromSnapshot -TargetBindings @($state.PriorTargetBindings)
        if ($state.FirewallRuleAdded) { Remove-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction Stop }
        Assert-RequiredHttpBindings (Get-IisBindingSnapshot)
        Wait-Health -Scheme http -Ports @($applications.HttpPort)
        $state.Status = 'RolledBack'
        $state.RolledBackAtUtc = [DateTime]::UtcNow.ToString('o')
        Write-State $state
        Write-Output 'HTTPS_PILOT_ROLLED_BACK_AND_HTTP_HEALTHY'
    }
    elseif ($WhatIfPreference) { Write-Output 'WHATIF_READY_ROLLBACK: rollback state and drift checks passed; nothing was changed.' }
    else { Write-Output 'HTTPS_PILOT_ROLLBACK_CANCELLED' }
    exit 0
}

$thumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
$rootThumbprint = ($PilotRootThumbprint -replace '\s', '').ToUpperInvariant()
$remoteAddresses = Convert-ToPilotAddress $PilotRemoteAddress
Write-Warning 'PILOT ONLY: revocation is not checked because the pilot CA has no CRL/OCSP service. The exact trusted root and leaf chain are still verified.'
$null = Assert-Certificate -Thumbprint $thumbprint -RootThumbprint $rootThumbprint
$iisBefore = @(Get-IisBindingSnapshot)
Assert-RequiredHttpBindings $iisBefore
Assert-TargetBindingsAvailable -Snapshot $iisBefore -Thumbprint $thumbprint
$firewallBefore = Get-FirewallSnapshot
Assert-FirewallAvailable -Snapshot $firewallBefore -RemoteAddress $remoteAddresses

if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
    $oldState = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if ($oldState.Status -eq 'Applied') { throw "An applied transaction already exists at '$StatePath'; roll it back before applying another." }
}

$state = [pscustomobject]@{
    Version = 1
    ComputerName = $expectedComputerName
    Status = 'Prepared'
    PreparedAtUtc = [DateTime]::UtcNow.ToString('o')
    CertificateThumbprint = $thumbprint
    PilotRootThumbprint = $rootThumbprint
    PilotRemoteAddress = $remoteAddresses
    AllBindingsBefore = $iisBefore
    PriorTargetBindings = @(Get-TargetBindingSnapshot $iisBefore)
    FirewallBefore = $firewallBefore
    FirewallRuleAdded = $false
    AppliedTargetBindings = @()
}

if (-not $PSCmdlet.ShouldProcess($expectedComputerName, "Add pilot HTTPS bindings and firewall rule for $($remoteAddresses -join ', ')")) {
    if ($WhatIfPreference) { Write-Output 'WHATIF_READY: certificate, host, IIS bindings, ports, and pilot firewall inputs passed preflight; nothing was changed.' }
    else { Write-Output 'HTTPS_PILOT_CONFIGURATION_CANCELLED' }
    exit 0
}

Write-State $state
try {
    Add-HttpsBindings $thumbprint
    if (-not $firewallBefore.Existed) {
        $state.FirewallRuleAdded = $true
        Write-State $state
        New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow -Enabled True `
            -Profile Domain,Private -Protocol TCP -LocalPort @($applications.HttpsPort) `
            -RemoteAddress $remoteAddresses | Out-Null
    }
    $iisAfter = @(Get-IisBindingSnapshot)
    Assert-RequiredHttpBindings $iisAfter
    Assert-TargetBindingsAvailable -Snapshot $iisAfter -Thumbprint $thumbprint
    Assert-FirewallAvailable -Snapshot (Get-FirewallSnapshot) -RemoteAddress $remoteAddresses
    Wait-Health -Scheme https -Ports @($applications.HttpsPort)
    Wait-Health -Scheme http -Ports @($applications.HttpPort)
    $state.Status = 'Applied'
    $state.AppliedAtUtc = [DateTime]::UtcNow.ToString('o')
    $state.AppliedTargetBindings = @(Get-TargetBindingSnapshot $iisAfter)
    Write-State $state
    Write-Output 'HTTPS_PILOT_CONFIGURED_AND_DUAL_SCHEME_HEALTHY'
}
catch {
    $failure = $_.Exception.Message
    Invoke-AutomaticRollback $state
    throw "HTTPS pilot transaction failed and was rolled back: $failure"
}
