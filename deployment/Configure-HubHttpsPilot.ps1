<#
    Additive pilot HTTPS transaction for the five SON-AERO Hub IIS sites on SON-IIS2.

    Preview:
      .\Configure-HubHttpsPilot.ps1 -CertificateThumbprint <LEAF> -PilotRootThumbprint <ROOT> -PilotRemoteAddress 10.50.10.25 -WhatIf
    Apply:
      .\Configure-HubHttpsPilot.ps1 -CertificateThumbprint <LEAF> -PilotRootThumbprint <ROOT> -PilotRemoteAddress 10.50.10.25 -Confirm:$false
    Roll back the last successful apply:
      .\Configure-HubHttpsPilot.ps1 -Rollback -Confirm:$false
    Secure an already-deployed, otherwise valid legacy transaction state:
      .\Configure-HubHttpsPilot.ps1 -MigrateLegacyStateProtection -WhatIf

    HTTP bindings on ports 5135-5170 are never removed. The pilot firewall rule is separate
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
    [Parameter(Mandatory = $true, ParameterSetName = 'MigrateLegacyStateProtection')]
    [switch]$MigrateLegacyStateProtection,
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
$stateRoot = 'C:\ProgramData\SonAero\deployment-state'
$legacyStatePath = 'C:\ProgramData\SonAero\deployment-state\https-pilot.json'
$bindingTransactionMutexName = 'Global\SonAero-HubHttpsBindingTransactions'
$requiredDnsNames = @('SON-IIS2', 'SON-IIS2.SON4L.LOCAL')
$applications = @(
    [pscustomobject]@{ Site = 'ProjectTracker'; HttpPort = 5135; HttpsPort = 6135 },
    [pscustomobject]@{ Site = 'SonAeroPortal'; HttpPort = 5140; HttpsPort = 6140 },
    [pscustomobject]@{ Site = 'EngineeringHub'; HttpPort = 5150; HttpsPort = 6150 },
    [pscustomobject]@{ Site = 'EstimatingDashboard'; HttpPort = 5160; HttpsPort = 6160 },
    [pscustomobject]@{ Site = 'QualityAssurance'; HttpPort = 5170; HttpsPort = 6170 }
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
function Enter-HubHttpsBindingTransactionLock {
    $mutex = New-Object Threading.Mutex($false, $bindingTransactionMutexName)
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) {
            throw 'Another SON-AERO HTTPS binding transaction is already running on SON-IIS2.'
        }
        return $mutex
    }
    catch {
        if (-not $acquired) { $mutex.Dispose() }
        throw
    }
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
function Get-CertificateRawSha256 {
    param([Parameter(Mandatory)]$Certificate)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($Certificate.RawData))).Replace('-', '')
    }
    finally { $sha256.Dispose() }
}
function Get-CertificateEkuOidValues {
    param([Parameter(Mandatory)]$Certificate)
    $extensions = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($extensions.Count -ne 1) { throw "Certificate must contain exactly one Enhanced Key Usage extension; found $($extensions.Count)." }
    try {
        $parsed = New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
            $extensions[0], $extensions[0].Critical)
        $values = @($parsed.EnhancedKeyUsages | ForEach-Object { [string]$_.Value } | Where-Object { $_ })
    }
    catch { throw "Certificate Enhanced Key Usage extension could not be parsed: $($_.Exception.Message)" }
    if ($values.Count -eq 0) { throw 'Certificate Enhanced Key Usage extension contains no usable OIDs.' }
    return $values
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
    $eku = @(Get-CertificateEkuOidValues -Certificate $certificate)
    if ($eku -notcontains '1.3.6.1.5.5.7.3.1') { throw 'The certificate lacks the Server Authentication EKU.' }
    $keyUsageExtensions = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.15' })
    if ($keyUsageExtensions.Count -gt 1) { throw 'The certificate contains duplicate Key Usage extensions.' }
    if ($keyUsageExtensions.Count -eq 1) {
        try {
            $keyUsage = New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                $keyUsageExtensions[0], $keyUsageExtensions[0].Critical)
        }
        catch { throw "Certificate Key Usage extension could not be parsed: $($_.Exception.Message)" }
        if (($keyUsage.KeyUsages -band [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -eq 0) {
            throw 'The certificate Key Usage extension does not allow Digital Signature.'
        }
    }
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
        $chain.ChainPolicy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        $chain.ChainPolicy.VerificationTime = $now
        $chainBuilt = $chain.Build($certificate)
        $allChainStatuses = @($chain.ChainStatus)
        foreach ($element in @($chain.ChainElements)) {
            $allChainStatuses += @($element.ChainElementStatus)
        }
        $nonZeroChainStatuses = @($allChainStatuses | Where-Object {
            $_.Status -ne [Security.Cryptography.X509Certificates.X509ChainStatusFlags]::NoError
        })
        if (-not $chainBuilt -or $nonZeroChainStatuses.Count -gt 0) {
            $details = @($nonZeroChainStatuses | ForEach-Object {
                "$($_.Status): $($_.StatusInformation.Trim())"
            } | Select-Object -Unique) -join '; '
            throw "Certificate chain validation failed: $details"
        }
        $elements = @($chain.ChainElements)
        if ($elements.Count -ne 2 -or
            (Convert-HashToHex $elements[0].Certificate.Thumbprint) -ne $Thumbprint -or
            (Convert-HashToHex $elements[$elements.Count - 1].Certificate.Thumbprint) -ne $RootThumbprint) {
            throw 'The pilot chain must contain exactly the selected leaf and explicit pilot root.'
        }
        $builtRootSha256 = Get-CertificateRawSha256 -Certificate $elements[1].Certificate
        $selectedRootSha256 = Get-CertificateRawSha256 -Certificate $rootCertificate
        if ($builtRootSha256 -ne $selectedRootSha256) {
            throw 'The built pilot root certificate bytes do not match the explicitly loaded pilot root certificate.'
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
function Get-CanonicalStatePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    try {
        return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
    }
    catch {
        throw "StatePath '$Path' is not a valid local path: $($_.Exception.Message)"
    }
}

function Assert-SafeStatePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullRoot = (Get-CanonicalStatePath $stateRoot).TrimEnd('\')
    $sonAeroRoot = Split-Path -Parent $fullRoot
    $commonApplicationData = (Get-CanonicalStatePath `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData))).TrimEnd('\')
    if ((Split-Path -Parent $sonAeroRoot) -ine $commonApplicationData -or
        (Split-Path -Leaf $sonAeroRoot) -ine 'SonAero' -or
        (Split-Path -Leaf $fullRoot) -ine 'deployment-state') {
        throw "Protected state root '$fullRoot' must be the SonAero\deployment-state directory directly under '$commonApplicationData'."
    }
    $fullPath = Get-CanonicalStatePath $Path
    if ((Split-Path -Parent $fullPath) -ine $fullRoot -or
        [IO.Path]::GetExtension($fullPath) -ine '.json') {
        throw "StatePath must be a JSON file directly under '$fullRoot'."
    }
    return $fullPath
}

function Assert-NoReparsePointInStatePathChain {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullPath = Get-CanonicalStatePath $Path
    $paths = @()
    $current = $fullPath
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        $paths += $current
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) { break }
        $current = $parent
    }
    for ($index = $paths.Count - 1; $index -ge 0; $index--) {
        $candidate = $paths[$index]
        try {
            $attributes = [IO.File]::GetAttributes($candidate)
        }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        catch {
            throw "Could not inspect protected pilot state path ancestor '$candidate': $($_.Exception.Message)"
        }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Protected pilot state path ancestor '$candidate' must not be a reparse point."
        }
    }
}

function New-ProtectedFileSystemSecurity {
    param([switch]$Directory)
    $security = if ($Directory) { New-Object Security.AccessControl.DirectorySecurity }
        else { New-Object Security.AccessControl.FileSecurity }
    $security.SetAccessRuleProtection($true, $false)
    $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $security.SetOwner($administrators)
    $inheritance = if ($Directory) {
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    }
    else { [Security.AccessControl.InheritanceFlags]::None }
    foreach ($identity in @($system, $administrators)) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $identity,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow
        )
        [void]$security.AddAccessRule($rule)
    }
    return $security
}

function Assert-ProtectedStatePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$Directory
    )
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Protected pilot state path '$Path' must not be a reparse point."
    }
    if ($Directory -and -not $item.PSIsContainer) {
        throw "Protected pilot state directory '$Path' is not a directory."
    }
    if (-not $Directory -and $item.PSIsContainer) {
        throw "Protected pilot state file '$Path' is not a file."
    }
    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) {
        throw "Protected pilot state path '$Path' still inherits access rules."
    }
    $allowedSids = @('S-1-5-18', 'S-1-5-32-544')
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin $allowedSids) {
        throw "Protected pilot state path '$Path' has unexpected owner '$owner'."
    }
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 2) {
        throw "Protected pilot state path '$Path' must contain exactly two access rules."
    }
    $fullControlSids = @()
    foreach ($rule in $rules) {
        $sid = $rule.IdentityReference.Value
        if ($sid -notin $allowedSids -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) {
            throw "Protected pilot state path '$Path' grants access to unexpected identity '$sid'."
        }
        if (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq
            [Security.AccessControl.FileSystemRights]::FullControl) {
            $fullControlSids += $sid
        }
    }
    foreach ($sid in $allowedSids) {
        if ($fullControlSids -notcontains $sid) {
            throw "Protected pilot state path '$Path' does not grant full control to '$sid'."
        }
    }
}

function Assert-PilotStateProtection {
    param([Parameter(Mandatory = $true)][string]$Path)
    Assert-ProtectedStatePath -Path (Split-Path -Parent $Path) -Directory
    Assert-ProtectedStatePath -Path $Path
}

function Test-ProtectedStateExists {
    Assert-NoReparsePointInStatePathChain -Path $StatePath
    $directory = Split-Path -Parent $StatePath
    if (Get-Item -LiteralPath $directory -Force -ErrorAction SilentlyContinue) {
        Assert-ProtectedStatePath -Path $directory -Directory
    }
    if (-not (Get-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue)) { return $false }
    Assert-PilotStateProtection -Path $StatePath
    return $true
}

function Read-State {
    param(
        [Parameter(Mandatory = $true)][string]$MissingMessage,
        [Parameter(Mandatory = $true)][string]$InvalidJsonLabel
    )
    Assert-NoReparsePointInStatePathChain -Path $StatePath
    if (-not (Test-ProtectedStateExists)) { throw $MissingMessage }
    # Repeat the check immediately before reading so retained JSON is never trusted first.
    Assert-NoReparsePointInStatePathChain -Path $StatePath
    Assert-PilotStateProtection -Path $StatePath
    try { return Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json }
    catch { throw "$InvalidJsonLabel at '$StatePath' is not valid JSON: $($_.Exception.Message)" }
}

function Initialize-ProtectedStateDirectory {
    Assert-NoReparsePointInStatePathChain -Path $StatePath
    $directory = Split-Path -Parent $StatePath
    if (Get-Item -LiteralPath $directory -Force -ErrorAction SilentlyContinue) {
        Assert-ProtectedStatePath -Path $directory -Directory
        return
    }
    $security = New-ProtectedFileSystemSecurity -Directory
    [void][IO.Directory]::CreateDirectory($directory, $security)
    Assert-NoReparsePointInStatePathChain -Path $StatePath
    Assert-ProtectedStatePath -Path $directory -Directory
}

function Write-State {
    param([Parameter(Mandatory = $true)]$State)
    Initialize-ProtectedStateDirectory
    Assert-NoReparsePointInStatePathChain -Path $StatePath
    if (Get-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue) {
        Assert-PilotStateProtection -Path $StatePath
    }
    $directory = Split-Path -Parent $StatePath
    $temporary = Join-Path $directory ((Split-Path -Leaf $StatePath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Assert-NoReparsePointInStatePathChain -Path $temporary
        $json = ($State | ConvertTo-Json -Depth 12) + [Environment]::NewLine
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json)
        $stream = [IO.File]::Open(
            $temporary,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None
        )
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
        Assert-NoReparsePointInStatePathChain -Path $temporary
        $temporaryItem = Get-Item -LiteralPath $temporary -Force -ErrorAction Stop
        if ($temporaryItem.PSIsContainer -or
            ($temporaryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Temporary pilot state path '$temporary' must be a regular file, not a directory or reparse point."
        }
        Set-Acl -LiteralPath $temporary -AclObject (New-ProtectedFileSystemSecurity)
        Assert-ProtectedStatePath -Path $temporary
        if (Get-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue) {
            Assert-NoReparsePointInStatePathChain -Path $StatePath
            Assert-PilotStateProtection -Path $StatePath
            [IO.File]::Replace($temporary, $StatePath, $null)
        }
        else {
            Move-Item -LiteralPath $temporary -Destination $StatePath
        }
        Assert-PilotStateProtection -Path $StatePath
    }
    finally {
        $temporaryItem = Get-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        if ($temporaryItem -and -not $temporaryItem.PSIsContainer -and
            ($temporaryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-ComparableTargetBindings {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Bindings
    )
    return (@($Bindings | ForEach-Object {
        '{0}|{1}|{2}|{3}|{4}|{5}' -f
            ([string]$_.Site),
            ([string]$_.Protocol),
            ([string]$_.BindingInformation),
            (Convert-HashToHex $_.CertificateHash),
            ([string]$_.CertificateStoreName),
            ([int]$_.SslFlags)
    } | Sort-Object) -join "`n")
}

function Assert-PriorTargetBindings {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Bindings,
        [Parameter(Mandatory = $true)][string]$Thumbprint
    )
    $seen = @{}
    foreach ($binding in @($Bindings)) {
        foreach ($property in @('Site', 'Protocol', 'BindingInformation', 'CertificateHash', 'CertificateStoreName', 'SslFlags')) {
            if ($binding.PSObject.Properties.Name -notcontains $property) {
                throw "Rollback state binding is missing '$property'."
            }
        }
        $application = @($applications | Where-Object Site -EQ ([string]$binding.Site))
        if ($application.Count -ne 1) { throw "Rollback state contains an unknown IIS site '$($binding.Site)'." }
        $expectedInformation = "*:$($application[0].HttpsPort):"
        $key = "$($binding.Site)|$($binding.Protocol)|$($binding.BindingInformation)"
        if ($seen.ContainsKey($key)) { throw "Rollback state contains duplicate binding '$key'." }
        $seen[$key] = $true
        if ([string]$binding.Protocol -ne 'https' -or
            [string]$binding.BindingInformation -ne $expectedInformation -or
            (Convert-HashToHex $binding.CertificateHash) -ne $Thumbprint -or
            [string]$binding.CertificateStoreName -ne $certificateStoreName -or
            [int]$binding.SslFlags -ne 0) {
            throw "Rollback state contains an out-of-scope or conflicting target binding '$key'."
        }
    }
}

function Assert-StateProperties {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string[]]$Names
    )
    foreach ($name in $Names) {
        if ($State.PSObject.Properties.Name -notcontains $name) {
            throw "Rollback state is missing required property '$name'."
        }
        if ($null -eq $State.$name) {
            throw "Rollback state property '$name' is null."
        }
    }
}

function Assert-ExactPropertySet {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string[]]$Names,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if ($Value -isnot [pscustomobject]) { throw "$Label must be one JSON object." }
    $actual = @($Value.PSObject.Properties.Name | Sort-Object -CaseSensitive)
    $expected = @($Names | Sort-Object -CaseSensitive)
    if ($actual.Count -ne $expected.Count -or ($actual -join "`n") -cne ($expected -join "`n")) {
        throw "$Label has an unexpected or missing property; legacy migration accepts only the exact v1 schema."
    }
}

function ConvertFrom-StrictUtcTimestamp {
    param([Parameter(Mandatory = $true)][string]$Value, [Parameter(Mandatory = $true)][string]$Name)
    $parsed = [DateTimeOffset]::MinValue
    $valid = [DateTimeOffset]::TryParseExact(
        $Value,
        'o',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$parsed
    )
    if (-not $valid -or $parsed.Offset -ne [TimeSpan]::Zero) {
        throw "Legacy pilot state '$Name' must be an exact UTC round-trip timestamp."
    }
    return $parsed
}

function Assert-BindingSnapshotShape {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Bindings,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $properties = @('Site', 'Protocol', 'BindingInformation', 'CertificateHash', 'CertificateStoreName', 'SslFlags')
    foreach ($binding in @($Bindings)) {
        Assert-ExactPropertySet -Value $binding -Names $properties -Label $Label
        if ($binding.Site -isnot [string] -or $binding.Protocol -isnot [string] -or
            $binding.BindingInformation -isnot [string] -or $binding.CertificateHash -isnot [string] -or
            $binding.CertificateStoreName -isnot [string] -or $binding.SslFlags -isnot [int]) {
            throw "$Label contains a binding with an invalid v1 property type."
        }
    }
}

function New-ExpectedPilotBindingSnapshot {
    param([Parameter(Mandatory = $true)][string]$Thumbprint)
    return @($applications | ForEach-Object {
        [pscustomobject]@{
            Site = $_.Site
            Protocol = 'https'
            BindingInformation = "*:$($_.HttpsPort):"
            CertificateHash = $Thumbprint
            CertificateStoreName = $certificateStoreName
            SslFlags = 0
        }
    })
}

function Assert-StrictLegacyPilotState {
    param([Parameter(Mandatory = $true)]$State)
    $stateProperties = @(
        'Version', 'ComputerName', 'Status', 'PreparedAtUtc', 'CertificateThumbprint',
        'PilotRootThumbprint', 'PilotRemoteAddress', 'AllBindingsBefore', 'PriorTargetBindings',
        'FirewallBefore', 'FirewallRuleAdded', 'AppliedTargetBindings', 'AppliedAtUtc',
        'RolledBackAtUtc', 'ApplyFailure', 'ApplyFailedAtUtc', 'RollbackFailure', 'RollbackFailedAtUtc'
    )
    Assert-ExactPropertySet -Value $State -Names $stateProperties -Label 'Legacy pilot state'
    if ($State.Version -isnot [int] -or $State.Version -ne 1 -or
        $State.ComputerName -isnot [string] -or [string]$State.ComputerName -cne $expectedComputerName -or
        $State.Status -isnot [string] -or [string]$State.Status -cne 'Applied') {
        throw 'Legacy migration requires exact v1 SON-IIS2 state with status Applied.'
    }
    $thumbprint = [string]$State.CertificateThumbprint
    $rootThumbprint = [string]$State.PilotRootThumbprint
    if ($State.CertificateThumbprint -isnot [string] -or $thumbprint -cnotmatch '^[A-F0-9]{40}$' -or
        $State.PilotRootThumbprint -isnot [string] -or $rootThumbprint -cnotmatch '^[A-F0-9]{40}$' -or
        $thumbprint -ceq $rootThumbprint) {
        throw 'Legacy pilot state leaf/root thumbprints must be distinct uppercase SHA-1 hex strings.'
    }
    if ($State.PilotRemoteAddress -isnot [object[]]) {
        throw 'Legacy pilot state PilotRemoteAddress must be a JSON array.'
    }
    $rawRemoteAddresses = @($State.PilotRemoteAddress)
    if ($rawRemoteAddresses.Count -eq 0 -or @($rawRemoteAddresses | Where-Object { $_ -isnot [string] }).Count -gt 0) {
        throw 'Legacy pilot state contains no valid pilot remote-address strings.'
    }
    $remoteAddresses = @(Convert-ToPilotAddress -Address @($rawRemoteAddresses))
    if ($remoteAddresses.Count -ne $rawRemoteAddresses.Count -or
        ($remoteAddresses -join "`n") -cne (@($rawRemoteAddresses) -join "`n")) {
        throw 'Legacy pilot state remote addresses are not the exact sorted, unique, constrained v1 values.'
    }
    if ($State.PriorTargetBindings -isnot [object[]] -or @($State.PriorTargetBindings).Count -ne 0) {
        throw 'Legacy migration requires an empty recorded pre-pilot 61xx binding baseline.'
    }
    if ($State.AllBindingsBefore -isnot [object[]] -or $State.AppliedTargetBindings -isnot [object[]]) {
        throw 'Legacy pilot state binding snapshots must be JSON arrays.'
    }
    $before = @($State.AllBindingsBefore)
    $applied = @($State.AppliedTargetBindings)
    Assert-BindingSnapshotShape -Bindings $before -Label 'Legacy AllBindingsBefore'
    Assert-BindingSnapshotShape -Bindings $applied -Label 'Legacy AppliedTargetBindings'
    Assert-RequiredHttpBindings -Snapshot $before
    if (@(Get-TargetBindingSnapshot -Snapshot $before).Count -ne 0) {
        throw 'Legacy AllBindingsBefore must record no preexisting pilot-port bindings.'
    }
    if ($applied.Count -ne $applications.Count) {
        throw 'Legacy AppliedTargetBindings must contain exactly five pilot HTTPS bindings.'
    }
    Assert-PriorTargetBindings -Bindings $applied -Thumbprint $thumbprint
    $expectedBindings = @(New-ExpectedPilotBindingSnapshot -Thumbprint $thumbprint)
    if ((Get-ComparableTargetBindings $applied) -cne (Get-ComparableTargetBindings $expectedBindings)) {
        throw 'Legacy AppliedTargetBindings do not equal the exact five expected pilot bindings.'
    }
    Assert-ExactPropertySet -Value $State.FirewallBefore -Names @('Existed') -Label 'Legacy FirewallBefore'
    if ($State.FirewallBefore.Existed -isnot [bool] -or $State.FirewallBefore.Existed -ne $false -or
        $State.FirewallRuleAdded -isnot [bool] -or $State.FirewallRuleAdded -ne $true) {
        throw 'Legacy migration requires a transaction-created firewall rule and a recorded absent firewall baseline.'
    }
    $preparedAt = ConvertFrom-StrictUtcTimestamp -Value ([string]$State.PreparedAtUtc) -Name 'PreparedAtUtc'
    $appliedAt = ConvertFrom-StrictUtcTimestamp -Value ([string]$State.AppliedAtUtc) -Name 'AppliedAtUtc'
    if ($preparedAt -gt $appliedAt -or $appliedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw 'Legacy pilot state timestamps are out of order or in the future.'
    }
    foreach ($name in @('RolledBackAtUtc', 'ApplyFailure', 'ApplyFailedAtUtc', 'RollbackFailure', 'RollbackFailedAtUtc')) {
        if ($null -ne $State.$name) { throw "Legacy Applied state property '$name' must be null." }
    }
    return [pscustomobject]@{
        State = $State
        Thumbprint = $thumbprint
        RootThumbprint = $rootThumbprint
        RemoteAddresses = $remoteAddresses
        ExpectedBindings = $expectedBindings
    }
}

function Initialize-LegacyStateNativeMethods {
    if ('SonAero.PilotStateNativeMethods' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace SonAero {
    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
    public static class PilotStateNativeMethods {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle handle, out ByHandleFileInformation information);
        public static uint GetLinkCount(SafeFileHandle handle) {
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information)) {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return information.NumberOfLinks;
        }
    }
}
'@ -ErrorAction Stop
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha256.ComputeHash($Bytes))).Replace('-', '') }
    finally { $sha256.Dispose() }
}

function Open-LegacyPilotStateForMigration {
    Assert-NoReparsePointInStatePathChain -Path $StatePath
    $directory = Split-Path -Parent $StatePath
    $directoryItem = Get-Item -LiteralPath $directory -Force -ErrorAction Stop
    $fileItem = Get-Item -LiteralPath $StatePath -Force -ErrorAction Stop
    if (-not $directoryItem.PSIsContainer -or $fileItem.PSIsContainer -or
        ($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ($fileItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Legacy pilot state must be one regular file in a regular deployment-state directory.'
    }
    if ($fileItem.Length -le 0 -or $fileItem.Length -gt 1MB) {
        throw 'Legacy pilot state must be nonempty and no larger than 1 MiB.'
    }
    Initialize-LegacyStateNativeMethods
    Assert-NoReparsePointInStatePathChain -Path $StatePath
    $rights = [Security.AccessControl.FileSystemRights]::ReadData -bor
        [Security.AccessControl.FileSystemRights]::ReadAttributes -bor
        [Security.AccessControl.FileSystemRights]::ReadPermissions -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    $stream = [IO.FileStream]::new(
        $StatePath, [IO.FileMode]::Open, $rights, [IO.FileShare]::None, 4096, [IO.FileOptions]::SequentialScan)
    try {
        if ([SonAero.PilotStateNativeMethods]::GetLinkCount($stream.SafeFileHandle) -ne 1) {
            throw 'Legacy pilot state must have exactly one filesystem link.'
        }
        if ($stream.Length -le 0 -or $stream.Length -gt 1MB -or $stream.Length -ne $fileItem.Length) {
            throw 'Legacy pilot state size changed or is outside the 1 MiB migration bound.'
        }
        [byte[]]$bytes = New-Object byte[] ([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { throw 'Legacy pilot state ended before its recorded file length.' }
            $offset += $read
        }
        $textOffset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $textOffset = 3
        }
        elseif ($bytes.Length -ge 2 -and
            (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or ($bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF))) {
            throw 'Legacy pilot state must be UTF-8, not UTF-16.'
        }
        $utf8 = New-Object Text.UTF8Encoding($false, $true)
        try { $json = $utf8.GetString($bytes, $textOffset, $bytes.Length - $textOffset) }
        catch { throw "Legacy pilot state is not strict UTF-8: $($_.Exception.Message)" }
        try { $state = $json | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "Legacy pilot state is not valid JSON: $($_.Exception.Message)" }
        $stream.Position = 0
        return [pscustomobject]@{
            Stream = $stream
            Bytes = $bytes
            Sha256 = Get-Sha256Hex -Bytes $bytes
            Validated = Assert-StrictLegacyPilotState -State $state
        }
    }
    catch {
        $stream.Dispose()
        throw
    }
}

function Assert-LegacyPilotLiveState {
    param([Parameter(Mandatory = $true)]$Validated)
    $null = Assert-Certificate -Thumbprint $Validated.Thumbprint -RootThumbprint $Validated.RootThumbprint
    $liveIis = @(Get-IisBindingSnapshot)
    Assert-RequiredHttpBindings -Snapshot $liveIis
    Assert-TargetBindingsAvailable -Snapshot $liveIis -Thumbprint $Validated.Thumbprint
    $liveTarget = @(Get-TargetBindingSnapshot -Snapshot $liveIis)
    if ($liveTarget.Count -ne $applications.Count -or
        (Get-ComparableTargetBindings $liveTarget) -cne (Get-ComparableTargetBindings $Validated.ExpectedBindings)) {
        throw 'Live pilot bindings do not equal the exact five bindings recorded in validated legacy state.'
    }
    $liveFirewall = Get-FirewallSnapshot
    if (-not $liveFirewall.Existed) { throw "Required live firewall rule '$firewallRuleName' was not found." }
    Assert-FirewallAvailable -Snapshot $liveFirewall -RemoteAddress @($Validated.RemoteAddresses)
    Wait-Health -Scheme https -Ports @($applications.HttpsPort)
    Wait-Health -Scheme http -Ports @($applications.HttpPort)
}

function Test-PilotStateProtectionCurrent {
    try { Assert-PilotStateProtection -Path $StatePath; return $true }
    catch { return $false }
}

function Try-WriteState {
    param([Parameter(Mandatory = $true)]$State)
    try {
        Write-State $State
        return $null
    }
    catch {
        return $_.Exception.Message
    }
}

function Set-StateProperty {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$Name,
        $Value
    )
    if ($State.PSObject.Properties.Name -contains $Name) {
        $State.$Name = $Value
    }
    else {
        $State | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
    }
}

function Set-TargetBindingsFromSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$TargetBindings
    )
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
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$OriginalFailure
    )
    $State.Status = 'ApplyFailedRollbackPending'
    Set-StateProperty -State $State -Name 'ApplyFailure' -Value $OriginalFailure
    Set-StateProperty -State $State -Name 'ApplyFailedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
    $pendingStateFailure = Try-WriteState $State
    try {
        Assert-PriorTargetBindings -Bindings @($State.PriorTargetBindings) -Thumbprint ([string]$State.CertificateThumbprint)
        $currentIis = @(Get-IisBindingSnapshot)
        Assert-TargetBindingsAvailable -Snapshot $currentIis -Thumbprint ([string]$State.CertificateThumbprint)
        $currentFirewall = $null
        if ($State.FirewallRuleAdded) {
            $currentFirewall = Get-FirewallSnapshot
            if ($currentFirewall.Existed) {
                Assert-FirewallAvailable -Snapshot $currentFirewall -RemoteAddress @($State.PilotRemoteAddress)
            }
        }
        Set-TargetBindingsFromSnapshot -TargetBindings @($State.PriorTargetBindings)
        if ($State.FirewallRuleAdded -and $currentFirewall.Existed) {
            Remove-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction Stop
        }
        $restoredIis = @(Get-IisBindingSnapshot)
        Assert-RequiredHttpBindings $restoredIis
        $restoredTarget = Get-ComparableTargetBindings -Bindings @(Get-TargetBindingSnapshot $restoredIis)
        $expectedTarget = Get-ComparableTargetBindings -Bindings @($State.PriorTargetBindings)
        if ($restoredTarget -ne $expectedTarget) {
            throw 'Restored pilot HTTPS bindings do not exactly match the recorded pre-transaction state.'
        }
        Wait-Health -Scheme http -Ports @($applications.HttpPort)
        $State.Status = 'AutomaticallyRolledBack'
        Set-StateProperty -State $State -Name 'RolledBackAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
        Set-StateProperty -State $State -Name 'RollbackFailure' -Value $null
        Write-State $State
    }
    catch {
        $rollbackFailure = $_.Exception.Message
        $State.Status = 'RollbackFailed'
        Set-StateProperty -State $State -Name 'RollbackFailure' -Value $rollbackFailure
        Set-StateProperty -State $State -Name 'RollbackFailedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
        $rollbackStateFailure = Try-WriteState $State
        $stateDetails = @()
        if ($pendingStateFailure) { $stateDetails += "Could not persist the original apply failure before rollback: $pendingStateFailure" }
        if ($rollbackStateFailure) { $stateDetails += "Could not persist the rollback failure: $rollbackStateFailure" }
        $suffix = if ($stateDetails.Count -gt 0) { " State persistence errors: $($stateDetails -join ' ')" } else { '' }
        throw "HTTPS pilot transaction failed. Original apply failure: $OriginalFailure Automatic rollback failure: $rollbackFailure$suffix Run -Rollback -WhatIf with the updated script before retrying apply."
    }
}

if (-not [IO.Path]::IsPathRooted($StatePath)) { throw 'StatePath must be an absolute local path.' }
$StatePath = Assert-SafeStatePath $StatePath
if ($MigrateLegacyStateProtection -and $StatePath -ine (Get-CanonicalStatePath $legacyStatePath)) {
    throw "Legacy migration is restricted to the exact deployed state path '$legacyStatePath'."
}
Assert-Host
if ($MigrateLegacyStateProtection -or -not $WhatIfPreference) { Assert-Administrator }
Import-IisAdministration
$transactionMutex = Enter-HubHttpsBindingTransactionLock

try {
if ($MigrateLegacyStateProtection) {
    $openedState = $null
    $originalHash = $null
    $alreadyProtected = $false
    try {
        # This is the only code path allowed to parse an unprotected state file. It is bounded to
        # the exact deployed v1 path and never supplies rollback authority until live corroboration.
        $openedState = Open-LegacyPilotStateForMigration
        $originalHash = $openedState.Sha256
        Assert-LegacyPilotLiveState -Validated $openedState.Validated
        $alreadyProtected = Test-PilotStateProtectionCurrent
        if ($alreadyProtected) {
            Write-Output 'HTTPS_PILOT_STATE_PROTECTION_ALREADY_CURRENT'
            exit 0
        }
        if (-not $PSCmdlet.ShouldProcess(
            $legacyStatePath,
            'Protect the validated legacy state file and directory with SYSTEM/Administrators-only ACLs')) {
            if ($WhatIfPreference) {
                Write-Output 'WHATIF_READY_HTTPS_PILOT_STATE_PROTECTION_MIGRATION'
            }
            else { Write-Output 'HTTPS_PILOT_STATE_PROTECTION_MIGRATION_CANCELLED' }
            exit 0
        }
        # Revalidate both immutable content and external authority immediately before the ACL-only change.
        $openedState.Stream.Position = 0
        [byte[]]$currentBytes = New-Object byte[] ([int]$openedState.Stream.Length)
        $currentOffset = 0
        while ($currentOffset -lt $currentBytes.Length) {
            $currentRead = $openedState.Stream.Read(
                $currentBytes, $currentOffset, $currentBytes.Length - $currentOffset)
            if ($currentRead -le 0) { throw 'Legacy pilot state changed length during migration.' }
            $currentOffset += $currentRead
        }
        if ((Get-Sha256Hex -Bytes $currentBytes) -cne $originalHash) {
            throw 'Legacy pilot state content changed during migration; no ACL was changed.'
        }
        Assert-NoReparsePointInStatePathChain -Path $StatePath
        Assert-LegacyPilotLiveState -Validated $openedState.Validated
        if ([SonAero.PilotStateNativeMethods]::GetLinkCount($openedState.Stream.SafeFileHandle) -ne 1) {
            throw 'Legacy pilot state link count changed during migration; no ACL was changed.'
        }
        # Protect the already-open file object first. FileShare.None keeps it from being replaced
        # while the containing directory is subsequently protected.
        $openedState.Stream.SetAccessControl((New-ProtectedFileSystemSecurity))
        Assert-NoReparsePointInStatePathChain -Path $StatePath
        Set-Acl -LiteralPath (Split-Path -Parent $StatePath) `
            -AclObject (New-ProtectedFileSystemSecurity -Directory)
        Assert-NoReparsePointInStatePathChain -Path $StatePath
        Assert-ProtectedStatePath -Path (Split-Path -Parent $StatePath) -Directory
    }
    finally {
        if ($openedState -and $openedState.Stream) { $openedState.Stream.Dispose() }
    }
    Assert-NoReparsePointInStatePathChain -Path $StatePath
    Assert-PilotStateProtection -Path $StatePath
    if ((Get-FileHash -LiteralPath $StatePath -Algorithm SHA256).Hash -cne $originalHash) {
        throw 'Legacy pilot state content changed while its protection was migrated.'
    }
    $protectedState = Read-State -MissingMessage "Migrated state disappeared from '$StatePath'." `
        -InvalidJsonLabel 'Migrated state'
    $validatedProtectedState = Assert-StrictLegacyPilotState -State $protectedState
    Assert-LegacyPilotLiveState -Validated $validatedProtectedState
    Write-Output 'HTTPS_PILOT_STATE_PROTECTION_MIGRATED'
    exit 0
}

if ($Rollback) {
    $state = Read-State -MissingMessage "Rollback state was not found at '$StatePath'." `
        -InvalidJsonLabel 'Rollback state'
    Assert-StateProperties -State $state -Names @(
        'Version', 'ComputerName', 'Status', 'CertificateThumbprint', 'PilotRemoteAddress',
        'PriorTargetBindings', 'FirewallRuleAdded'
    )
    if ($state.ComputerName -ine $expectedComputerName -or $state.Version -ne 1) { throw 'Rollback state does not match this transaction.' }
    $terminalStatuses = @('RolledBack', 'AutomaticallyRolledBack', 'RecoveredRolledBack')
    $recoverableStatuses = @('Applied', 'Prepared', 'ApplyFailedRollbackPending', 'ManualRollbackPending', 'RollbackFailed')
    if ([string]$state.Status -notin @($terminalStatuses + $recoverableStatuses)) {
        throw "Rollback state has unknown status '$($state.Status)'; no changes were made."
    }
    $stateThumbprint = (Convert-HashToHex ([string]$state.CertificateThumbprint))
    if ($stateThumbprint -notmatch '^[A-F0-9]{40}$') { throw 'Rollback state certificate thumbprint is invalid.' }
    $stateRemoteAddresses = Convert-ToPilotAddress @($state.PilotRemoteAddress)
    if ($stateRemoteAddresses.Count -eq 0) { throw 'Rollback state contains no pilot remote addresses.' }
    Assert-PriorTargetBindings -Bindings @($state.PriorTargetBindings) -Thumbprint $stateThumbprint
    $currentIis = @(Get-IisBindingSnapshot)
    $currentTargetBindings = @(Get-TargetBindingSnapshot $currentIis)
    if ([string]$state.Status -in $terminalStatuses) {
        $currentTarget = Get-ComparableTargetBindings -Bindings $currentTargetBindings
        $priorTarget = Get-ComparableTargetBindings -Bindings @($state.PriorTargetBindings)
        if ($currentTarget -ne $priorTarget) {
            throw "Rollback state is '$($state.Status)', but current pilot HTTPS bindings do not match the recorded pre-transaction state."
        }
        if ($state.FirewallRuleAdded -and (Get-FirewallSnapshot).Existed) {
            throw "Rollback state is '$($state.Status)', but the transaction-added firewall rule still exists."
        }
        Assert-RequiredHttpBindings $currentIis
        Wait-Health -Scheme http -Ports @($applications.HttpPort)
        Write-Output 'HTTPS_PILOT_ALREADY_ROLLED_BACK_AND_HTTP_HEALTHY'
        exit 0
    }
    $rollbackWasApplied = [string]$state.Status -eq 'Applied' -or
        ($state.PSObject.Properties.Name -contains 'RollbackStartedFromStatus' -and
            [string]$state.RollbackStartedFromStatus -eq 'Applied')
    if ([string]$state.Status -eq 'Applied') {
        Assert-StateProperties -State $state -Names @('AppliedTargetBindings')
        Assert-PriorTargetBindings -Bindings @($state.AppliedTargetBindings) -Thumbprint $stateThumbprint
        if (@($state.AppliedTargetBindings).Count -ne $applications.Count) {
            throw 'Applied rollback state does not contain exactly one HTTPS binding for every pilot IIS site.'
        }
        $currentTarget = Get-ComparableTargetBindings -Bindings $currentTargetBindings
        $appliedTarget = Get-ComparableTargetBindings -Bindings @($state.AppliedTargetBindings)
        if ($currentTarget -ne $appliedTarget) { throw 'Current pilot HTTPS bindings have drifted since apply; rollback refused.' }
    }
    else {
        # A failed first-time apply may contain zero through four transaction-owned HTTPS bindings.
        # This assertion refuses recovery if any target port is owned by another binding/certificate.
        Assert-TargetBindingsAvailable -Snapshot $currentIis -Thumbprint $stateThumbprint
    }
    $currentFirewall = $null
    if ($state.FirewallRuleAdded) {
        $currentFirewall = Get-FirewallSnapshot
        if ($currentFirewall.Existed) {
            Assert-FirewallAvailable -Snapshot $currentFirewall -RemoteAddress $stateRemoteAddresses
        }
        elseif ([string]$state.Status -eq 'Applied') {
            throw 'The pilot firewall rule was removed after apply; rollback refused due to drift.'
        }
    }
    if ($PSCmdlet.ShouldProcess($expectedComputerName, 'Restore the prior pilot HTTPS bindings and firewall state')) {
        $rollbackStartStatus = if ($state.PSObject.Properties.Name -contains 'RollbackStartedFromStatus' -and
            -not [string]::IsNullOrWhiteSpace([string]$state.RollbackStartedFromStatus)) {
            [string]$state.RollbackStartedFromStatus
        }
        else { [string]$state.Status }
        Set-StateProperty -State $state -Name 'RollbackStartedFromStatus' -Value $rollbackStartStatus
        Set-StateProperty -State $state -Name 'RollbackStartedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
        Set-StateProperty -State $state -Name 'RollbackFailure' -Value $null
        $state.Status = 'ManualRollbackPending'
        Write-State $state
        try {
            Set-TargetBindingsFromSnapshot -TargetBindings @($state.PriorTargetBindings)
            if ($state.FirewallRuleAdded -and $currentFirewall.Existed) {
                Remove-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction Stop
            }
            $restoredIis = @(Get-IisBindingSnapshot)
            Assert-RequiredHttpBindings $restoredIis
            $restoredTarget = Get-ComparableTargetBindings -Bindings @(Get-TargetBindingSnapshot $restoredIis)
            $expectedTarget = Get-ComparableTargetBindings -Bindings @($state.PriorTargetBindings)
            if ($restoredTarget -ne $expectedTarget) {
                throw 'Restored pilot HTTPS bindings do not exactly match the recorded pre-transaction state.'
            }
            Wait-Health -Scheme http -Ports @($applications.HttpPort)
            $state.Status = if ($rollbackWasApplied) { 'RolledBack' } else { 'RecoveredRolledBack' }
            Set-StateProperty -State $state -Name 'RolledBackAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
            Write-State $state
            if ($rollbackWasApplied) { Write-Output 'HTTPS_PILOT_ROLLED_BACK_AND_HTTP_HEALTHY' }
            else { Write-Output 'HTTPS_PILOT_RECOVERED_ROLLED_BACK_AND_HTTP_HEALTHY' }
        }
        catch {
            $manualRollbackFailure = $_.Exception.Message
            $state.Status = 'RollbackFailed'
            Set-StateProperty -State $state -Name 'RollbackFailure' -Value $manualRollbackFailure
            Set-StateProperty -State $state -Name 'RollbackFailedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
            $stateWriteFailure = Try-WriteState $state
            $stateSuffix = if ($stateWriteFailure) { " The rollback failure also could not be persisted: $stateWriteFailure" } else { '' }
            throw "HTTPS pilot rollback failed after entering resumable rollback state: $manualRollbackFailure$stateSuffix Rerun -Rollback -WhatIf before any apply attempt."
        }
    }
    elseif ($WhatIfPreference) { Write-Output 'WHATIF_READY_RECOVERY: rollback state, ownership, and drift checks passed; nothing was changed.' }
    else { Write-Output 'HTTPS_PILOT_ROLLBACK_CANCELLED' }
    exit 0
}

if (Test-ProtectedStateExists) {
    $oldState = Read-State -MissingMessage "Existing transaction state was not found at '$StatePath'." `
        -InvalidJsonLabel 'Existing transaction state'
    Assert-StateProperties -State $oldState -Names @(
        'Version', 'ComputerName', 'Status', 'CertificateThumbprint', 'PilotRemoteAddress',
        'PriorTargetBindings', 'FirewallRuleAdded'
    )
    if ($oldState.ComputerName -ine $expectedComputerName -or $oldState.Version -ne 1) {
        throw "Existing transaction state at '$StatePath' does not belong to this host/script version."
    }
    $completedStatuses = @('RolledBack', 'AutomaticallyRolledBack', 'RecoveredRolledBack')
    if ([string]$oldState.Status -notin $completedStatuses) {
        throw "An incomplete HTTPS pilot transaction with status '$($oldState.Status)' exists at '$StatePath'. Run this script with -Rollback -WhatIf, then -Rollback -Confirm:`$false, before retrying apply."
    }
    $oldThumbprint = Convert-HashToHex ([string]$oldState.CertificateThumbprint)
    if ($oldThumbprint -notmatch '^[A-F0-9]{40}$') { throw 'Completed transaction state has an invalid certificate thumbprint.' }
    $oldRemoteAddresses = Convert-ToPilotAddress @($oldState.PilotRemoteAddress)
    if ($oldRemoteAddresses.Count -eq 0) { throw 'Completed transaction state contains no pilot remote addresses.' }
    Assert-PriorTargetBindings -Bindings @($oldState.PriorTargetBindings) -Thumbprint $oldThumbprint
    $completedIis = @(Get-IisBindingSnapshot)
    Assert-RequiredHttpBindings $completedIis
    $completedCurrentTarget = Get-ComparableTargetBindings -Bindings @(Get-TargetBindingSnapshot $completedIis)
    $completedPriorTarget = Get-ComparableTargetBindings -Bindings @($oldState.PriorTargetBindings)
    if ($completedCurrentTarget -ne $completedPriorTarget) {
        throw "Completed transaction state is '$($oldState.Status)', but current pilot HTTPS bindings have drifted from its recorded baseline. The state file was not overwritten."
    }
    if ($oldState.FirewallRuleAdded -and (Get-FirewallSnapshot).Existed) {
        throw "Completed transaction state is '$($oldState.Status)', but its transaction-added firewall rule still exists. The state file was not overwritten."
    }
    Wait-Health -Scheme http -Ports @($applications.HttpPort)
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
    AppliedAtUtc = $null
    RolledBackAtUtc = $null
    ApplyFailure = $null
    ApplyFailedAtUtc = $null
    RollbackFailure = $null
    RollbackFailedAtUtc = $null
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
    Set-StateProperty -State $state -Name 'AppliedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
    $state.AppliedTargetBindings = @(Get-TargetBindingSnapshot $iisAfter)
    Write-State $state
    Write-Output 'HTTPS_PILOT_CONFIGURED_AND_DUAL_SCHEME_HEALTHY'
}
catch {
    $failure = $_.Exception.Message
    Invoke-AutomaticRollback -State $state -OriginalFailure $failure
    throw "HTTPS pilot transaction failed and was automatically rolled back. Original apply failure: $failure"
}
}
finally {
    try { $transactionMutex.ReleaseMutex() }
    finally { $transactionMutex.Dispose() }
}
