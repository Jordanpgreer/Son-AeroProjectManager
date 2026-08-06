<#
    Read-only HTTPS prerequisite audit for the four SON-AERO Hub IIS sites.
    Run from an elevated Windows PowerShell 5.1 session on SON-IIS2.

    The audit validates server-side prerequisites only. It never creates or changes an IIS binding
    and it cannot prove certificate-chain trust on employee workstations.
#>
[CmdletBinding()]
param(
    [string]$ExpectedComputerName = 'SON-IIS2',
    [Parameter(Mandatory)]
    [ValidatePattern('^(?:[A-Fa-f0-9]{2}\s*){20}$')]
    [string]$CertificateThumbprint,
    [string[]]$RequiredDnsNames = @('SON-IIS2', 'SON-IIS2.SON4L.LOCAL'),
    [ValidateRange(7, 365)]
    [int]$MinimumRemainingDays = 30
)

$ErrorActionPreference = 'Stop'

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This audit is for $ExpectedComputerName; the current computer is $env:COMPUTERNAME."
}
if (-not $RequiredDnsNames -or @($RequiredDnsNames | Where-Object {
    [string]::IsNullOrWhiteSpace($_)
}).Count -gt 0) {
    throw 'RequiredDnsNames must contain one or more non-empty DNS names.'
}

# Windows certificate-store thumbprints are SHA-1 identifiers even when the certificate itself is
# signed with SHA-256 or stronger. Do not accept an arbitrary SHA-256 file hash here.
$thumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
if ($thumbprint -notmatch '^[A-F0-9]{40}$') {
    throw 'CertificateThumbprint must be the certificate store SHA-1 thumbprint (exactly 40 hexadecimal characters).'
}

$certificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$thumbprint" -ErrorAction SilentlyContinue
if (-not $certificate) {
    throw "Certificate $thumbprint was not found in Cert:\LocalMachine\My."
}

$failures = [System.Collections.Generic.List[string]]::new()
$now = Get-Date
$minimumExpiry = $now.AddDays($MinimumRemainingDays)

if (-not $certificate.HasPrivateKey) {
    $failures.Add('The certificate does not have a private key on this server.')
}
if ($certificate.NotBefore -gt $now) {
    $failures.Add("The certificate is not valid until $($certificate.NotBefore.ToString('u')).")
}
if ($certificate.NotAfter -lt $minimumExpiry) {
    $failures.Add("The certificate expires before the required $MinimumRemainingDays-day safety window at $($certificate.NotAfter.ToString('u')).")
}
if ($certificate.Subject -eq $certificate.Issuer) {
    $failures.Add('The certificate is self-issued; use a certificate issued by the trusted internal CA.')
}

$basicConstraintsExtension = $certificate.Extensions |
    Where-Object { $_.Oid.Value -eq '2.5.29.19' } |
    Select-Object -First 1
if (-not $basicConstraintsExtension) {
    $failures.Add('The certificate has no Basic Constraints extension, so end-entity status cannot be proven.')
}
else {
    try {
        $basicConstraints = [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
            $basicConstraintsExtension,
            $basicConstraintsExtension.Critical
        )
        if ($basicConstraints.CertificateAuthority) {
            $failures.Add('The selected certificate is a CA certificate, not an IIS server certificate.')
        }
    }
    catch {
        $failures.Add("The Basic Constraints extension could not be decoded: $($_.Exception.Message)")
    }
}

$serverAuthenticationOid = '1.3.6.1.5.5.7.3.1'
$ekuExtensions = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
if ($ekuExtensions.Count -ne 1) {
    $failures.Add("The certificate must contain exactly one Enhanced Key Usage extension; found $($ekuExtensions.Count).")
}
else {
    try {
        $parsedEku = New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
            $ekuExtensions[0], $ekuExtensions[0].Critical)
        $ekuOids = @($parsedEku.EnhancedKeyUsages | ForEach-Object { [string]$_.Value } | Where-Object { $_ })
        if ($ekuOids -notcontains $serverAuthenticationOid) {
            $failures.Add('The certificate does not include the Server Authentication EKU.')
        }
    }
    catch { $failures.Add("The Enhanced Key Usage extension could not be decoded: $($_.Exception.Message)") }
}

$keyUsageExtension = $certificate.Extensions |
    Where-Object { $_.Oid.Value -eq '2.5.29.15' } |
    Select-Object -First 1
$keyUsageNames = @()
$hasDigitalSignature = $false
$hasKeyEncipherment = $false
if (-not $keyUsageExtension) {
    $failures.Add('The certificate has no Key Usage extension, so TLS key usage cannot be constrained.')
}
else {
    try {
        $keyUsage = [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            $keyUsageExtension,
            $keyUsageExtension.Critical
        )
        $keyUsageNames = @($keyUsage.KeyUsages.ToString().Split(',') | ForEach-Object { $_.Trim() })
        $hasDigitalSignature = ($keyUsage.KeyUsages -band
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -ne 0
        $hasKeyEncipherment = ($keyUsage.KeyUsages -band
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment) -ne 0
        if (-not $hasDigitalSignature) {
            $failures.Add('The certificate Key Usage does not permit Digital Signature.')
        }
    }
    catch {
        $failures.Add("The Key Usage extension could not be decoded: $($_.Exception.Message)")
    }
}

$supportedSignatureOids = @(
    '1.2.840.113549.1.1.11', # sha256RSA
    '1.2.840.113549.1.1.12', # sha384RSA
    '1.2.840.113549.1.1.13', # sha512RSA
    '1.2.840.10045.4.3.2',   # ecdsa-with-SHA256
    '1.2.840.10045.4.3.3',   # ecdsa-with-SHA384
    '1.2.840.10045.4.3.4'    # ecdsa-with-SHA512
)
if ($certificate.SignatureAlgorithm.Value -notin $supportedSignatureOids) {
    $failures.Add("Unsupported or weak certificate signature algorithm '$($certificate.SignatureAlgorithm.FriendlyName)' ($($certificate.SignatureAlgorithm.Value)).")
}

$rsaPublicKeyOid = '1.2.840.113549.1.1.1'
$ecPublicKeyOid = '1.2.840.10045.2.1'
$publicKeySize = 0
switch ($certificate.PublicKey.Oid.Value) {
    $rsaPublicKeyOid {
        try {
            $publicKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
            if (-not $publicKey) {
                throw 'RSA public key was unavailable.'
            }
            try { $publicKeySize = $publicKey.KeySize } finally { $publicKey.Dispose() }
            if ($publicKeySize -lt 2048) {
                $failures.Add("The RSA public key is only $publicKeySize bits; at least 2048 bits are required.")
            }
            if (-not $hasKeyEncipherment) {
                $failures.Add('An RSA IIS certificate must permit Key Encipherment as well as Digital Signature.')
            }
        }
        catch {
            $failures.Add("The RSA public key could not be validated: $($_.Exception.Message)")
        }
        break
    }
    $ecPublicKeyOid {
        try {
            $publicKey = [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPublicKey($certificate)
            if (-not $publicKey) {
                throw 'ECDSA public key was unavailable on this Windows/.NET runtime.'
            }
            try { $publicKeySize = $publicKey.KeySize } finally { $publicKey.Dispose() }
            if ($publicKeySize -notin @(256, 384, 521)) {
                $failures.Add("The ECDSA public key size $publicKeySize is not an approved P-256/P-384/P-521 size.")
            }
        }
        catch {
            $failures.Add("The ECDSA public key could not be validated: $($_.Exception.Message)")
        }
        break
    }
    default {
        $failures.Add("Unsupported public-key algorithm '$($certificate.PublicKey.Oid.FriendlyName)' ($($certificate.PublicKey.Oid.Value)).")
    }
}

# DnsNameList is language-neutral and available for certificates returned by the Windows
# certificate provider. Do not parse localized extension display text as a fallback.
$certificateDnsNames = @()
if ($certificate.PSObject.Properties.Name -notcontains 'DnsNameList') {
    $failures.Add('DnsNameList is unavailable on this host, so SAN entries cannot be validated safely under Windows PowerShell 5.1.')
}
else {
    try {
        $certificateDnsNames = @($certificate.DnsNameList | ForEach-Object {
            if ($_.PSObject.Properties.Name -contains 'Punycode' -and $_.Punycode) {
                $_.Punycode
            }
            elseif ($_.PSObject.Properties.Name -contains 'Unicode' -and $_.Unicode) {
                $_.Unicode
            }
        } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($certificateDnsNames.Count -eq 0) {
            $failures.Add('DnsNameList contains no DNS SAN entries.')
        }
    }
    catch {
        $failures.Add("DnsNameList could not be read safely: $($_.Exception.Message)")
    }
}

foreach ($requiredName in $RequiredDnsNames) {
    if (-not ($certificateDnsNames | Where-Object { $_ -ieq $requiredName })) {
        $failures.Add("Certificate SAN does not include $requiredName.")
    }
}

$chainTrusted = $false
$chainStatus = @()
$chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
try {
    $chain.ChainPolicy.RevocationMode =
        [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
    $chain.ChainPolicy.RevocationFlag =
        [Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
    $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(15)
    $chainTrusted = $chain.Build($certificate)
    $chainStatus = @($chain.ChainStatus | ForEach-Object {
        '{0}: {1}' -f $_.Status, $_.StatusInformation.Trim()
    })
    if (-not $chainTrusted) {
        $failures.Add('The certificate chain or revocation status is not trusted on SON-IIS2: ' + ($chainStatus -join '; '))
    }
}
finally {
    $chain.Dispose()
}

Import-Module WebAdministration
$expectedSites = @(
    [pscustomobject]@{ Name = 'ProjectTracker'; Port = 5135 },
    [pscustomobject]@{ Name = 'SonAeroPortal'; Port = 5140 },
    [pscustomobject]@{ Name = 'EngineeringHub'; Port = 5150 },
    [pscustomobject]@{ Name = 'EstimatingDashboard'; Port = 5160 }
)

$allBindings = [System.Collections.Generic.List[object]]::new()
foreach ($iisSite in @(Get-Website)) {
    foreach ($binding in @(Get-WebBinding -Name $iisSite.Name)) {
        $port = $null
        if ($binding.bindingInformation -match ':(?<port>\d+):[^:]*$') {
            $port = [int]$Matches['port']
        }
        else {
            $failures.Add("IIS binding '$($binding.bindingInformation)' on '$($iisSite.Name)' could not be parsed safely.")
        }
        $allBindings.Add([pscustomobject]@{
            Site = $iisSite.Name
            Protocol = [string]$binding.protocol
            BindingInformation = [string]$binding.bindingInformation
            Port = $port
        })
    }
}

$siteResults = foreach ($expectedSite in $expectedSites) {
    $site = Get-Website -Name $expectedSite.Name -ErrorAction SilentlyContinue
    if (-not $site) {
        $failures.Add("IIS site $($expectedSite.Name) does not exist.")
        [pscustomobject]@{
            Site = $expectedSite.Name
            Port = $expectedSite.Port
            State = 'Missing'
            CurrentBindings = @()
        }
        continue
    }

    $siteBindings = @($allBindings | Where-Object Site -EQ $expectedSite.Name)
    $expectedBindingInformation = "*:$($expectedSite.Port):"
    $expectedHttpBindings = @($siteBindings | Where-Object {
        $_.Protocol -eq 'http' -and $_.BindingInformation -eq $expectedBindingInformation
    })
    if ($expectedHttpBindings.Count -ne 1 -or $siteBindings.Count -ne 1) {
        $failures.Add("IIS site $($expectedSite.Name) must currently have exactly one binding: http $expectedBindingInformation.")
    }
    if ($site.State -ne 'Started') {
        $failures.Add("IIS site $($expectedSite.Name) is $($site.State), not Started.")
    }

    $conflicts = @($allBindings | Where-Object {
        $_.Port -eq $expectedSite.Port -and $_.Site -ne $expectedSite.Name
    })
    if ($conflicts.Count -gt 0) {
        $conflictText = @($conflicts | ForEach-Object {
            "$($_.Site) $($_.Protocol) $($_.BindingInformation)"
        }) -join '; '
        $failures.Add("Port $($expectedSite.Port) is also bound by another IIS site: $conflictText.")
    }

    [pscustomobject]@{
        Site = $expectedSite.Name
        Port = $expectedSite.Port
        State = $site.State
        CurrentBindings = @($siteBindings | ForEach-Object {
            "$($_.Protocol) $($_.BindingInformation)"
        })
        PortConflicts = @($conflicts | ForEach-Object {
            "$($_.Site) $($_.Protocol) $($_.BindingInformation)"
        })
    }
}

$serverReady = $failures.Count -eq 0
[pscustomobject]@{
    Status = if ($serverReady) {
        'HTTPS_SERVER_PREREQUISITES_READY_WORKSTATION_TRUST_PENDING'
    }
    else {
        'HTTPS_NOT_READY'
    }
    ComputerName = $env:COMPUTERNAME
    Thumbprint = $thumbprint
    Subject = $certificate.Subject
    Issuer = $certificate.Issuer
    NotBefore = $certificate.NotBefore
    NotAfter = $certificate.NotAfter
    MinimumRemainingDays = $MinimumRemainingDays
    PublicKeyAlgorithm = $certificate.PublicKey.Oid.FriendlyName
    PublicKeySize = $publicKeySize
    SignatureAlgorithm = $certificate.SignatureAlgorithm.FriendlyName
    KeyUsages = $keyUsageNames
    HasPrivateKey = $certificate.HasPrivateKey
    DnsNames = $certificateDnsNames
    ChainTrustedOnServer = $chainTrusted
    ChainStatus = $chainStatus
    ServerPrerequisitesReady = $serverReady
    WorkstationTrustStatus = 'NOT_VERIFIED_BY_THIS_SERVER_AUDIT'
    WorkstationTrustReminder = 'Before rollout, test the final HTTPS URL from representative domain workstations and confirm there is no certificate warning.'
    Sites = $siteResults
    Failures = $failures.ToArray()
}

if ($failures.Count -gt 0) {
    throw "HTTPS readiness failed: $($failures -join ' ') No IIS bindings were changed."
}
