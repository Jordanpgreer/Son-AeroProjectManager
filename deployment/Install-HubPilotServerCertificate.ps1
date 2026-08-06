<#
    PILOT ONLY. Imports the public pilot root and the SON-IIS2 leaf PFX on SON-IIS2.

    Point BundleDirectory only at SERVER-PILOT-HANDOFF. The script refuses any handoff directory
    containing a second PFX, preventing accidental transfer of the offline root private key.
    It never creates or changes IIS bindings.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$BundleDirectory,

    [ValidateSet('Install', 'RemoveLeaf', 'RemoveAll')]
    [string]$Operation = 'Install',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,62}$')]
    [string]$ExpectedComputerName = 'SON-IIS2'
)

$ErrorActionPreference = 'Stop'
$requiredServerName = 'SON-IIS2'
$requiredDnsNames = @('SON-IIS2', 'SON-IIS2.SON4L.LOCAL')
$bundleType = 'SonAeroHubPilotPki'
$manifestFileName = 'SonAero-Hub-Pilot-Server-Manifest.json'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-NormalizedThumbprint {
    param([Parameter(Mandatory)][string]$Thumbprint)
    return ($Thumbprint -replace '\s', '').ToUpperInvariant()
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    $hasher = [Security.Cryptography.SHA256]::Create(); try { return ([BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($Path)))).Replace('-', '') } finally { $hasher.Dispose() }
}

function Get-CertificateEkuOidValues {
    param([Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $extensions = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($extensions.Count -ne 1) {
        throw "Certificate must contain exactly one Enhanced Key Usage extension; found $($extensions.Count)."
    }
    try {
        $parsed = New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
            $extensions[0], $extensions[0].Critical)
        $values = @($parsed.EnhancedKeyUsages | ForEach-Object { [string]$_.Value } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    catch { throw "Certificate Enhanced Key Usage extension could not be parsed: $($_.Exception.Message)" }
    if ($values.Count -eq 0) { throw 'Certificate Enhanced Key Usage extension contains no usable OIDs.' }
    return $values
}

function Resolve-HandoffFile {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Name
    )
    if ([string]::IsNullOrWhiteSpace($Name) -or [IO.Path]::IsPathRooted($Name) -or
        $Name -ne [IO.Path]::GetFileName($Name) -or $Name.Contains('..')) {
        throw "The pilot manifest contains an unsafe file name: '$Name'."
    }
    $path = [IO.Path]::GetFullPath((Join-Path $Directory $Name))
    if (-not $path.StartsWith($Directory + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The required pilot handoff file is missing: $Name"
    }
    return $path
}

function Assert-FileHash {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )
    if ($ExpectedSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "The manifest SHA-256 value for $([IO.Path]::GetFileName($Path)) is invalid."
    }
    $actual = Get-Sha256 $Path
    if ($actual -ne $ExpectedSha256.ToUpperInvariant()) {
        throw "SHA-256 mismatch for $([IO.Path]::GetFileName($Path)). No certificate-store changes were made."
    }
}

function Assert-CertificateShape {
    param(
        [Parameter(Mandatory)]$Certificate,
        [Parameter(Mandatory)][bool]$MustBeCa
    )
    $basicExtension = $Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' } | Select-Object -First 1
    if (-not $basicExtension) {
        throw "Certificate $($Certificate.Subject) has no Basic Constraints extension."
    }
    $basic = New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($basicExtension, $basicExtension.Critical)
    if ($basic.CertificateAuthority -ne $MustBeCa) {
        throw "Certificate $($Certificate.Subject) has the wrong CA/end-entity constraint."
    }
}

function Assert-LeafCertificate {
    param([Parameter(Mandatory)]$Certificate)

    Assert-CertificateShape -Certificate $Certificate -MustBeCa $false
    if ($Certificate.Subject -ine 'CN=SON-IIS2' -or $Certificate.Subject -eq $Certificate.Issuer) {
        throw 'The leaf subject/issuer is not valid for the SON-IIS2 pilot.'
    }
    if ($Certificate.NotBefore -gt (Get-Date) -or $Certificate.NotAfter -lt (Get-Date).AddDays(7)) {
        throw 'The pilot leaf is not currently valid for at least seven more days.'
    }
    $serverAuthenticationOid = '1.3.6.1.5.5.7.3.1'
    $eku = @(Get-CertificateEkuOidValues -Certificate $Certificate)
    if ($eku -notcontains $serverAuthenticationOid) {
        throw 'The pilot leaf does not include the Server Authentication EKU.'
    }
    $keyUsageExtensions = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.15' })
    if ($keyUsageExtensions.Count -gt 1) { throw 'The pilot leaf contains duplicate Key Usage extensions.' }
    if ($keyUsageExtensions.Count -eq 1) {
        try {
            $keyUsage = New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                $keyUsageExtensions[0], $keyUsageExtensions[0].Critical)
        }
        catch { throw "The pilot leaf Key Usage extension could not be parsed: $($_.Exception.Message)" }
        $digitalSignature = [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature
        if (($keyUsage.KeyUsages -band $digitalSignature) -eq 0) {
            throw 'The pilot leaf Key Usage extension does not allow Digital Signature.'
        }
    }
    if ($Certificate.PSObject.Properties.Name -notcontains 'DnsNameList') {
        throw 'DnsNameList is unavailable; refusing to guess at localized SAN extension text.'
    }
    $actualDnsNames = @($Certificate.DnsNameList | ForEach-Object {
        if ($_.Punycode) { $_.Punycode } elseif ($_.Unicode) { $_.Unicode }
    })
    foreach ($requiredName in $requiredDnsNames) {
        if ($actualDnsNames -inotcontains $requiredName) {
            throw "The pilot leaf SAN is missing $requiredName."
        }
    }
}

function Get-BindingCertificateThumbprints {
    # Module import is read-only setup; do not let the caller's -WhatIf suppress type/drive loading.
    $savedWhatIfPreference = $WhatIfPreference
    try {
        $WhatIfPreference = $false
        Import-Module WebAdministration
    }
    finally {
        $WhatIfPreference = $savedWhatIfPreference
    }
    $thumbprints = [System.Collections.Generic.List[string]]::new()
    foreach ($site in @(Get-Website)) {
        foreach ($binding in @(Get-WebBinding -Name $site.Name)) {
            if (-not $binding.certificateHash) { continue }
            if ($binding.certificateHash -is [byte[]]) {
                $hash = ($binding.certificateHash | ForEach-Object { $_.ToString('X2') }) -join ''
            }
            else {
                $hash = ([string]$binding.certificateHash -replace '\s', '').ToUpperInvariant()
            }
            if (-not [string]::IsNullOrWhiteSpace($hash)) {
                $thumbprints.Add($hash)
            }
        }
    }
    return $thumbprints.ToArray()
}

$currentComputer = [string]$env:COMPUTERNAME
if ([string]::IsNullOrWhiteSpace($currentComputer) -or $currentComputer -ine $ExpectedComputerName -or
    $ExpectedComputerName -ine $requiredServerName) {
    throw "This PILOT server certificate operation is restricted to $requiredServerName; the current computer is '$currentComputer'."
}
if (-not $WhatIfPreference -and -not (Test-IsAdministrator)) {
    throw 'Run this script from an elevated Windows PowerShell session on SON-IIS2.'
}

$fullBundleDirectory = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($BundleDirectory)).TrimEnd('\')
if (-not (Test-Path -LiteralPath $fullBundleDirectory -PathType Container)) {
    throw "BundleDirectory was not found: $fullBundleDirectory"
}
$manifestPath = Join-Path $fullBundleDirectory $manifestFileName
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Use only the generated SERVER-PILOT-HANDOFF directory; $manifestFileName was not found."
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.SchemaVersion -ne 1 -or $manifest.BundleType -ne $bundleType -or
    $manifest.HandoffType -ne 'ServerPilotOnly' -or $manifest.PilotOnly -ne $true -or
    $manifest.RequiredServer -ine $requiredServerName) {
    throw 'The supplied manifest is not the SON-IIS2 pilot server handoff manifest.'
}

$rootCerPath = Resolve-HandoffFile -Directory $fullBundleDirectory -Name ([string]$manifest.Files.RootPublicCer.Name)
$leafPfxPath = Resolve-HandoffFile -Directory $fullBundleDirectory -Name ([string]$manifest.Files.LeafPfx.Name)
$leafCerPath = Resolve-HandoffFile -Directory $fullBundleDirectory -Name ([string]$manifest.Files.LeafPublicCer.Name)
$unexpectedPfxFiles = @(Get-ChildItem -LiteralPath $fullBundleDirectory -Recurse -File -Filter '*.pfx' |
    Where-Object { $_.FullName -ine $leafPfxPath })
if ($unexpectedPfxFiles.Count -gt 0) {
    throw 'PILOT SECURITY STOP: the server handoff contains an unexpected PFX. The offline root private PFX must never be copied to SON-IIS2.'
}
Assert-FileHash -Path $rootCerPath -ExpectedSha256 ([string]$manifest.Files.RootPublicCer.Sha256)
Assert-FileHash -Path $leafPfxPath -ExpectedSha256 ([string]$manifest.Files.LeafPfx.Sha256)
Assert-FileHash -Path $leafCerPath -ExpectedSha256 ([string]$manifest.Files.LeafPublicCer.Sha256)

$rootPublicCertificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($rootCerPath)
$leafPublicCertificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($leafCerPath)
$rootThumbprint = Get-NormalizedThumbprint ([string]$manifest.Root.ThumbprintSha1)
$leafThumbprint = Get-NormalizedThumbprint ([string]$manifest.Leaf.ThumbprintSha1)
$pilotHttpsPreview = ".\Configure-HubHttpsPilot.ps1 -CertificateThumbprint '$leafThumbprint' -PilotRootThumbprint '$rootThumbprint' -PilotRemoteAddress '<PILOT_WORKSTATION_IP>' -WhatIf"
if ($rootThumbprint -notmatch '^[A-F0-9]{40}$' -or $leafThumbprint -notmatch '^[A-F0-9]{40}$' -or
    (Get-NormalizedThumbprint $rootPublicCertificate.Thumbprint) -ne $rootThumbprint -or
    (Get-NormalizedThumbprint $leafPublicCertificate.Thumbprint) -ne $leafThumbprint) {
    throw 'Certificate thumbprints do not match the integrity-checked pilot manifest.'
}
Assert-CertificateShape -Certificate $rootPublicCertificate -MustBeCa $true

$rootStorePath = "Cert:\LocalMachine\Root\$rootThumbprint"
$rootPersonalStorePath = "Cert:\LocalMachine\My\$rootThumbprint"
$leafStorePath = "Cert:\LocalMachine\My\$leafThumbprint"
$rootInstalled = Get-Item -LiteralPath $rootStorePath -ErrorAction SilentlyContinue
$rootPrivateCopy = Get-Item -LiteralPath $rootPersonalStorePath -ErrorAction SilentlyContinue
$leafInstalled = Get-Item -LiteralPath $leafStorePath -ErrorAction SilentlyContinue

if ($Operation -ne 'RemoveAll' -and
    (($rootInstalled -and $rootInstalled.HasPrivateKey) -or $rootPrivateCopy)) {
    throw 'PILOT SECURITY STOP: the pilot root private key appears to be installed on SON-IIS2. Use RemoveAll after confirming no dependent certificates, then rebuild from the public-root/leaf-only server handoff.'
}

Write-Warning 'PILOT ONLY: this installs a private pilot trust anchor locally on SON-IIS2. It does not configure IIS bindings or company-wide trust.'

if ($Operation -eq 'Install') {
    if ($rootInstalled -and $leafInstalled) {
        Assert-LeafCertificate -Certificate $leafInstalled
        if (-not $leafInstalled.HasPrivateKey) {
            throw 'The pilot leaf exists in LocalMachine\My but its private key is missing.'
        }
        [pscustomobject]@{
            Status = 'PILOT_SERVER_CERTIFICATES_ALREADY_INSTALLED'
            PilotOnly = $true
            ComputerName = $currentComputer
            RootThumbprintSha1 = $rootThumbprint
            LeafThumbprintSha1 = $leafThumbprint
            LeafNotAfter = $leafInstalled.NotAfter
            IisBindingsChanged = $false
            NextStep = $pilotHttpsPreview
        }
        return
    }

    if (-not $PSCmdlet.ShouldProcess('Cert:\LocalMachine\Root and Cert:\LocalMachine\My',
        'Import the public pilot root and the non-exportable SON-IIS2 pilot leaf private key')) {
        [pscustomobject]@{
            Status = if ($WhatIfPreference) { 'WHATIF_READY_PILOT_SERVER_CERTIFICATE_IMPORT' } else { 'PILOT_SERVER_CERTIFICATE_IMPORT_CANCELLED' }
            PilotOnly = $true
            ComputerName = $currentComputer
            RootThumbprintSha1 = $rootThumbprint
            LeafThumbprintSha1 = $leafThumbprint
            IisBindingsChanged = $false
        }
        return
    }

    $leafPassword = Read-Host 'Enter the SON-IIS2 leaf transport PFX password (it will not be displayed)' -AsSecureString
    $pfxData = Get-PfxData -FilePath $leafPfxPath -Password $leafPassword
    $pfxCertificates = @($pfxData.EndEntityCertificates) + @($pfxData.OtherCertificates)
    $pfxLeaf = @($pfxCertificates | Where-Object {
        (Get-NormalizedThumbprint $_.Thumbprint) -eq $leafThumbprint
    }) | Select-Object -First 1
    if (-not $pfxLeaf) {
        throw 'The leaf PFX did not contain the manifest leaf certificate. No certificate-store changes were made.'
    }
    Assert-LeafCertificate -Certificate $pfxLeaf
    if ($pfxLeaf.Issuer -ne $rootPublicCertificate.Subject) {
        throw 'The pilot leaf issuer does not match the pilot root subject.'
    }

    $rootAdded = $false
    try {
        if (-not $rootInstalled) {
            Import-Certificate -FilePath $rootCerPath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
            $rootAdded = $true
        }
        if (-not $leafInstalled) {
            # Deliberately omit -Exportable so the IIS private key cannot be re-exported from SON-IIS2.
            Import-PfxCertificate -FilePath $leafPfxPath -Password $leafPassword `
                -CertStoreLocation 'Cert:\LocalMachine\My' | Out-Null
        }
        $importedLeaf = Get-Item -LiteralPath $leafStorePath -ErrorAction Stop
        Assert-LeafCertificate -Certificate $importedLeaf
        if (-not $importedLeaf.HasPrivateKey) {
            throw 'The imported SON-IIS2 leaf does not have its private key.'
        }

        [pscustomobject]@{
            Status = 'PILOT_SERVER_CERTIFICATES_IMPORTED'
            PilotOnly = $true
            ComputerName = $currentComputer
            RootThumbprintSha1 = $rootThumbprint
            LeafThumbprintSha1 = $leafThumbprint
            LeafNotAfter = $importedLeaf.NotAfter
            LeafPrivateKeyExportableRequested = $false
            IisBindingsChanged = $false
            NextStep = "Run $pilotHttpsPreview. After pilot HTTPS is healthy, securely remove this server-local handoff directory containing the leaf PFX."
        }
    }
    catch {
        if (-not $leafInstalled -and (Test-Path -LiteralPath $leafStorePath)) {
            Remove-Item -LiteralPath $leafStorePath -Force -ErrorAction SilentlyContinue
        }
        if ($rootAdded -and (Test-Path -LiteralPath $rootStorePath)) {
            Remove-Item -LiteralPath $rootStorePath -Force -ErrorAction SilentlyContinue
        }
        throw
    }
    return
}

$boundThumbprints = @(Get-BindingCertificateThumbprints)
if ($boundThumbprints -contains $leafThumbprint) {
    throw 'The pilot leaf is still referenced by an IIS binding. Remove only the pilot HTTPS bindings before removing the certificate.'
}
if ($Operation -eq 'RemoveAll') {
    $otherIssuedCertificates = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object {
        (Get-NormalizedThumbprint $_.Thumbprint) -ne $leafThumbprint -and
        (Get-NormalizedThumbprint $_.Thumbprint) -ne $rootThumbprint -and
        $_.Issuer -eq $rootPublicCertificate.Subject -and $_.NotAfter -gt (Get-Date)
    })
    if ($otherIssuedCertificates.Count -gt 0) {
        throw 'The pilot root currently issues another unexpired LocalMachine certificate. RemoveAll refuses to break that certificate chain.'
    }
}

$removed = [System.Collections.Generic.List[string]]::new()
if ($leafInstalled -and $PSCmdlet.ShouldProcess($leafStorePath, 'Remove the unbound SON-IIS2 pilot leaf certificate and private key')) {
    Remove-Item -LiteralPath $leafStorePath -Force
    $removed.Add('Leaf')
}
if ($Operation -eq 'RemoveAll' -and $rootInstalled -and
    $PSCmdlet.ShouldProcess($rootStorePath, 'Remove the public pilot root trust anchor from SON-IIS2')) {
    Remove-Item -LiteralPath $rootStorePath -Force
    $removed.Add('Root')
}
if ($Operation -eq 'RemoveAll' -and $rootPrivateCopy -and
    $PSCmdlet.ShouldProcess($rootPersonalStorePath, 'Remove the prohibited pilot root private-key copy from SON-IIS2')) {
    Remove-Item -LiteralPath $rootPersonalStorePath -Force
    $removed.Add('RootPrivateCopy')
}

[pscustomobject]@{
    Status = if ($WhatIfPreference) { 'WHATIF_READY_PILOT_SERVER_CERTIFICATE_REMOVAL' } elseif ($removed.Count -eq 0) { 'PILOT_SERVER_CERTIFICATES_ALREADY_ABSENT' } else { 'PILOT_SERVER_CERTIFICATES_REMOVED' }
    PilotOnly = $true
    ComputerName = $currentComputer
    Operation = $Operation
    Removed = $removed.ToArray()
    IisBindingsChanged = $false
}
