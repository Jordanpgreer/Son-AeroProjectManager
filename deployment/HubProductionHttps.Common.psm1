Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-HubProductionApplicationMap {
    return @(
        [pscustomobject]@{ Site = 'ProjectTracker';      HostName = 'projects.hub.son4l.local';    HttpPort = 5135; PilotHttpsPort = 6135 },
        [pscustomobject]@{ Site = 'SonAeroPortal';       HostName = 'hub.son4l.local';             HttpPort = 5140; PilotHttpsPort = 6140 },
        [pscustomobject]@{ Site = 'EngineeringHub';      HostName = 'engineering.hub.son4l.local'; HttpPort = 5150; PilotHttpsPort = 6150 },
        [pscustomobject]@{ Site = 'EstimatingDashboard'; HostName = 'estimating.hub.son4l.local';   HttpPort = 5160; PilotHttpsPort = 6160 },
        [pscustomobject]@{ Site = 'QualityAssurance';    HostName = 'quality.hub.son4l.local';      HttpPort = 5170; PilotHttpsPort = 6170 }
    )
}
function ConvertTo-HubThumbprint {
    param([Parameter(Mandatory = $true)][string]$Value)
    $result = ($Value -replace '[\s-]', '').ToUpperInvariant()
    if ($result -notmatch '^[A-F0-9]{40}$') {
        throw 'CertificateThumbprint must be the certificate-store SHA-1 thumbprint (40 hexadecimal characters).'
    }
    return $result
}
function ConvertFrom-HubCertificateHash {
    param($Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [byte[]]) { return ([BitConverter]::ToString($Value)).Replace('-', '') }
    return ([string]$Value).Replace(' ', '').Replace('-', '').ToUpperInvariant()
}
function ConvertTo-HubCertificateHashBytes {
    param([Parameter(Mandatory = $true)][string]$Value)
    $hex = ConvertTo-HubThumbprint $Value
    [byte[]]$bytes = @(for ($index = 0; $index -lt $hex.Length; $index += 2) {
        [Convert]::ToByte($hex.Substring($index, 2), 16)
    })
    # A byte array is enumerable in Windows PowerShell.  The unary comma is required so callers
    # receive one [byte[]] object instead of an [object[]] containing 20 emitted byte values.
    return ,$bytes
}
function Test-HubDnsNameMatch {
    param(
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$DnsName
    )
    $patternValue = $Pattern.Trim().TrimEnd('.').ToLowerInvariant()
    $dnsValue = $DnsName.Trim().TrimEnd('.').ToLowerInvariant()
    if ($patternValue -eq $dnsValue) { return $true }
    if (-not $patternValue.StartsWith('*.')) { return $false }
    $suffix = $patternValue.Substring(2)
    if (-not $dnsValue.EndsWith(".$suffix")) { return $false }
    $left = $dnsValue.Substring(0, $dnsValue.Length - $suffix.Length - 1)
    return -not [string]::IsNullOrWhiteSpace($left) -and $left.IndexOf('.') -lt 0
}
function Split-HubBindingInformation {
    param([Parameter(Mandatory = $true)][string]$BindingInformation)
    $match = [regex]::Match($BindingInformation, '^(.*):(\d+):(.*)$')
    if (-not $match.Success) { throw "Invalid IIS binding information '$BindingInformation'." }
    return [pscustomobject]@{
        Address = $match.Groups[1].Value
        Port = [int]$match.Groups[2].Value
        HostName = $match.Groups[3].Value
    }
}
function Get-HubCanonicalBindingHostName {
    param([AllowEmptyString()][string]$HostName)
    if ($null -eq $HostName) { return '' }
    return $HostName.Trim().TrimEnd('.')
}
function Assert-HubAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated Windows PowerShell session.'
    }
}
function Assert-HubComputerName {
    param([Parameter(Mandatory = $true)][string]$ExpectedComputerName)
    if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
        throw "This operation is restricted to $ExpectedComputerName; current computer is $env:COMPUTERNAME."
    }
}
function Import-HubIisAdministration {
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
function Get-HubCertificateDnsNames {
    param([Parameter(Mandatory = $true)]$Certificate)
    $sanExtensions = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' })
    if ($sanExtensions.Count -ne 1) {
        throw "Certificate must contain exactly one Subject Alternative Name extension; found $($sanExtensions.Count)."
    }
    if ($Certificate.PSObject.Properties.Name -notcontains 'DnsNameList') {
        throw 'DnsNameList is unavailable, so SAN validation cannot be completed safely.'
    }
    return @($Certificate.DnsNameList | ForEach-Object {
        if ($_.PSObject.Properties.Name -contains 'Punycode' -and $_.Punycode) { [string]$_.Punycode }
        elseif ($_.PSObject.Properties.Name -contains 'Unicode' -and $_.Unicode) { [string]$_.Unicode }
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
function Assert-HubProductionCertificateDnsCoverage {
    param(
        [Parameter(Mandatory = $true)][string[]]$DnsNames,
        [Parameter(Mandatory = $true)][object[]]$Applications
    )
    $normalizedNames = @($DnsNames | ForEach-Object {
        ([string]$_).Trim().TrimEnd('.').ToLowerInvariant()
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    if ($normalizedNames -notcontains 'hub.son4l.local') {
        throw "Certificate SAN must contain the exact Portal name 'hub.son4l.local'."
    }
    if ($normalizedNames -notcontains '*.hub.son4l.local') {
        throw "Certificate SAN must contain the managed wildcard name '*.hub.son4l.local'."
    }
    foreach ($application in $Applications) {
        $covered = @($normalizedNames | Where-Object {
            Test-HubDnsNameMatch -Pattern $_ -DnsName $application.HostName
        })
        if ($covered.Count -eq 0) { throw "Certificate SAN does not cover '$($application.HostName)'." }
    }
}
function Assert-HubProductionCertificate {
    param(
        [Parameter(Mandatory = $true)][string]$Thumbprint,
        [Parameter(Mandatory = $true)][object[]]$Applications,
        [ValidateRange(7, 365)][int]$MinimumRemainingDays = 30
    )
    $normalized = ConvertTo-HubThumbprint $Thumbprint
    $certificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$normalized" -ErrorAction SilentlyContinue
    if (-not $certificate) { throw "Certificate $normalized was not found in Cert:\LocalMachine\My." }
    $now = Get-Date
    if (-not $certificate.HasPrivateKey) { throw 'The selected certificate has no private key on SON-IIS2.' }
    if ($certificate.NotBefore -gt $now -or $certificate.NotAfter -lt $now.AddDays($MinimumRemainingDays)) {
        throw "The certificate is not valid for the required $MinimumRemainingDays-day safety window."
    }
    if ($certificate.Subject -eq $certificate.Issuer) { throw 'A self-issued certificate is not permitted for production HTTPS.' }

    $supportedSignatureOids = @(
        '1.2.840.113549.1.1.11', # sha256RSA
        '1.2.840.113549.1.1.12', # sha384RSA
        '1.2.840.113549.1.1.13', # sha512RSA
        '1.2.840.10045.4.3.2',   # ecdsa-with-SHA256
        '1.2.840.10045.4.3.3',   # ecdsa-with-SHA384
        '1.2.840.10045.4.3.4'    # ecdsa-with-SHA512
    )
    if ($certificate.SignatureAlgorithm.Value -notin $supportedSignatureOids) {
        throw "Unsupported or weak certificate signature algorithm '$($certificate.SignatureAlgorithm.FriendlyName)'."
    }
    switch ($certificate.PublicKey.Oid.Value) {
        '1.2.840.113549.1.1.1' {
            $publicKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
            if (-not $publicKey) { throw 'RSA public key was unavailable.' }
            try {
                if ($publicKey.KeySize -lt 2048) { throw "RSA key size $($publicKey.KeySize) is below 2048 bits." }
            }
            finally { $publicKey.Dispose() }
            break
        }
        '1.2.840.10045.2.1' {
            $publicKey = [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPublicKey($certificate)
            if (-not $publicKey) { throw 'ECDSA public key was unavailable.' }
            try {
                if ($publicKey.KeySize -notin @(256, 384, 521)) { throw "ECDSA key size $($publicKey.KeySize) is not approved." }
            }
            finally { $publicKey.Dispose() }
            break
        }
        default { throw "Unsupported certificate public-key algorithm '$($certificate.PublicKey.Oid.Value)'." }
    }

    $basicExtensions = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' })
    if ($basicExtensions.Count -ne 1) { throw "Certificate must contain exactly one Basic Constraints extension; found $($basicExtensions.Count)." }
    $basic = New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(
        $basicExtensions[0], $basicExtensions[0].Critical)
    if ($basic.CertificateAuthority) { throw 'The selected certificate is a CA certificate, not an IIS leaf certificate.' }

    $ekuExtensions = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($ekuExtensions.Count -ne 1) { throw "Certificate must contain exactly one Enhanced Key Usage extension; found $($ekuExtensions.Count)." }
    $eku = New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
        $ekuExtensions[0], $ekuExtensions[0].Critical)
    $ekuValues = @($eku.EnhancedKeyUsages | ForEach-Object { [string]$_.Value })
    if ($ekuValues -notcontains '1.3.6.1.5.5.7.3.1') { throw 'The certificate lacks the Server Authentication EKU.' }

    $keyUsageExtensions = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.15' })
    if ($keyUsageExtensions.Count -gt 1) { throw 'The certificate contains duplicate Key Usage extensions.' }
    if ($keyUsageExtensions.Count -eq 1) {
        $usage = New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            $keyUsageExtensions[0], $keyUsageExtensions[0].Critical)
        if (($usage.KeyUsages -band [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -eq 0) {
            throw 'The certificate Key Usage does not allow Digital Signature.'
        }
    }

    $dnsNames = @(Get-HubCertificateDnsNames $certificate)
    Assert-HubProductionCertificateDnsCoverage -DnsNames $dnsNames -Applications $Applications

    $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
    try {
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag = [Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
        $chain.ChainPolicy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        $chain.ChainPolicy.VerificationTime = $now
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(15)
        if (-not $chain.Build($certificate)) {
            $details = @($chain.ChainStatus | ForEach-Object {
                "$($_.Status): $($_.StatusInformation.Trim())"
            } | Select-Object -Unique) -join '; '
            throw "Certificate chain/revocation validation failed: $details"
        }
    }
    finally { $chain.Dispose() }
    return $certificate
}
function Assert-HubProductionDns {
    param(
        [Parameter(Mandatory = $true)][object[]]$Applications,
        [Parameter(Mandatory = $true)][string]$ExpectedServerAddress
    )
    $expectedIp = $null
    if (-not [Net.IPAddress]::TryParse($ExpectedServerAddress, [ref]$expectedIp)) {
        throw "ExpectedServerAddress '$ExpectedServerAddress' is not a valid IP address."
    }
    $expectedAddress = $expectedIp.ToString()
    foreach ($application in $Applications) {
        # The default A_AAAA query is intentional.  An unexpected IPv6 record could route a
        # workstation somewhere other than SON-IIS2 even when the expected IPv4 A record exists.
        $records = @(Resolve-DnsName -Name $application.HostName -DnsOnly -ErrorAction Stop |
            Where-Object { $_.PSObject.Properties.Name -contains 'IPAddress' -and $_.IPAddress } |
            ForEach-Object {
                $address = $null
                if (-not [Net.IPAddress]::TryParse([string]$_.IPAddress, [ref]$address)) {
                    throw "DNS returned invalid address '$($_.IPAddress)' for '$($application.HostName)'."
                }
                $address.ToString()
            } | Sort-Object -Unique)
        if ($records.Count -eq 0) { throw "DNS returned no IP address for '$($application.HostName)'." }
        if ($records.Count -ne 1 -or $records[0] -ne $expectedAddress) {
            throw "DNS for '$($application.HostName)' must resolve only to $expectedAddress; found $($records -join ', ')."
        }
    }
}
function ConvertTo-HubBindingSnapshot {
    param([Parameter(Mandatory = $true)]$Binding, [Parameter(Mandatory = $true)][string]$Site)
    return [pscustomobject]@{
        Site = $Site
        Protocol = [string]$Binding.Protocol
        BindingInformation = [string]$Binding.BindingInformation
        CertificateHash = ConvertFrom-HubCertificateHash $Binding.CertificateHash
        CertificateStoreName = [string]$Binding.CertificateStoreName
        SslFlags = [int]$Binding.SslFlags
    }
}
function Get-HubIisBindingSnapshot {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $result = @()
        foreach ($site in $manager.Sites) {
            foreach ($binding in $site.Bindings) {
                $result += ConvertTo-HubBindingSnapshot -Binding $binding -Site $site.Name
            }
        }
        return @($result)
    }
    finally { $manager.Dispose() }
}
function Test-HubIsTargetBinding {
    param([Parameter(Mandatory = $true)]$Binding, [Parameter(Mandatory = $true)][object[]]$Applications)
    if ($Binding.Protocol -ine 'https') { return $false }
    $parts = Split-HubBindingInformation $Binding.BindingInformation
    if ($parts.Port -ne 443) { return $false }
    $hostName = Get-HubCanonicalBindingHostName $parts.HostName
    return @($Applications | Where-Object { $_.HostName -ieq $hostName }).Count -gt 0
}
function Get-HubTargetBindingSnapshot {
    param([Parameter(Mandatory = $true)][object[]]$Snapshot, [Parameter(Mandatory = $true)][object[]]$Applications)
    return @($Snapshot | Where-Object { Test-HubIsTargetBinding -Binding $_ -Applications $Applications })
}
function Get-HubComparableBindings {
    param([AllowEmptyCollection()][object[]]$Bindings)
    return (@($Bindings | ForEach-Object {
        '{0}|{1}|{2}|{3}|{4}|{5}' -f $_.Site, $_.Protocol, $_.BindingInformation,
            (ConvertFrom-HubCertificateHash $_.CertificateHash), $_.CertificateStoreName, ([int]$_.SslFlags)
    } | Sort-Object) -join "`n")
}
function Assert-HubBaseBindings {
    param([Parameter(Mandatory = $true)][object[]]$Snapshot, [Parameter(Mandatory = $true)][object[]]$Applications)
    foreach ($application in $Applications) {
        $httpInformation = "*:$($application.HttpPort):"
        $httpMatches = @($Snapshot | Where-Object {
            $_.Site -eq $application.Site -and $_.Protocol -eq 'http' -and $_.BindingInformation -eq $httpInformation
        })
        if ($httpMatches.Count -ne 1) {
            throw "Site '$($application.Site)' must retain exactly one HTTP binding '$httpInformation'; found $($httpMatches.Count)."
        }
        $pilotInformation = "*:$($application.PilotHttpsPort):"
        $pilotMatches = @($Snapshot | Where-Object {
            $_.Site -eq $application.Site -and $_.Protocol -eq 'https' -and $_.BindingInformation -eq $pilotInformation
        })
        if ($pilotMatches.Count -ne 1) {
            throw "Site '$($application.Site)' must retain exactly one pilot HTTPS binding '$pilotInformation'; found $($pilotMatches.Count)."
        }
    }
}
function Assert-HubProductionBindingAvailability {
    param(
        [Parameter(Mandatory = $true)][object[]]$Snapshot,
        [Parameter(Mandatory = $true)][object[]]$Applications,
        [Parameter(Mandatory = $true)][string]$Thumbprint
    )
    $null = ConvertTo-HubThumbprint $Thumbprint
    foreach ($binding in @($Snapshot)) {
        if ($binding.Protocol -inotin @('http', 'https')) {
            if ([string]$binding.BindingInformation -match '(^|:)443(?=:|$)') {
                throw "Unsupported protocol '$($binding.Protocol)' may reserve TCP 443 on site '$($binding.Site)'."
            }
            continue
        }
        $parts = Split-HubBindingInformation $binding.BindingInformation
        if ($parts.Port -ne 443) { continue }
        if ($binding.Protocol -ine 'https') {
            throw "Conflicting non-HTTPS binding '$($binding.BindingInformation)' uses TCP 443 on site '$($binding.Site)'."
        }
        $hostName = Get-HubCanonicalBindingHostName $parts.HostName
        if ($parts.HostName -ine $hostName) {
            throw "HTTPS binding '$($binding.BindingInformation)' has a non-canonical host name."
        }
        $target = @($Applications | Where-Object { $_.HostName -ieq $hostName })
        if ($target.Count -eq 0) {
            if ([string]::IsNullOrWhiteSpace($hostName) -or $hostName.IndexOfAny([char[]]'*+?') -ge 0 -or
                ([int]$binding.SslFlags -band 1) -eq 0) {
                throw "Conflicting non-SNI HTTPS binding '$($binding.BindingInformation)' exists on site '$($binding.Site)'."
            }
            continue
        }
        if ($target.Count -ne 1 -or $binding.Site -ine $target[0].Site) {
            throw "Production host '$($parts.HostName)' is bound to the wrong IIS site '$($binding.Site)'."
        }
        if ($parts.Address -ne '*') {
            throw "Existing binding '$($binding.BindingInformation)' must use the wildcard IIS IP address before it can be reconciled safely."
        }
        $boundThumbprint = ConvertFrom-HubCertificateHash $binding.CertificateHash
        try { $null = ConvertTo-HubThumbprint $boundThumbprint }
        catch { throw "Existing binding '$($binding.BindingInformation)' has an invalid certificate hash and cannot be reconciled safely." }
        if ($binding.CertificateStoreName -ine 'My' -or ([int]$binding.SslFlags -notin @(0, 1))) {
            throw "Existing binding '$($binding.BindingInformation)' does not use LocalMachine\\My or a reconcilable SNI mode."
        }
    }
    foreach ($application in $Applications) {
        $matches = @($Snapshot | Where-Object {
            if ($_.Protocol -ine 'https') { return $false }
            $parts = Split-HubBindingInformation $_.BindingInformation
            $hostName = Get-HubCanonicalBindingHostName $parts.HostName
            return $parts.Port -eq 443 -and $hostName -ieq $application.HostName
        })
        if ($matches.Count -gt 1) { throw "Production host '$($application.HostName)' has duplicate IIS bindings." }
    }
}
function New-HubDesiredBindingSnapshot {
    param(
        [Parameter(Mandatory = $true)][object[]]$Applications,
        [Parameter(Mandatory = $true)][string]$Thumbprint
    )
    $normalized = ConvertTo-HubThumbprint $Thumbprint
    return @($Applications | ForEach-Object {
        [pscustomobject]@{
            Site = $_.Site
            Protocol = 'https'
            BindingInformation = "*:443:$($_.HostName)"
            CertificateHash = $normalized
            CertificateStoreName = 'My'
            SslFlags = 1
        }
    })
}
function Test-HubDesiredBindings {
    param(
        [Parameter(Mandatory = $true)][object[]]$Snapshot,
        [Parameter(Mandatory = $true)][object[]]$Applications,
        [Parameter(Mandatory = $true)][string]$Thumbprint
    )
    $normalized = ConvertTo-HubThumbprint $Thumbprint
    foreach ($application in $Applications) {
        $information = "*:443:$($application.HostName)"
        $matches = @($Snapshot | Where-Object {
            $_.Site -eq $application.Site -and $_.Protocol -eq 'https' -and $_.BindingInformation -ieq $information -and
            (ConvertFrom-HubCertificateHash $_.CertificateHash) -eq $normalized -and
            $_.CertificateStoreName -ieq 'My' -and ([int]$_.SslFlags -eq 1)
        })
        if ($matches.Count -ne 1) { return $false }
    }
    return $true
}
function Set-HubDesiredBindings {
    param(
        [Parameter(Mandatory = $true)][object[]]$Applications,
        [Parameter(Mandatory = $true)][string]$Thumbprint
    )
    $hashBytes = ConvertTo-HubCertificateHashBytes $Thumbprint
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        foreach ($application in $Applications) {
            $site = $manager.Sites[$application.Site]
            if (-not $site) { throw "IIS site '$($application.Site)' does not exist." }
            $information = "*:443:$($application.HostName)"
            $matches = @($site.Bindings | Where-Object {
                $_.Protocol -eq 'https' -and $_.BindingInformation -ieq $information
            })
            if ($matches.Count -eq 0) {
                $binding = $site.Bindings.Add($information, $hashBytes, 'My')
                $binding.Protocol = 'https'
            }
            else { $binding = $matches[0] }
            $binding.CertificateHash = $hashBytes
            $binding.CertificateStoreName = 'My'
            $binding.SslFlags = [Microsoft.Web.Administration.SslFlags]::Sni
        }
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}
function Restore-HubTargetBindings {
    param([Parameter(Mandatory = $true)][object[]]$Applications, [AllowEmptyCollection()][object[]]$Bindings)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        foreach ($site in $manager.Sites) {
            foreach ($binding in @($site.Bindings)) {
                $snapshot = ConvertTo-HubBindingSnapshot -Binding $binding -Site $site.Name
                if (Test-HubIsTargetBinding -Binding $snapshot -Applications $Applications) { $site.Bindings.Remove($binding) }
            }
        }
        foreach ($snapshot in @($Bindings)) {
            $site = $manager.Sites[$snapshot.Site]
            if (-not $site) { throw "Cannot restore missing IIS site '$($snapshot.Site)'." }
            $binding = $site.Bindings.Add($snapshot.BindingInformation, $snapshot.Protocol)
            if ($snapshot.Protocol -eq 'https') {
                $binding.CertificateHash = ConvertTo-HubCertificateHashBytes $snapshot.CertificateHash
                $binding.CertificateStoreName = $snapshot.CertificateStoreName
                $binding.SslFlags = [Microsoft.Web.Administration.SslFlags]([int]$snapshot.SslFlags)
            }
        }
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
}

function Wait-HubEndpointHealth {
    param(
        [Parameter(Mandatory = $true)][object[]]$Applications,
        [ValidateSet('http', 'pilotHttps', 'https')][string]$Scheme,
        [ValidateRange(30, 600)][int]$TimeoutSeconds = 180,
        [string]$ExpectedComputerName = 'SON-IIS2'
    )
    $pending = @($Applications)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastErrors = @{}
    do {
        foreach ($application in @($pending)) {
            $uri = switch ($Scheme) {
                'https' { "https://$($application.HostName)/api/health"; break }
                'pilotHttps' { "https://$ExpectedComputerName`:$($application.PilotHttpsPort)/api/health"; break }
                default { "http://$ExpectedComputerName`:$($application.HttpPort)/api/health" }
            }
            try {
                $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec 10 -MaximumRedirection 0
                if ($response.StatusCode -eq 200) {
                    $pending = @($pending | Where-Object { $_.Site -ne $application.Site })
                    $lastErrors.Remove($application.Site)
                }
                else { $lastErrors[$application.Site] = "HTTP $($response.StatusCode) from $uri" }
            }
            catch { $lastErrors[$application.Site] = "$uri - $($_.Exception.Message)" }
        }
        if ($pending.Count -gt 0) { Start-Sleep -Milliseconds 750 }
    } while ($pending.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($pending.Count -gt 0) {
        $details = @($pending | ForEach-Object {
            if ($lastErrors.ContainsKey($_.Site)) { $lastErrors[$_.Site] }
            else { $_.HostName }
        }) -join ' | '
        throw "$Scheme health verification timed out: $details"
    }
}

Export-ModuleMember -Function @(
    'Get-HubProductionApplicationMap', 'ConvertTo-HubThumbprint', 'ConvertFrom-HubCertificateHash',
    'ConvertTo-HubCertificateHashBytes', 'Test-HubDnsNameMatch', 'Split-HubBindingInformation',
    'Assert-HubAdministrator', 'Assert-HubComputerName', 'Import-HubIisAdministration',
    'Assert-HubProductionCertificateDnsCoverage', 'Assert-HubProductionCertificate',
    'Assert-HubProductionDns', 'Get-HubIisBindingSnapshot',
    'Get-HubTargetBindingSnapshot', 'Get-HubComparableBindings', 'Assert-HubBaseBindings',
    'Assert-HubProductionBindingAvailability', 'New-HubDesiredBindingSnapshot',
    'Test-HubDesiredBindings', 'Set-HubDesiredBindings',
    'Restore-HubTargetBindings', 'Wait-HubEndpointHealth'
)
