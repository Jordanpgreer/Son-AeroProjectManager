<#
    PILOT ONLY. Creates a private pilot root CA and one SON-IIS2 IIS leaf certificate.
    Run interactively on the explicitly named, secured admin workstation. Never run this script on
    SON-IIS2 or SON-SQL2. This is not a replacement for managed enterprise PKI: the lightweight
    pilot CA has no CA database, CRL distribution point, or OCSP responder.
    Only SERVER-PILOT-HANDOFF may be copied to SON-IIS2. Keep the root PFX offline.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,62}$')]
    [string]$ExpectedAdminWorkstationName,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,
    [ValidateRange(1, 5)]
    [int]$RootValidityYears = 3,
    [ValidateRange(30, 180)]
    [int]$LeafValidityDays = 90
)
$ErrorActionPreference = 'Stop'
$requiredServerName = 'SON-IIS2'
$requiredDnsNames = @('SON-IIS2', 'SON-IIS2.SON4L.LOCAL')
$forbiddenSigningHosts = @('SON-IIS2', 'SON-SQL2')
$bundleType = 'SonAeroHubPilotPki'
$schemaVersion = 1
function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    $hasher = [Security.Cryptography.SHA256]::Create(); try { return ([BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($Path)))).Replace('-', '') } finally { $hasher.Dispose() }
}
function Get-NormalizedThumbprint {
    param([Parameter(Mandatory)][string]$Thumbprint)
    return ($Thumbprint -replace '\s', '').ToUpperInvariant()
}
function Test-SecureStringEqual {
    param(
        [Parameter(Mandatory)][Security.SecureString]$First,
        [Parameter(Mandatory)][Security.SecureString]$Second
    )
    $firstPointer = [IntPtr]::Zero
    $secondPointer = [IntPtr]::Zero
    try {
        $firstPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($First)
        $secondPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Second)
        $firstByteLength = [Runtime.InteropServices.Marshal]::ReadInt32($firstPointer, -4)
        $secondByteLength = [Runtime.InteropServices.Marshal]::ReadInt32($secondPointer, -4)
        if ($firstByteLength -ne $secondByteLength) {
            return $false
        }
        for ($offset = 0; $offset -lt $firstByteLength; $offset += 2) {
            if ([Runtime.InteropServices.Marshal]::ReadInt16($firstPointer, $offset) -ne
                [Runtime.InteropServices.Marshal]::ReadInt16($secondPointer, $offset)) {
                return $false
            }
        }
        return $true
    }
    finally {
        if ($firstPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($firstPointer)
        }
        if ($secondPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secondPointer)
        }
    }
}
function Read-ConfirmedPfxPassword {
    param([Parameter(Mandatory)][string]$Purpose)
    while ($true) {
        $first = Read-Host "$Purpose (minimum 16 characters; it will not be displayed)" -AsSecureString
        if ($first.Length -lt 16) {
            Write-Warning 'The PFX password must contain at least 16 characters.'
            continue
        }
        $second = Read-Host "Confirm $Purpose" -AsSecureString
        if (Test-SecureStringEqual -First $first -Second $second) {
            return $first
        }
        Write-Warning 'The passwords did not match. Try again.'
    }
}
function Assert-PrivateOutputLocation {
    param([Parameter(Mandatory)][string]$Path)
    if (-not [IO.Path]::IsPathRooted($Path)) {
        throw 'OutputDirectory must be an absolute local path.'
    }
    $fullPath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
    if ($fullPath.StartsWith('\\', [StringComparison]::Ordinal) -or
        $fullPath -match '^[A-Za-z]:\\?$') {
        throw 'OutputDirectory must be a non-root directory on a local NTFS or ReFS volume, not a UNC path.'
    }
    $qualifier = Split-Path -Path $fullPath -Qualifier
    if ($qualifier -notmatch '^[A-Za-z]:$') {
        throw 'OutputDirectory must use a local drive-letter path.'
    }
    $disk = New-Object IO.DriveInfo($qualifier)
    if (-not $disk.IsReady -or $disk.DriveType -notin @([IO.DriveType]::Removable, [IO.DriveType]::Fixed) -or
        $disk.DriveFormat -notin @('NTFS', 'ReFS')) {
        throw 'Private PKI artifacts must be written to a local/removable NTFS or ReFS volume. Network and FAT/exFAT volumes are refused.'
    }

    $cursor = $fullPath
    while (-not [string]::IsNullOrWhiteSpace($cursor)) {
        if (Test-Path -LiteralPath (Join-Path $cursor '.git')) {
            throw 'OutputDirectory cannot be inside a Git worktree. Private PFX files must never be committed.'
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $fullPath.TrimEnd('\')
}

function Set-PrivateDirectoryAcl {
    param([Parameter(Mandatory)][string]$Path)

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $administratorsSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($currentSid, $systemSid, $administratorsSid)) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            $propagation,
            $allow
        )
        $null = $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Get-PfxCertificates {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][Security.SecureString]$Password
    )
    $data = Get-PfxData -FilePath $Path -Password $Password
    return @($data.EndEntityCertificates) + @($data.OtherCertificates)
}

function Test-ExistingBundle {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$BundleRoot
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return $null
    }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne $schemaVersion -or $manifest.BundleType -ne $bundleType -or
        $manifest.PilotOnly -ne $true -or $manifest.AdminWorkstation -ine $env:COMPUTERNAME) {
        throw 'The existing pilot bundle manifest is incompatible or belongs to another workstation.'
    }
    foreach ($file in @($manifest.Files)) {
        if ([string]::IsNullOrWhiteSpace($file.RelativePath) -or
            [IO.Path]::IsPathRooted([string]$file.RelativePath) -or
            ([string]$file.RelativePath).Contains('..')) {
            throw 'The existing manifest contains an unsafe relative path.'
        }
        $artifact = [IO.Path]::GetFullPath((Join-Path $BundleRoot ([string]$file.RelativePath)))
        if (-not $artifact.StartsWith($BundleRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
            throw "The existing pilot bundle is incomplete: $($file.RelativePath)"
        }
        $actualHash = Get-Sha256 $artifact
        if ($actualHash -ne ([string]$file.Sha256).ToUpperInvariant()) {
            throw "The existing pilot artifact failed its SHA-256 integrity check: $($file.RelativePath)"
        }
    }
    return $manifest
}

$currentComputer = [string]$env:COMPUTERNAME
if ([string]::IsNullOrWhiteSpace($currentComputer) -or $currentComputer -ine $ExpectedAdminWorkstationName) {
    throw "This signing operation is restricted to $ExpectedAdminWorkstationName; the current computer is '$currentComputer'."
}
if ($forbiddenSigningHosts -icontains $currentComputer) {
    throw 'PILOT SECURITY STOP: the root CA private key must never be created on SON-IIS2 or SON-SQL2.'
}
if ($PSVersionTable.PSVersion.Major -lt 5) {
    throw 'Windows PowerShell 5.1 or later is required.'
}

$fullOutput = Assert-PrivateOutputLocation -Path $OutputDirectory
$offlineDirectory = Join-Path $fullOutput 'OFFLINE-ROOT-PRIVATE-DO-NOT-COPY'
$serverDirectory = Join-Path $fullOutput 'SERVER-PILOT-HANDOFF'
$trustDirectory = Join-Path $fullOutput 'WORKSTATION-PILOT-TRUST'
$rootPfxPath = Join-Path $offlineDirectory 'SonAero-Hub-Pilot-Root-PRIVATE.pfx'
$rootRecoveryManifestPath = Join-Path $offlineDirectory 'SonAero-Hub-Pilot-Root-Recovery-Manifest.json'
$serverRootCerPath = Join-Path $serverDirectory 'SonAero-Hub-Pilot-Root-PUBLIC.cer'
$serverLeafPfxPath = Join-Path $serverDirectory 'SON-IIS2-Pilot-IIS-Leaf.pfx'
$serverLeafCerPath = Join-Path $serverDirectory 'SON-IIS2-Pilot-IIS-Leaf-PUBLIC.cer'
$serverManifestPath = Join-Path $serverDirectory 'SonAero-Hub-Pilot-Server-Manifest.json'
$trustRootCerPath = Join-Path $trustDirectory 'SonAero-Hub-Pilot-Root-PUBLIC.cer'
$trustManifestPath = Join-Path $trustDirectory 'SonAero-Hub-Pilot-Trust-Manifest.json'
$masterManifestPath = Join-Path $offlineDirectory 'SonAero-Hub-Pilot-Master-Manifest.json'

if (Test-Path -LiteralPath $fullOutput -PathType Leaf) {
    throw 'OutputDirectory exists as a file.'
}
$existingManifest = Test-ExistingBundle -ManifestPath $masterManifestPath -BundleRoot $fullOutput
if ($existingManifest) {
    $rootStorePath = "Cert:\CurrentUser\My\$($existingManifest.Root.ThumbprintSha1)"
    [pscustomobject]@{
        Status = 'PILOT_PKI_BUNDLE_ALREADY_READY'
        PilotOnly = $true
        OutputDirectory = $fullOutput
        ServerHandoffDirectory = $serverDirectory
        WorkstationTrustDirectory = $trustDirectory
        RootPrivateBackupDirectory = $offlineDirectory
        RootPrivateKeyStillInCurrentUserStore = [bool](Test-Path -LiteralPath $rootStorePath)
        RootSha256 = $existingManifest.Root.CertificateSha256
        LeafSha256 = $existingManifest.Leaf.CertificateSha256
        LeafNotAfter = $existingManifest.Leaf.NotAfter
    }
    return
}

if (Test-Path -LiteralPath $fullOutput -PathType Container) {
    $existingItems = @(Get-ChildItem -LiteralPath $fullOutput -Force)
    if ($existingItems.Count -gt 0) {
        throw 'OutputDirectory is non-empty but has no valid master manifest. Refusing to overwrite or guess at a partial private-key bundle.'
    }
}

$action = "Create a PILOT-ONLY root CA and $requiredServerName leaf, export encrypted PFX files, and remove working keys from the CurrentUser store"
if (-not $PSCmdlet.ShouldProcess($fullOutput, $action)) {
    [pscustomobject]@{
        Status = if ($WhatIfPreference) { 'WHATIF_READY_PILOT_PKI_BUNDLE' } else { 'PILOT_PKI_CREATION_CANCELLED' }
        PilotOnly = $true
        AdminWorkstation = $currentComputer
        OutputDirectory = $fullOutput
        RootPrivateBackupDirectory = $offlineDirectory
        ServerHandoffDirectory = $serverDirectory
        WorkstationTrustDirectory = $trustDirectory
        RequiredDnsNames = $requiredDnsNames
        Warning = 'No certificate, private key, directory, or file was created.'
    }
    return
}

Write-Warning 'PILOT ONLY: this lightweight root has no managed CA database, CDP, CRL, or OCSP service.'
Write-Warning 'Never copy OFFLINE-ROOT-PRIVATE-DO-NOT-COPY or its parent bundle to SON-IIS2, another workstation, Git, chat, email, or a file share.'
$rootPassword = Read-ConfirmedPfxPassword -Purpose 'Enter a unique password for the OFFLINE ROOT recovery PFX'
$leafPassword = Read-ConfirmedPfxPassword -Purpose 'Enter a different password for the SON-IIS2 leaf transport PFX'

$rootCertificate = $null
$leafCertificate = $null
$outputCreatedByScript = -not (Test-Path -LiteralPath $fullOutput)
$bundleValidated = $false
try {
    foreach ($directory in @($fullOutput, $offlineDirectory, $serverDirectory, $trustDirectory)) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            New-Item -ItemType Directory -Path $directory | Out-Null
        }
    }
    Set-PrivateDirectoryAcl -Path $fullOutput

    $rootBasicConstraints = New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($true, $true, 0, $true)
    $rootKeyUsageFlags = [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign
    $rootKeyUsage = New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension($rootKeyUsageFlags, $true)
    $rootParameters = @{
        Type = 'Custom'
        Subject = "CN=Son-Aero Hub Pilot Root CA $((Get-Date).Year)"
        FriendlyName = 'Son-Aero Hub PILOT Root CA - PRIVATE KEY MUST REMAIN OFFLINE'
        Provider = 'Microsoft Software Key Storage Provider'
        KeyAlgorithm = 'RSA'
        KeyLength = 4096
        HashAlgorithm = 'SHA256'
        KeyExportPolicy = 'ExportableEncrypted'
        KeyUsage = 'None'
        KeyUsageProperty = 'Sign'
        Extension = @($rootBasicConstraints, $rootKeyUsage)
        CertStoreLocation = 'Cert:\CurrentUser\My'
        NotAfter = (Get-Date).AddYears($RootValidityYears)
    }
    $rootCertificate = New-SelfSignedCertificate @rootParameters

    $serverAuthenticationOids = New-Object Security.Cryptography.OidCollection
    $null = $serverAuthenticationOids.Add((New-Object Security.Cryptography.Oid('1.3.6.1.5.5.7.3.1', 'Server Authentication')))
    $leafBasicConstraints = New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($false, $false, 0, $true)
    $leafKeyUsageFlags = [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
    $leafKeyUsage = New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension($leafKeyUsageFlags, $true)
    $leafEku = New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($serverAuthenticationOids, $false)
    $leafParameters = @{
        Type = 'Custom'
        Subject = 'CN=SON-IIS2'
        DnsName = $requiredDnsNames
        FriendlyName = 'SON-IIS2 Son-Aero Hub PILOT IIS certificate'
        Signer = $rootCertificate
        Provider = 'Microsoft Software Key Storage Provider'
        KeyAlgorithm = 'RSA'
        KeyLength = 3072
        HashAlgorithm = 'SHA256'
        KeyExportPolicy = 'ExportableEncrypted'
        KeyUsage = 'None'
        KeyUsageProperty = 'All'
        Extension = @($leafBasicConstraints, $leafKeyUsage, $leafEku)
        CertStoreLocation = 'Cert:\CurrentUser\My'
        NotAfter = (Get-Date).AddDays($LeafValidityDays)
    }
    $leafCertificate = New-SelfSignedCertificate @leafParameters

    if (-not $rootCertificate.HasPrivateKey -or -not $leafCertificate.HasPrivateKey) {
        throw 'Certificate creation did not produce both required private keys.'
    }
    if ($leafCertificate.Subject -eq $leafCertificate.Issuer -or $leafCertificate.Issuer -ne $rootCertificate.Subject) {
        throw 'The leaf was not issued by the new pilot root.'
    }
    $generatedEkuExtensions = @($leafCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($generatedEkuExtensions.Count -ne 1) { throw 'Generated leaf must contain exactly one Enhanced Key Usage extension.' }
    $generatedEku = New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
        $generatedEkuExtensions[0], $generatedEkuExtensions[0].Critical)
    $generatedEkuOids = @($generatedEku.EnhancedKeyUsages | ForEach-Object { [string]$_.Value })
    if ($generatedEkuOids -notcontains '1.3.6.1.5.5.7.3.1') { throw 'Generated leaf is missing the Server Authentication EKU.' }
    $actualDnsNames = @($leafCertificate.DnsNameList | ForEach-Object { $_.Punycode })
    foreach ($requiredName in $requiredDnsNames) {
        if ($actualDnsNames -inotcontains $requiredName) {
            throw "Generated leaf SAN validation failed for $requiredName."
        }
    }

    Export-PfxCertificate -Cert $rootCertificate -FilePath $rootPfxPath -Password $rootPassword `
        -CryptoAlgorithmOption AES256_SHA256 -ChainOption EndEntityCertOnly -NoProperties -NoClobber | Out-Null
    Export-Certificate -Cert $rootCertificate -FilePath $serverRootCerPath -Type CERT -NoClobber | Out-Null
    Export-Certificate -Cert $rootCertificate -FilePath $trustRootCerPath -Type CERT -NoClobber | Out-Null
    Export-PfxCertificate -Cert $leafCertificate -FilePath $serverLeafPfxPath -Password $leafPassword `
        -CryptoAlgorithmOption AES256_SHA256 -ChainOption EndEntityCertOnly -NoProperties -NoClobber | Out-Null
    Export-Certificate -Cert $leafCertificate -FilePath $serverLeafCerPath -Type CERT -NoClobber | Out-Null

    $rootPfxCertificates = Get-PfxCertificates -Path $rootPfxPath -Password $rootPassword
    $leafPfxCertificates = Get-PfxCertificates -Path $serverLeafPfxPath -Password $leafPassword
    if (@($rootPfxCertificates | Where-Object Thumbprint -EQ $rootCertificate.Thumbprint).Count -ne 1) {
        throw 'The encrypted root recovery PFX could not be validated against the generated root.'
    }
    if (@($leafPfxCertificates | Where-Object Thumbprint -EQ $leafCertificate.Thumbprint).Count -ne 1) {
        throw 'The encrypted leaf transport PFX could not be validated against the generated leaf.'
    }

    $rootCertificateSha256 = Get-Sha256 $serverRootCerPath
    $leafCertificateSha256 = Get-Sha256 $serverLeafCerPath
    $rootPfxSha256 = Get-Sha256 $rootPfxPath
    $leafPfxSha256 = Get-Sha256 $serverLeafPfxPath
    $createdAt = [DateTimeOffset]::UtcNow.ToString('o')

    $rootRecord = [ordered]@{
        Subject = $rootCertificate.Subject
        Issuer = $rootCertificate.Issuer
        ThumbprintSha1 = Get-NormalizedThumbprint $rootCertificate.Thumbprint
        CertificateSha256 = $rootCertificateSha256
        SerialNumber = $rootCertificate.SerialNumber
        NotBefore = $rootCertificate.NotBefore.ToUniversalTime().ToString('o')
        NotAfter = $rootCertificate.NotAfter.ToUniversalTime().ToString('o')
        PublicKey = 'RSA-4096'
        Signature = $rootCertificate.SignatureAlgorithm.FriendlyName
        BasicConstraints = 'critical CA=true pathLength=0'
        KeyUsage = @('Certificate Signing', 'CRL Signing')
    }
    $leafRecord = [ordered]@{
        Subject = $leafCertificate.Subject
        Issuer = $leafCertificate.Issuer
        ThumbprintSha1 = Get-NormalizedThumbprint $leafCertificate.Thumbprint
        CertificateSha256 = $leafCertificateSha256
        SerialNumber = $leafCertificate.SerialNumber
        NotBefore = $leafCertificate.NotBefore.ToUniversalTime().ToString('o')
        NotAfter = $leafCertificate.NotAfter.ToUniversalTime().ToString('o')
        PublicKey = 'RSA-3072'
        Signature = $leafCertificate.SignatureAlgorithm.FriendlyName
        DnsNames = $requiredDnsNames
        EnhancedKeyUsage = @('Server Authentication (1.3.6.1.5.5.7.3.1)')
        KeyUsage = @('Digital Signature', 'Key Encipherment')
    }

    $serverManifest = [ordered]@{
        SchemaVersion = $schemaVersion
        BundleType = $bundleType
        HandoffType = 'ServerPilotOnly'
        PilotOnly = $true
        CreatedAtUtc = $createdAt
        RequiredServer = $requiredServerName
        Root = $rootRecord
        Leaf = $leafRecord
        Files = [ordered]@{
            RootPublicCer = [ordered]@{ Name = [IO.Path]::GetFileName($serverRootCerPath); Sha256 = $rootCertificateSha256 }
            LeafPfx = [ordered]@{ Name = [IO.Path]::GetFileName($serverLeafPfxPath); Sha256 = $leafPfxSha256 }
            LeafPublicCer = [ordered]@{ Name = [IO.Path]::GetFileName($serverLeafCerPath); Sha256 = $leafCertificateSha256 }
        }
        Warning = 'PILOT ONLY. This directory intentionally contains no root private key. Do not expand trust beyond named pilot machines.'
    }
    $serverManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $serverManifestPath -Encoding UTF8

    $trustManifest = [ordered]@{
        SchemaVersion = $schemaVersion
        BundleType = $bundleType
        HandoffType = 'WorkstationPilotTrustOnly'
        PilotOnly = $true
        CreatedAtUtc = $createdAt
        Root = $rootRecord
        Files = [ordered]@{
            RootPublicCer = [ordered]@{ Name = [IO.Path]::GetFileName($trustRootCerPath); Sha256 = $rootCertificateSha256 }
        }
        Warning = 'PILOT ONLY. Trust only on explicitly named test workstations and remove it by exact thumbprint after the pilot.'
    }
    $trustManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $trustManifestPath -Encoding UTF8

    $rootRecoveryManifest = [ordered]@{
        SchemaVersion = $schemaVersion
        BundleType = $bundleType
        HandoffType = 'OfflineRootRecoveryOnly'
        PilotOnly = $true
        CreatedAtUtc = $createdAt
        AdminWorkstation = $currentComputer
        Root = $rootRecord
        Files = [ordered]@{
            RootPrivatePfx = [ordered]@{ Name = [IO.Path]::GetFileName($rootPfxPath); Sha256 = $rootPfxSha256 }
        }
        Warning = 'PRIVATE ROOT KEY. Keep powered off/offline under separate custody. Never copy this directory to a server, workstation, source repository, chat, email, or file share.'
    }
    $rootRecoveryManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $rootRecoveryManifestPath -Encoding UTF8

    $files = @(
        $rootPfxPath,
        $rootRecoveryManifestPath,
        $serverRootCerPath,
        $serverLeafPfxPath,
        $serverLeafCerPath,
        $serverManifestPath,
        $trustRootCerPath,
        $trustManifestPath
    ) | ForEach-Object {
        [ordered]@{
            RelativePath = $_.Substring($fullOutput.Length + 1)
            Sha256 = Get-Sha256 $_
        }
    }
    $masterManifest = [ordered]@{
        SchemaVersion = $schemaVersion
        BundleType = $bundleType
        PilotOnly = $true
        CreatedAtUtc = $createdAt
        AdminWorkstation = $currentComputer
        RequiredServer = $requiredServerName
        Root = $rootRecord
        Leaf = $leafRecord
        Files = $files
        Warning = 'PILOT ONLY. Separate offline root custody from the two public/leaf handoff directories. This is not managed production PKI.'
    }
    $masterManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $masterManifestPath -Encoding UTF8

    $null = Test-ExistingBundle -ManifestPath $masterManifestPath -BundleRoot $fullOutput
    $bundleValidated = $true

    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$(Get-NormalizedThumbprint $leafCertificate.Thumbprint)" -Force
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$(Get-NormalizedThumbprint $rootCertificate.Thumbprint)" -Force
    if (Test-Path -LiteralPath "Cert:\CurrentUser\My\$(Get-NormalizedThumbprint $rootCertificate.Thumbprint)") {
        throw 'The encrypted bundle is valid, but the root private key could not be removed from the CurrentUser certificate store. The bundle was preserved; remove that exact thumbprint before proceeding.'
    }

    [pscustomobject]@{
        Status = 'PILOT_PKI_BUNDLE_CREATED'
        PilotOnly = $true
        AdminWorkstation = $currentComputer
        RootPrivateBackupDirectory = $offlineDirectory
        ServerHandoffDirectory = $serverDirectory
        WorkstationTrustDirectory = $trustDirectory
        RootThumbprintSha1 = $rootRecord.ThumbprintSha1
        RootSha256 = $rootRecord.CertificateSha256
        LeafThumbprintSha1 = $leafRecord.ThumbprintSha1
        LeafSha256 = $leafRecord.CertificateSha256
        LeafNotAfter = $leafRecord.NotAfter
        WorkingKeysRemovedFromCurrentUserStore = $true
        NextStep = 'Move the OFFLINE root directory to two encrypted offline backups. Copy only SERVER-PILOT-HANDOFF to SON-IIS2.'
    }
}
catch {
    foreach ($certificate in @($leafCertificate, $rootCertificate)) {
        if ($certificate) {
            $storePath = "Cert:\CurrentUser\My\$(Get-NormalizedThumbprint $certificate.Thumbprint)"
            if (Test-Path -LiteralPath $storePath) {
                Remove-Item -LiteralPath $storePath -Force -ErrorAction SilentlyContinue
            }
        }
    }
    if (-not $bundleValidated -and $outputCreatedByScript -and
        (Test-Path -LiteralPath $fullOutput -PathType Container)) {
        Remove-Item -LiteralPath $fullOutput -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw
}
