<#
    Adds only the Quality Assurance HTTPS pilot binding to an authentic protected
    four-site SON-IIS2 pilot transaction. The historical state remains authoritative.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'Apply')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Rollback')]
    [switch]$Rollback,
    [ValidateRange(7, 365)]
    [int]$MinimumRemainingDays = 30,
    [ValidateRange(30, 600)]
    [int]$HealthTimeoutSeconds = 180,
    [string]$HistoricalStatePath = 'C:\ProgramData\SonAero\deployment-state\https-pilot.json',
    [string]$StatePath = 'C:\ProgramData\SonAero\deployment-state\https-pilot-quality-extension.json'
)

$ErrorActionPreference = 'Stop'
$expectedComputerName = 'SON-IIS2'
$stateRoot = 'C:\ProgramData\SonAero\deployment-state'
$exactHistoricalStatePath = Join-Path $stateRoot 'https-pilot.json'
$exactStatePath = Join-Path $stateRoot 'https-pilot-quality-extension.json'
$firewallRuleName = 'SON-AERO Hub HTTPS pilot'
$mutexName = 'Global\SonAero-HubHttpsBindingTransactions'
$storeName = 'My'
$applications = @(
    [pscustomobject]@{ Site = 'ProjectTracker'; HttpPort = 5135; HttpsPort = 6135 },
    [pscustomobject]@{ Site = 'SonAeroPortal'; HttpPort = 5140; HttpsPort = 6140 },
    [pscustomobject]@{ Site = 'EngineeringHub'; HttpPort = 5150; HttpsPort = 6150 },
    [pscustomobject]@{ Site = 'EstimatingDashboard'; HttpPort = 5160; HttpsPort = 6160 },
    [pscustomobject]@{ Site = 'QualityAssurance'; HttpPort = 5170; HttpsPort = 6170 }
)
$historicalApplications = @($applications | Select-Object -First 4)
$qualityApplication = $applications[4]

function Convert-ToFullPath([string]$Path) {
    try { return [IO.Path]::GetFullPath($Path) }
    catch { throw "Invalid local state path '$Path': $($_.Exception.Message)" }
}
function Assert-ExactStatePaths {
    if (-not [IO.Path]::IsPathRooted($HistoricalStatePath) -or -not [IO.Path]::IsPathRooted($StatePath)) {
        throw 'HistoricalStatePath and StatePath must be absolute local paths.'
    }
    $script:HistoricalStatePath = Convert-ToFullPath $HistoricalStatePath
    $script:StatePath = Convert-ToFullPath $StatePath
    if ($HistoricalStatePath -ine (Convert-ToFullPath $exactHistoricalStatePath)) {
        throw "HistoricalStatePath must be the exact deployed path '$exactHistoricalStatePath'."
    }
    if ($StatePath -ine (Convert-ToFullPath $exactStatePath)) {
        throw "StatePath must be the exact quality-extension path '$exactStatePath'."
    }
}
function Assert-NoReparsePathChain([string]$Path) {
    $full = Convert-ToFullPath $Path
    $root = [IO.Path]::GetPathRoot($full)
    $relative = $full.Substring($root.Length)
    $current = $root.TrimEnd('\')
    foreach ($part in @($relative -split '\\' | Where-Object { $_ })) {
        $current = if ($current) { Join-Path $current $part } else { Join-Path $root $part }
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "State path contains reparse point '$current'."
            }
        }
    }
}
function New-ProtectedFileSystemSecurity([switch]$Directory) {
    $acl = New-Object Security.AccessControl.DirectorySecurity
    if (-not $Directory) { $acl = New-Object Security.AccessControl.FileSecurity }
    $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($administrators)
    $inheritance = if ($Directory) {
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    } else { [Security.AccessControl.InheritanceFlags]::None }
    foreach ($sid in @($system, $administrators)) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $sid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance,
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    return $acl
}
function Assert-ProtectedPath([string]$Path, [switch]$Directory) {
    Assert-NoReparsePathChain $Path
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -ne [bool]$Directory) { throw "Unexpected state path type '$Path'." }
    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) { throw "State ACL inheritance is enabled at '$Path'." }
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @('S-1-5-18', 'S-1-5-32-544')) { throw "State owner is not trusted at '$Path'." }
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $expectedSids = @('S-1-5-18', 'S-1-5-32-544')
    if ($rules.Count -ne 2) { throw "State ACL at '$Path' must contain exactly two explicit rules." }
    $actualSids = @($rules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object)
    if (($actualSids -join '|') -cne (($expectedSids | Sort-Object) -join '|')) {
        throw "State ACL at '$Path' must contain exactly one SYSTEM and one Administrators rule."
    }
    foreach ($rule in $rules) {
        if ($rule.IdentityReference.Value -notin $expectedSids -or
            $rule.IsInherited -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne
                [Security.AccessControl.FileSystemRights]::FullControl) {
            throw "State ACL at '$Path' grants an unexpected identity or permission."
        }
    }
}
function Assert-ProtectedStateFile([string]$Path) {
    Assert-ProtectedPath -Path (Split-Path -Parent $Path) -Directory
    Assert-ProtectedPath -Path $Path
}
function Read-ProtectedJson([string]$Path, [string]$Label) {
    Assert-NoReparsePathChain $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label was not found at '$Path'." }
    Assert-ProtectedStateFile $Path
    Assert-NoReparsePathChain $Path
    Assert-ProtectedStateFile $Path
    try { return Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "$Label is not valid JSON: $($_.Exception.Message)" }
}
function Write-ExtensionState($State) {
    Assert-NoReparsePathChain $StatePath
    $directory = Split-Path -Parent $StatePath
    Assert-ProtectedPath -Path $directory -Directory
    if (Test-Path -LiteralPath $StatePath) { Assert-ProtectedStateFile $StatePath }
    $temporary = Join-Path $directory ((Split-Path -Leaf $StatePath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $json = $State | ConvertTo-Json -Depth 12
        $encoding = New-Object Text.UTF8Encoding($false)
        $bytes = $encoding.GetBytes($json)
        $stream = New-Object IO.FileStream(
            $temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        Set-Acl -LiteralPath $temporary -AclObject (New-ProtectedFileSystemSecurity)
        Assert-ProtectedPath $temporary
        if (Test-Path -LiteralPath $StatePath) { [IO.File]::Replace($temporary, $StatePath, $null) }
        else { Move-Item -LiteralPath $temporary -Destination $StatePath }
        Assert-ProtectedStateFile $StatePath
    }
    finally {
        $item = Get-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        if ($item -and -not $item.PSIsContainer -and
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
}
function Assert-HostAndAdministrator {
    if ($env:COMPUTERNAME -ine $expectedComputerName) { throw "This transaction is restricted to $expectedComputerName." }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated Windows PowerShell session.'
    }
}
function Import-Iis {
    $prior = $WhatIfPreference
    try { $WhatIfPreference = $false; Import-Module WebAdministration -Global -ErrorAction Stop }
    finally { $WhatIfPreference = $prior }
    $assembly = Join-Path $env:WINDIR 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw 'IIS administration assembly was not found.' }
    Add-Type -Path $assembly -ErrorAction Stop
}
function Enter-TransactionLock {
    $mutex = New-Object Threading.Mutex($false, $mutexName)
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'Another SON-AERO HTTPS binding transaction is already running.' }
        return $mutex
    }
    catch { if (-not $acquired) { $mutex.Dispose() }; throw }
}
function Convert-HashToHex($Value) {
    if ($null -eq $Value) { return '' }
    if ($Value -is [byte[]]) { return ([BitConverter]::ToString($Value)).Replace('-', '') }
    return ([string]$Value).Replace(' ', '').Replace('-', '').ToUpperInvariant()
}
function Convert-HexToBytes([string]$Value) {
    $hex = Convert-HashToHex $Value
    if ($hex -notmatch '^[A-F0-9]{40}$') { throw 'Certificate thumbprint must contain 40 hexadecimal characters.' }
    [byte[]]$bytes = @(for ($i = 0; $i -lt $hex.Length; $i += 2) { [Convert]::ToByte($hex.Substring($i, 2), 16) })
    return $bytes
}
function Convert-Binding($Binding, [string]$Site) {
    return [pscustomobject]@{
        Site = $Site; Protocol = [string]$Binding.Protocol
        BindingInformation = [string]$Binding.BindingInformation
        CertificateHash = Convert-HashToHex $Binding.CertificateHash
        CertificateStoreName = [string]$Binding.CertificateStoreName; SslFlags = [int]$Binding.SslFlags
    }
}
function Get-IisSnapshot {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $result = @()
        foreach ($site in $manager.Sites) { foreach ($binding in $site.Bindings) { $result += Convert-Binding $binding $site.Name } }
        return @($result)
    }
    finally { $manager.Dispose() }
}
function Get-ComparableBindings([object[]]$Bindings) {
    return (@($Bindings | ForEach-Object {
        '{0}|{1}|{2}|{3}|{4}|{5}' -f $_.Site, $_.Protocol, $_.BindingInformation,
            (Convert-HashToHex $_.CertificateHash), $_.CertificateStoreName, ([int]$_.SslFlags)
    } | Sort-Object) -join "`n")
}
function Get-TargetBindings([object[]]$Snapshot) {
    $ports = @($applications.HttpsPort)
    return @($Snapshot | Where-Object {
        $parts = $_.BindingInformation -split ':', 3
        $parts.Count -ge 2 -and [int]$parts[1] -in $ports
    })
}
function Get-UnrelatedBindings([object[]]$Snapshot) {
    return @($Snapshot | Where-Object {
        -not ($_.Site -eq $qualityApplication.Site -and $_.Protocol -eq 'https' -and
            $_.BindingInformation -eq "*:$($qualityApplication.HttpsPort):")
    })
}
function New-ExpectedBinding($Application, [string]$Thumbprint) {
    return [pscustomobject]@{
        Site = $Application.Site; Protocol = 'https'; BindingInformation = "*:$($Application.HttpsPort):"
        CertificateHash = $Thumbprint; CertificateStoreName = $storeName; SslFlags = 0
    }
}
function Assert-HttpBindings([object[]]$Snapshot) {
    foreach ($application in $applications) {
        $matches = @($Snapshot | Where-Object {
            $_.Site -eq $application.Site -and $_.Protocol -eq 'http' -and
            $_.BindingInformation -eq "*:$($application.HttpPort):"
        })
        if ($matches.Count -ne 1) { throw "Required HTTP binding for '$($application.Site)' is missing or ambiguous." }
    }
}
function Assert-HistoricalBindings([object[]]$Snapshot, [string]$Thumbprint, [switch]$AllowQuality) {
    $target = @(Get-TargetBindings $Snapshot)
    $expected = @($historicalApplications | ForEach-Object { New-ExpectedBinding $_ $Thumbprint })
    if ($AllowQuality) { $expected += New-ExpectedBinding $qualityApplication $Thumbprint }
    if ((Get-ComparableBindings $target) -cne (Get-ComparableBindings $expected)) {
        throw 'Current 61xx bindings do not equal the exact permitted pilot generation.'
    }
}
function Assert-QaPortFree {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $qualityApplication.HttpsPort -ErrorAction SilentlyContinue)
    if ($listeners.Count -gt 0) { throw 'QA HTTPS port 6170 has a listener while its IIS binding is absent.' }
}
function Get-FirewallSnapshot {
    $rules = @(Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue)
    if ($rules.Count -ne 1) { throw "Expected exactly one firewall rule '$firewallRuleName'." }
    $port = $rules[0] | Get-NetFirewallPortFilter
    $address = $rules[0] | Get-NetFirewallAddressFilter
    return [pscustomobject]@{
        Existed = $true; Enabled = [string]$rules[0].Enabled; Direction = [string]$rules[0].Direction
        Action = [string]$rules[0].Action; Profile = [string]$rules[0].Profile; Protocol = [string]$port.Protocol
        LocalPort = @($port.LocalPort); RemoteAddress = @($address.RemoteAddress)
    }
}
function Convert-ToRemoteAddresses([object[]]$Values) {
    $result = @()
    foreach ($raw in $Values) {
        if ($raw -isnot [string]) { throw 'Historical remote addresses must be strings.' }
        $value = $raw.Trim(); $parts = $value -split '/', 2; $ip = $null
        if (-not [Net.IPAddress]::TryParse($parts[0], [ref]$ip) -or
            $value -match '^(Any|LocalSubnet|Internet|Intranet)$') { throw "Unsafe historical remote address '$value'." }
        if ($parts.Count -eq 2) {
            $prefix = 0
            if (-not [int]::TryParse($parts[1], [ref]$prefix) -or
                ($ip.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and ($prefix -lt 24 -or $prefix -gt 32)) -or
                ($ip.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6 -and ($prefix -lt 64 -or $prefix -gt 128))) {
                throw "Unsafe historical CIDR '$value'."
            }
        }
        $result += $value
    }
    return @($result | Sort-Object -Unique)
}
function Assert-Firewall($Snapshot, [string[]]$RemoteAddress, [int[]]$Ports) {
    $actualPorts = @($Snapshot.LocalPort | ForEach-Object { ([string]$_) -split ',' } | ForEach-Object { $_.Trim() } | Sort-Object)
    $actualRemote = @($Snapshot.RemoteAddress | ForEach-Object { ([string]$_) -split ',' } | ForEach-Object { $_.Trim() } | Sort-Object)
    $profile = ([string]$Snapshot.Profile).Replace(' ', '')
    if (-not $Snapshot.Existed -or $Snapshot.Enabled -ne 'True' -or $Snapshot.Direction -ne 'Inbound' -or
        $Snapshot.Action -ne 'Allow' -or $profile -notin @('Domain,Private', 'Private,Domain') -or
        $Snapshot.Protocol -ne 'TCP' -or ($actualPorts -join ',') -ne (@($Ports | Sort-Object) -join ',') -or
        ($actualRemote -join ',') -ne (@($RemoteAddress | Sort-Object) -join ',')) {
        throw 'Pilot firewall rule does not exactly match the required ports and remote addresses.'
    }
}
function Set-FirewallPorts(
    [int[]]$Ports,
    [string[]]$RemoteAddress,
    [int[]]$ExpectedCurrentPorts,
    [int[]]$AlternateCurrentPorts
) {
    $fresh = Get-FirewallSnapshot
    $allowed = $true
    try { Assert-Firewall $fresh $RemoteAddress @($ExpectedCurrentPorts) } catch { $allowed = $false }
    if (-not $allowed -and $PSBoundParameters.ContainsKey('AlternateCurrentPorts')) {
        $allowed = $true
        try { Assert-Firewall $fresh $RemoteAddress @($AlternateCurrentPorts) } catch { $allowed = $false }
    }
    if (-not $allowed) { throw 'Pilot firewall drifted immediately before its port-filter mutation.' }
    $rule = Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction Stop
    $filter = $rule | Get-NetFirewallPortFilter
    $null = $filter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort $Ports -ErrorAction Stop
}
function Assert-Certificate([string]$Thumbprint, [string]$RootThumbprint) {
    $leaf = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object { (Convert-HashToHex $_.Thumbprint) -eq $Thumbprint })
    $root = @(Get-ChildItem Cert:\LocalMachine\Root | Where-Object { (Convert-HashToHex $_.Thumbprint) -eq $RootThumbprint })
    if ($leaf.Count -ne 1 -or $root.Count -ne 1) { throw 'Historical leaf or root certificate was not found uniquely.' }
    $certificate = $leaf[0]; $rootCertificate = $root[0]; $now = Get-Date
    if (-not $certificate.HasPrivateKey -or $certificate.NotBefore -gt $now -or
        $certificate.NotAfter -lt $now.AddDays($MinimumRemainingDays)) { throw 'Historical leaf certificate is unusable or expires too soon.' }
    $eku = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($eku.Count -ne 1 -or $eku[0].Format($false) -notmatch '1\.3\.6\.1\.5\.5\.7\.3\.1|Server Authentication') {
        throw 'Historical leaf certificate lacks Server Authentication EKU.'
    }
    $dns = @($certificate.DnsNameList | ForEach-Object { if ($_.Punycode) { $_.Punycode } else { $_.Unicode } })
    foreach ($name in @('SON-IIS2', 'SON-IIS2.SON4L.LOCAL')) {
        if (-not @($dns | Where-Object { $_ -ieq $name })) { throw "Historical certificate SAN lacks '$name'." }
    }
    $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
    try {
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $chain.ChainPolicy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        if (-not $chain.Build($certificate) -or @($chain.ChainStatus | Where-Object { $_.Status -ne 0 }).Count -gt 0) {
            throw 'Historical pilot certificate chain validation failed.'
        }
        $elements = @($chain.ChainElements)
        if ($elements.Count -ne 2 -or (Convert-HashToHex $elements[1].Certificate.Thumbprint) -ne $RootThumbprint -or
            -not $rootCertificate.Subject.Equals($rootCertificate.Issuer)) { throw 'Historical certificate does not terminate at the recorded pilot root.' }
    }
    finally { $chain.Dispose() }
}
function Assert-BindingShape([object[]]$Bindings) {
    foreach ($binding in $Bindings) {
        $names = @('Site','Protocol','BindingInformation','CertificateHash','CertificateStoreName','SslFlags')
        Assert-ExactProperties $binding $names 'Binding snapshot'
        if ($binding.Site -isnot [string] -or $binding.Protocol -isnot [string] -or
            $binding.BindingInformation -isnot [string] -or $binding.CertificateHash -isnot [string] -or
            $binding.CertificateStoreName -isnot [string] -or $binding.SslFlags -isnot [int]) {
            throw 'Binding snapshot contains an invalid property type.'
        }
    }
}
function Assert-ExactProperties($Value, [string[]]$Names, [string]$Label) {
    if ($Value -isnot [pscustomobject]) { throw "$Label must be one JSON object." }
    $actual = @($Value.PSObject.Properties.Name | Sort-Object -CaseSensitive)
    $expected = @($Names | Sort-Object -CaseSensitive)
    if ($actual.Count -ne $expected.Count -or ($actual -join "`n") -cne ($expected -join "`n")) {
        throw "$Label has an unexpected or missing property."
    }
}
function Assert-HistoricalState($State) {
    $historyNames = @('Version','ComputerName','Status','PreparedAtUtc','CertificateThumbprint',
        'PilotRootThumbprint','PilotRemoteAddress','AllBindingsBefore','PriorTargetBindings','FirewallBefore',
        'FirewallRuleAdded','AppliedTargetBindings','AppliedAtUtc','RolledBackAtUtc','ApplyFailure',
        'ApplyFailedAtUtc','RollbackFailure','RollbackFailedAtUtc')
    Assert-ExactProperties $State $historyNames 'Historical pilot state'
    if ($State.Version -isnot [int] -or $State.Version -ne 1 -or $State.ComputerName -isnot [string] -or
        $State.ComputerName -cne $expectedComputerName -or $State.Status -isnot [string] -or
        $State.Status -cne 'Applied' -or $State.FirewallRuleAdded -isnot [bool]) {
        throw 'Historical state must be exact v1 SON-IIS2 Applied state.'
    }
    $thumbprint = [string]$State.CertificateThumbprint; $root = [string]$State.PilotRootThumbprint
    if ($thumbprint -cnotmatch '^[A-F0-9]{40}$' -or $root -cnotmatch '^[A-F0-9]{40}$' -or $thumbprint -ceq $root) {
        throw 'Historical certificate thumbprints are invalid.'
    }
    $remote = @(Convert-ToRemoteAddresses @($State.PilotRemoteAddress))
    if ($remote.Count -eq 0 -or ($remote -join "`n") -cne (@($State.PilotRemoteAddress) -join "`n")) {
        throw 'Historical remote addresses are not exact sorted unique constrained values.'
    }
    if ($State.PilotRemoteAddress -isnot [object[]] -or $State.AllBindingsBefore -isnot [object[]] -or
        $State.PriorTargetBindings -isnot [object[]] -or $State.AppliedTargetBindings -isnot [object[]] -or
        @($State.PriorTargetBindings).Count -ne 0 -or @($State.AppliedTargetBindings).Count -ne 4) {
        throw 'Historical state must record an empty prior baseline and exactly four applied bindings.'
    }
    Assert-BindingShape @($State.AllBindingsBefore); Assert-BindingShape @($State.AppliedTargetBindings)
    $expectedApplied = @($historicalApplications | ForEach-Object { New-ExpectedBinding $_ $thumbprint })
    if ((Get-ComparableBindings @($State.AppliedTargetBindings)) -cne (Get-ComparableBindings $expectedApplied)) {
        throw 'Historical state does not record the authentic four-site binding set.'
    }
    $before = @($State.AllBindingsBefore)
    foreach ($app in $historicalApplications) {
        if (@($before | Where-Object { $_.Site -eq $app.Site -and $_.Protocol -eq 'http' -and
            $_.BindingInformation -eq "*:$($app.HttpPort):" }).Count -ne 1) { throw 'Historical HTTP baseline is incomplete.' }
    }
    if (@(Get-TargetBindings $before).Count -ne 0 -or @($before | Where-Object {
        $_.Site -eq 'QualityAssurance' -or $_.BindingInformation -eq '*:5170:' -or $_.BindingInformation -eq '*:6170:'
    }).Count -ne 0 -or
        $State.FirewallRuleAdded -isnot [bool] -or -not $State.FirewallRuleAdded -or
        $State.FirewallBefore.Existed -isnot [bool] -or $State.FirewallBefore.Existed) {
        throw 'Historical state is not the authentic four-site first-time pilot transaction.'
    }
    Assert-ExactProperties $State.FirewallBefore @('Existed') 'Historical FirewallBefore'
    foreach ($name in @('RolledBackAtUtc','ApplyFailure','ApplyFailedAtUtc','RollbackFailure','RollbackFailedAtUtc')) {
        if ($null -ne $State.$name) { throw "Historical Applied state property '$name' must be null." }
    }
    $prepared = [DateTimeOffset]::MinValue; $appliedAt = [DateTimeOffset]::MinValue
    if ($State.PreparedAtUtc -isnot [string] -or $State.AppliedAtUtc -isnot [string] -or
        -not [DateTimeOffset]::TryParseExact($State.PreparedAtUtc, 'o', [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind, [ref]$prepared) -or
        -not [DateTimeOffset]::TryParseExact($State.AppliedAtUtc, 'o', [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind, [ref]$appliedAt) -or
        $prepared.Offset -ne [TimeSpan]::Zero -or $appliedAt.Offset -ne [TimeSpan]::Zero -or
        $prepared -gt $appliedAt -or $appliedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw 'Historical timestamps are invalid.'
    }
    return [pscustomobject]@{ Thumbprint = $thumbprint; RootThumbprint = $root; RemoteAddress = $remote }
}
function Assert-ExtensionState($State, $History) {
    $extensionNames = @('Version','Transaction','ComputerName','Status','HistoricalStateSha256',
        'CertificateThumbprint','PilotRootThumbprint','PilotRemoteAddress','UnrelatedBindingsBefore',
        'FirewallBefore','PriorQaBinding','PlannedQaBinding','PreparedAtUtc','AppliedQaBinding','AppliedAtUtc','ApplyFailure',
        'ApplyFailedAtUtc','RollbackStartedAtUtc','RolledBackAtUtc','RollbackFailure','RollbackFailedAtUtc')
    Assert-ExactProperties $State $extensionNames 'Quality-extension state'
    if ($State.Version -ne 1 -or $State.Transaction -cne 'HttpsPilotQualityExtension' -or
        $State.ComputerName -cne $expectedComputerName -or $State.CertificateThumbprint -cne $History.Thumbprint -or
        $State.PilotRootThumbprint -cne $History.RootThumbprint -or
        $State.HistoricalStateSha256 -isnot [string] -or $State.HistoricalStateSha256 -cnotmatch '^[A-F0-9]{64}$' -or
        $State.Status -notin @('Prepared','Applied','ApplyFailedRollbackPending','RollbackPending','RollbackFailed',
            'RolledBack','AutomaticallyRolledBack') -or
        $State.PilotRemoteAddress -isnot [object[]] -or $State.UnrelatedBindingsBefore -isnot [object[]] -or
        $State.PriorQaBinding -isnot [object[]] -or @($State.PriorQaBinding).Count -ne 0 -or
        $State.PlannedQaBinding -isnot [object[]] -or
        (@($State.PilotRemoteAddress) -join "`n") -cne (@($History.RemoteAddress) -join "`n")) {
        throw 'Extension state does not match the protected historical authority.'
    }
    Assert-BindingShape @($State.UnrelatedBindingsBefore); Assert-BindingShape @($State.PlannedQaBinding)
    Assert-Firewall $State.FirewallBefore $History.RemoteAddress @($historicalApplications.HttpsPort)
    $expectedQa = New-ExpectedBinding $qualityApplication $History.Thumbprint
    if (@($State.PlannedQaBinding).Count -ne 1 -or
        (Get-ComparableBindings @($State.PlannedQaBinding)) -cne (Get-ComparableBindings @($expectedQa))) {
        throw 'Extension state planned QA binding is invalid.'
    }
    $prepared = [DateTimeOffset]::MinValue
    if ($State.PreparedAtUtc -isnot [string] -or -not [DateTimeOffset]::TryParseExact(
        $State.PreparedAtUtc, 'o', [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind, [ref]$prepared) -or $prepared.Offset -ne [TimeSpan]::Zero) {
        throw 'Extension PreparedAtUtc is invalid.'
    }
    if ($State.Status -eq 'Applied') {
        if ($State.AppliedQaBinding -isnot [object[]] -or @($State.AppliedQaBinding).Count -ne 1 -or
            (Get-ComparableBindings @($State.AppliedQaBinding)) -cne (Get-ComparableBindings @($expectedQa)) -or
            $State.AppliedAtUtc -isnot [string]) { throw 'Applied state lacks exact applied QA ownership.' }
    }
    elseif ($null -ne $State.AppliedQaBinding -and @($State.AppliedQaBinding).Count -gt 0 -and
        (Get-ComparableBindings @($State.AppliedQaBinding)) -cne (Get-ComparableBindings @($expectedQa))) {
        throw 'Extension AppliedQaBinding is outside exact QA ownership.'
    }
}
function Set-StateValue($State, [string]$Name, $Value) {
    if ($State.PSObject.Properties.Name -contains $Name) { $State.$Name = $Value }
    else { $State | Add-Member -MemberType NoteProperty -Name $Name -Value $Value }
}
function Add-QaBinding([string]$Thumbprint) {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$qualityApplication.Site]
        if (-not $site) { throw "IIS site '$($qualityApplication.Site)' does not exist." }
        $allOnPort = @($manager.Sites | ForEach-Object { $_.Bindings } | Where-Object {
            $parts = $_.BindingInformation -split ':', 3
            $parts.Count -ge 2 -and [int]$parts[1] -eq $qualityApplication.HttpsPort
        })
        if ($allOnPort.Count -ne 0) {
            throw 'QA 6170 became occupied before apply.'
        }
        $binding = $site.Bindings.Add("*:$($qualityApplication.HttpsPort):", 'https')
        $binding.CertificateHash = Convert-HexToBytes $Thumbprint
        $binding.CertificateStoreName = $storeName
        $binding.SslFlags = [Microsoft.Web.Administration.SslFlags]::None
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}
function Remove-QaBinding([object[]]$PlannedQaBinding) {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $matches = @()
        foreach ($site in $manager.Sites) {
            foreach ($binding in $site.Bindings) {
                $parts = $binding.BindingInformation -split ':', 3
                if ($parts.Count -ge 2 -and [int]$parts[1] -eq $qualityApplication.HttpsPort) {
                    $matches += [pscustomobject]@{ SiteObject = $site; BindingObject = $binding; Snapshot = Convert-Binding $binding $site.Name }
                }
            }
        }
        if ($matches.Count -gt 1) { throw 'Multiple 6170 bindings exist; automatic removal refused.' }
        if ($matches.Count -eq 1) {
            if ((Get-ComparableBindings @($matches[0].Snapshot)) -cne (Get-ComparableBindings $PlannedQaBinding)) {
                throw 'The 6170 binding is not the exact transaction-owned planned QA binding.'
            }
            $matches[0].SiteObject.Bindings.Remove($matches[0].BindingObject)
            $manager.CommitChanges()
        }
    }
    finally { $manager.Dispose() }
}
function Wait-Health([ValidateSet('http','https')][string]$Scheme, [int[]]$Ports) {
    $pending = @($Ports); $deadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSeconds)
    do {
        foreach ($port in @($pending)) {
            try {
                $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials `
                    -Uri "${Scheme}://$expectedComputerName`:$port/api/health" -TimeoutSec 10
                if ($response.StatusCode -eq 200) { $pending = @($pending | Where-Object { $_ -ne $port }) }
            } catch { }
        }
        if ($pending.Count) { Start-Sleep -Milliseconds 750 }
    } while ($pending.Count -and [DateTime]::UtcNow -lt $deadline)
    if ($pending.Count) { throw "$Scheme health failed on ports $($pending -join ', ')." }
}
function Assert-UnrelatedUnchanged([object[]]$Snapshot, [object[]]$Expected) {
    if ((Get-ComparableBindings @(Get-UnrelatedBindings $Snapshot)) -cne (Get-ComparableBindings $Expected)) {
        throw 'An unrelated IIS binding, including a retained 443 binding, drifted.'
    }
}
function Assert-FourSiteLive($History, [object[]]$Unrelated) {
    $current = @(Get-IisSnapshot); Assert-HttpBindings $current; Assert-HistoricalBindings $current $History.Thumbprint
    if ($Unrelated) { Assert-UnrelatedUnchanged $current $Unrelated }
    Assert-QaPortFree
    Assert-Firewall (Get-FirewallSnapshot) $History.RemoteAddress @($historicalApplications.HttpsPort)
    Wait-Health http @($applications.HttpPort); Wait-Health https @($historicalApplications.HttpsPort)
}
function Assert-FiveSiteLive($History, [object[]]$Unrelated) {
    $current = @(Get-IisSnapshot); Assert-HttpBindings $current
    Assert-HistoricalBindings $current $History.Thumbprint -AllowQuality
    Assert-UnrelatedUnchanged $current $Unrelated
    Assert-Firewall (Get-FirewallSnapshot) $History.RemoteAddress @($applications.HttpsPort)
    Wait-Health http @($applications.HttpPort); Wait-Health https @($applications.HttpsPort)
}
function Assert-RecoverableState($State, $History) {
    $current = @(Get-IisSnapshot); Assert-HttpBindings $current
    Assert-UnrelatedUnchanged $current @($State.UnrelatedBindingsBefore)
    $target = Get-ComparableBindings @(Get-TargetBindings $current)
    $four = Get-ComparableBindings @($historicalApplications | ForEach-Object { New-ExpectedBinding $_ $History.Thumbprint })
    $five = Get-ComparableBindings @(@($historicalApplications | ForEach-Object {
        New-ExpectedBinding $_ $History.Thumbprint
    }) + @($State.PlannedQaBinding))
    if ($target -cne $four -and $target -cne $five) {
        throw 'Recovery permits only exact prior four-site or exact planned five-site pilot bindings.'
    }
    $firewall = Get-FirewallSnapshot
    $fourFirewall = $true; try { Assert-Firewall $firewall $History.RemoteAddress @($State.FirewallBefore.LocalPort) } catch { $fourFirewall = $false }
    $fiveFirewall = $true; try { Assert-Firewall $firewall $History.RemoteAddress @($applications.HttpsPort) } catch { $fiveFirewall = $false }
    if (-not $fourFirewall -and -not $fiveFirewall) {
        throw 'Recovery permits only exact saved four-port or exact planned five-port firewall state.'
    }
}
function Restore-FourSite($State, $History) {
    Assert-RecoverableState $State $History
    $State.Status = 'RollbackPending'; Set-StateValue $State 'RollbackStartedAtUtc' ([DateTime]::UtcNow.ToString('o'))
    Write-ExtensionState $State
    Assert-RecoverableState $State $History
    Remove-QaBinding @($State.PlannedQaBinding)
    $firewallNow = Get-FirewallSnapshot
    $fourFirewall = $true; try { Assert-Firewall $firewallNow $History.RemoteAddress @($State.FirewallBefore.LocalPort) } catch { $fourFirewall = $false }
    $fiveFirewall = $true; try { Assert-Firewall $firewallNow $History.RemoteAddress @($applications.HttpsPort) } catch { $fiveFirewall = $false }
    if (-not $fourFirewall -and -not $fiveFirewall) { throw 'Firewall drifted immediately before rollback mutation.' }
    Set-FirewallPorts -Ports @($State.FirewallBefore.LocalPort) -RemoteAddress @($History.RemoteAddress) `
        -ExpectedCurrentPorts @($State.FirewallBefore.LocalPort) -AlternateCurrentPorts @($applications.HttpsPort)
    Assert-FourSiteLive $History @($State.UnrelatedBindingsBefore)
    $State.Status = 'RolledBack'; Set-StateValue $State 'RolledBackAtUtc' ([DateTime]::UtcNow.ToString('o'))
    Set-StateValue $State 'RollbackFailure' $null; Write-ExtensionState $State
}

Assert-ExactStatePaths
Assert-HostAndAdministrator
Import-Iis
$transactionMutex = Enter-TransactionLock
try {
    $historicalState = Read-ProtectedJson $HistoricalStatePath 'Historical pilot state'
    $history = Assert-HistoricalState $historicalState
    Assert-Certificate $history.Thumbprint $history.RootThumbprint
    $historicalHash = (Get-FileHash -LiteralPath $HistoricalStatePath -Algorithm SHA256).Hash

    if ($Rollback) {
        $state = Read-ProtectedJson $StatePath 'Quality-extension state'
        Assert-ExtensionState $state $history
        if ($state.HistoricalStateSha256 -cne $historicalHash) { throw 'Protected historical state content drifted.' }
        $terminal = @('RolledBack','AutomaticallyRolledBack')
        if ($state.Status -in $terminal) {
            Assert-FourSiteLive $history @($state.UnrelatedBindingsBefore)
            Write-Output 'HTTPS_PILOT_QA_EXTENSION_ALREADY_ROLLED_BACK_AND_FOUR_SITE_HEALTHY'; exit 0
        }
        if ($state.Status -notin @('Prepared','Applied','ApplyFailedRollbackPending','RollbackPending','RollbackFailed')) {
            throw "Unknown extension transaction status '$($state.Status)'."
        }
        Assert-RecoverableState $state $history
        if (-not $PSCmdlet.ShouldProcess($expectedComputerName, 'Remove only QA HTTPS 6170 and restore the exact four-port pilot firewall')) {
            if ($WhatIfPreference) { Write-Output 'WHATIF_READY_HTTPS_PILOT_QA_EXTENSION_ROLLBACK' }; exit 0
        }
        try { Restore-FourSite $state $history }
        catch {
            $state.Status = 'RollbackFailed'; Set-StateValue $state 'RollbackFailure' $_.Exception.Message
            Set-StateValue $state 'RollbackFailedAtUtc' ([DateTime]::UtcNow.ToString('o'))
            try { Write-ExtensionState $state } catch { }
            throw
        }
        Write-Output 'HTTPS_PILOT_QA_EXTENSION_ROLLED_BACK_AND_FOUR_SITE_HEALTHY'; exit 0
    }

    if (Test-Path -LiteralPath $StatePath) {
        $existing = Read-ProtectedJson $StatePath 'Quality-extension state'
        Assert-ExtensionState $existing $history
        if ($existing.HistoricalStateSha256 -cne $historicalHash) { throw 'Protected historical state content drifted.' }
        if ($existing.Status -eq 'Applied') {
            Assert-FiveSiteLive $history @($existing.UnrelatedBindingsBefore)
            Write-Output 'HTTPS_PILOT_QA_EXTENSION_ALREADY_APPLIED_AND_FIVE_SITE_HEALTHY'; exit 0
        }
        if ($existing.Status -in @('RolledBack','AutomaticallyRolledBack')) {
            Assert-FourSiteLive $history @($existing.UnrelatedBindingsBefore)
            throw 'The prior QA extension is rolled back. Remove or archive its protected state through change control before a new apply.'
        }
        throw "Incomplete quality-extension transaction '$($existing.Status)' exists. Run -Rollback first."
    }

    $before = @(Get-IisSnapshot); Assert-HttpBindings $before; Assert-HistoricalBindings $before $history.Thumbprint
    Assert-QaPortFree
    Assert-Firewall (Get-FirewallSnapshot) $history.RemoteAddress @($historicalApplications.HttpsPort)
    Wait-Health http @($applications.HttpPort); Wait-Health https @($historicalApplications.HttpsPort)
    $unrelated = @(Get-UnrelatedBindings $before)
    if (-not $PSCmdlet.ShouldProcess($expectedComputerName, 'Add only QA HTTPS 6170 and expand the restricted pilot firewall to five ports')) {
        if ($WhatIfPreference) { Write-Output 'WHATIF_READY_HTTPS_PILOT_QA_EXTENSION' }; exit 0
    }
    $state = [pscustomobject]@{
        Version = 1; Transaction = 'HttpsPilotQualityExtension'; ComputerName = $expectedComputerName; Status = 'Prepared'
        HistoricalStateSha256 = $historicalHash; CertificateThumbprint = $history.Thumbprint
        PilotRootThumbprint = $history.RootThumbprint; PilotRemoteAddress = @($history.RemoteAddress)
        UnrelatedBindingsBefore = $unrelated; FirewallBefore = Get-FirewallSnapshot
        PriorQaBinding = @()
        PlannedQaBinding = @(New-ExpectedBinding $qualityApplication $history.Thumbprint)
        PreparedAtUtc = [DateTime]::UtcNow.ToString('o'); AppliedQaBinding = $null; AppliedAtUtc = $null
        ApplyFailure = $null; ApplyFailedAtUtc = $null; RollbackStartedAtUtc = $null
        RolledBackAtUtc = $null; RollbackFailure = $null; RollbackFailedAtUtc = $null
    }
    Write-ExtensionState $state
    $state = Read-ProtectedJson $StatePath 'Quality-extension state'
    Assert-ExtensionState $state $history
    if ($state.HistoricalStateSha256 -cne (Get-FileHash -LiteralPath $HistoricalStatePath -Algorithm SHA256).Hash) {
        throw 'Protected historical state drifted after Prepared was persisted.'
    }
    Assert-RecoverableState $state $history
    try {
        Add-QaBinding $history.Thumbprint
        Set-FirewallPorts -Ports @($applications.HttpsPort) -RemoteAddress @($history.RemoteAddress) `
            -ExpectedCurrentPorts @($state.FirewallBefore.LocalPort)
        Assert-FiveSiteLive $history $unrelated
        $state.Status = 'Applied'; $state.AppliedQaBinding = @(New-ExpectedBinding $qualityApplication $history.Thumbprint)
        $state.AppliedAtUtc = [DateTime]::UtcNow.ToString('o'); Write-ExtensionState $state
    }
    catch {
        $applyFailure = $_.Exception.Message; $state.Status = 'ApplyFailedRollbackPending'
        $state.ApplyFailure = $applyFailure; $state.ApplyFailedAtUtc = [DateTime]::UtcNow.ToString('o')
        try { Write-ExtensionState $state } catch { }
        try { Restore-FourSite $state $history; $state.Status = 'AutomaticallyRolledBack'; Write-ExtensionState $state }
        catch { throw "QA extension apply failed: $applyFailure Automatic rollback failed: $($_.Exception.Message)" }
        throw "QA extension apply failed and was automatically rolled back: $applyFailure"
    }
    Write-Output 'HTTPS_PILOT_QA_EXTENSION_APPLIED_AND_FIVE_SITE_HEALTHY'
}
finally {
    if ($transactionMutex) { try { $transactionMutex.ReleaseMutex() } finally { $transactionMutex.Dispose() } }
}
