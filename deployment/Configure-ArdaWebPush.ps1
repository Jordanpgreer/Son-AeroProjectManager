<#
    Transactionally enables the Portal Web Push broker and its Quality/Project Tracker producers.
    Producer secrets are generated in memory and written only to IIS applicationHost.config.
    Project Tracker legacy delivery is disabled in the same IIS commit to prevent duplicate pushes.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('SON-IIS2')]
    [string]$ExpectedComputerName = 'SON-IIS2',
    [string]$PortalSiteName = 'SonAeroPortal',
    [string]$ProjectTrackerSiteName = 'ProjectTracker',
    [string]$QualitySiteName = 'QualityAssurance',
    [ValidatePattern('^https://[A-Za-z0-9.-]+(?::[0-9]{1,5})?$')]
    [string]$PortalBaseUrl = 'https://hub.son4l.local',
    [string]$VapidPublicKey,
    [Parameter(Mandatory = $true)]
    [Security.SecureString]$VapidPrivateKey,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(mailto:|https:)')]
    [string]$VapidSubject,
    [ValidateRange(15, 300)]
    [int]$HealthTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$managedBySite = @{
    $PortalSiteName = @(
        'WebPush__Enabled', 'WebPush__PublicKey', 'WebPush__PrivateKey', 'WebPush__Subject',
        'WebPush__ProducerKeys__quality-assurance', 'WebPush__ProducerKeys__project-tracker'
    )
    $QualitySiteName = @('PortalPush__Enabled', 'PortalPush__BaseUrl', 'PortalPush__ProducerKey')
    $ProjectTrackerSiteName = @(
        'PortalPush__Enabled', 'PortalPush__BaseUrl', 'PortalPush__ProducerKey', 'WebPush__Enabled'
    )
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertFrom-Base64Url {
    param([string]$Value, [string]$Label)
    $normalized = $Value.Trim().Replace('-', '+').Replace('_', '/')
    switch ($normalized.Length % 4) {
        0 { }
        2 { $normalized += '==' }
        3 { $normalized += '=' }
        default { throw "$Label is not valid unpadded Base64URL." }
    }
    try { return [Convert]::FromBase64String($normalized) }
    catch { throw "$Label is not valid unpadded Base64URL." }
}

function New-ProducerKey {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
        return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        $rng.Dispose()
    }
}

function Assert-VapidP256KeyPair {
    param([byte[]]$PublicKeyBytes, [byte[]]$PrivateKeyBytes)
    $x = New-Object byte[] 32
    $y = New-Object byte[] 32
    [Array]::Copy($PublicKeyBytes, 1, $x, 0, 32)
    [Array]::Copy($PublicKeyBytes, 33, $y, 0, 32)
    $point = New-Object Security.Cryptography.ECPoint
    $point.X = $x
    $point.Y = $y
    $parameters = New-Object Security.Cryptography.ECParameters
    $parameters.Curve = [Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256')
    $parameters.Q = $point
    $parameters.D = $PrivateKeyBytes
    $signer = New-Object Security.Cryptography.ECDsaCng
    try {
        try { $signer.ImportParameters($parameters) }
        catch { throw 'The VAPID public and private keys are not a valid matching P-256 pair.' }
    }
    finally {
        $signer.Dispose()
        [Array]::Clear($x, 0, $x.Length)
        [Array]::Clear($y, 0, $y.Length)
    }
}

function Get-EnvironmentSnapshot {
    param([object]$Collection)
    $snapshot = @{}
    foreach ($entry in $Collection) {
        $name = [string]$entry.GetAttributeValue('name')
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $snapshot[$name] = [string]$entry.GetAttributeValue('value')
        }
    }
    return $snapshot
}

function Set-EnvironmentValue {
    param([object]$Collection, [string]$Name, [AllowEmptyString()][string]$Value)
    $entry = $Collection | Where-Object {
        [string]$_.GetAttributeValue('name') -ceq $Name
    } | Select-Object -First 1
    if ($null -eq $entry) {
        $entry = $Collection.CreateElement('environmentVariable')
        $entry.SetAttributeValue('name', $Name)
        $entry.SetAttributeValue('value', $Value)
        $Collection.Add($entry)
    }
    else { $entry.SetAttributeValue('value', $Value) }
}

function Restore-EnvironmentValues {
    param([object]$Collection, [hashtable]$Snapshot, [string[]]$Names)
    foreach ($name in $Names) {
        $entry = $Collection | Where-Object {
            [string]$_.GetAttributeValue('name') -ceq $name
        } | Select-Object -First 1
        if ($Snapshot.ContainsKey($name)) {
            Set-EnvironmentValue -Collection $Collection -Name $name -Value $Snapshot[$name]
        }
        elseif ($null -ne $entry) { $Collection.Remove($entry) }
    }
}

function Wait-Healthy {
    param([string]$Uri, [int]$TimeoutSeconds, [string]$ExpectedPublicKey = '')
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = 'No response was received.'
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $Uri -TimeoutSec 10
            if ($response.StatusCode -eq 200) {
                if ([string]::IsNullOrWhiteSpace($ExpectedPublicKey)) { return }
                $payload = $response.Content | ConvertFrom-Json
                if ([bool]$payload.enabled -and [string]$payload.publicKey -ceq $ExpectedPublicKey) { return }
            }
            $lastError = "HTTP $($response.StatusCode) or unexpected payload"
        }
        catch { $lastError = $_.Exception.Message }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Health verification failed for $Uri. Last error: $lastError"
}

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is '$env:COMPUTERNAME'."
}
if (-not $WhatIfPreference -and -not (Test-IsAdministrator)) {
    throw 'Run this script from an elevated Windows PowerShell session.'
}
$publicBytes = ConvertFrom-Base64Url -Value $VapidPublicKey -Label 'VapidPublicKey'
if ($publicBytes.Length -ne 65 -or $publicBytes[0] -ne 4) {
    throw 'VapidPublicKey must be an uncompressed P-256 public key.'
}

$privatePointer = [IntPtr]::Zero
$privatePlaintext = $null
$qualityProducerKey = $null
$projectTrackerProducerKey = $null
$manager = $null
$snapshots = @{}
try {
    $privatePointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($VapidPrivateKey)
    $privatePlaintext = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($privatePointer)
    $privateBytes = ConvertFrom-Base64Url -Value $privatePlaintext -Label 'VapidPrivateKey'
    if ($privateBytes.Length -ne 32) { throw 'VapidPrivateKey must be a 32-byte P-256 private scalar.' }
    Assert-VapidP256KeyPair -PublicKeyBytes $publicBytes -PrivateKeyBytes $privateBytes
    [Array]::Clear($privateBytes, 0, $privateBytes.Length)
    $qualityProducerKey = New-ProducerKey
    $projectTrackerProducerKey = New-ProducerKey

    if (-not $PSCmdlet.ShouldProcess(
        "$ExpectedComputerName/$PortalSiteName+$QualitySiteName+$ProjectTrackerSiteName",
        'Enable central Arda Web Push and atomically migrate both producers')) {
        Write-Output 'WHATIF_READY_ARDA_WEB_PUSH: no IIS values or pools were changed.'
        return
    }

    $assembly = Join-Path $env:WINDIR 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw 'IIS administration assembly is unavailable.' }
    if (-not ('Microsoft.Web.Administration.ServerManager' -as [type])) { Add-Type -Path $assembly }
    Import-Module WebAdministration -ErrorAction Stop
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    foreach ($siteName in $managedBySite.Keys) {
        if ($null -eq $manager.Sites[$siteName] -or $null -eq $manager.ApplicationPools[$siteName]) {
            throw "Required IIS site/application pool '$siteName' does not exist."
        }
    }

    $collections = @{}
    $configuration = $manager.GetApplicationHostConfiguration()
    foreach ($siteName in $managedBySite.Keys) {
        $collection = $configuration.GetSection('system.webServer/aspNetCore', $siteName).GetCollection('environmentVariables')
        $collections[$siteName] = $collection
        $snapshots[$siteName] = Get-EnvironmentSnapshot -Collection $collection
    }

    Set-EnvironmentValue $collections[$PortalSiteName] 'WebPush__Enabled' 'true'
    Set-EnvironmentValue $collections[$PortalSiteName] 'WebPush__PublicKey' $VapidPublicKey.Trim()
    Set-EnvironmentValue $collections[$PortalSiteName] 'WebPush__PrivateKey' $privatePlaintext
    Set-EnvironmentValue $collections[$PortalSiteName] 'WebPush__Subject' $VapidSubject.Trim()
    Set-EnvironmentValue $collections[$PortalSiteName] 'WebPush__ProducerKeys__quality-assurance' $qualityProducerKey
    Set-EnvironmentValue $collections[$PortalSiteName] 'WebPush__ProducerKeys__project-tracker' $projectTrackerProducerKey
    foreach ($producer in @(
        @{ Site = $QualitySiteName; Key = $qualityProducerKey },
        @{ Site = $ProjectTrackerSiteName; Key = $projectTrackerProducerKey }
    )) {
        Set-EnvironmentValue $collections[$producer.Site] 'PortalPush__Enabled' 'true'
        Set-EnvironmentValue $collections[$producer.Site] 'PortalPush__BaseUrl' $PortalBaseUrl.TrimEnd('/')
        Set-EnvironmentValue $collections[$producer.Site] 'PortalPush__ProducerKey' $producer.Key
    }
    Set-EnvironmentValue $collections[$ProjectTrackerSiteName] 'WebPush__Enabled' 'false'
    $manager.CommitChanges()
    $manager.Dispose()
    $manager = $null

    try {
        foreach ($pool in @($PortalSiteName, $QualitySiteName, $ProjectTrackerSiteName)) {
            Restart-WebAppPool -Name $pool
        }
        Wait-Healthy "$($PortalBaseUrl.TrimEnd('/'))/api/push/public-key" $HealthTimeoutSeconds $VapidPublicKey.Trim()
        Wait-Healthy 'https://quality.hub.son4l.local/api/health' $HealthTimeoutSeconds
        Wait-Healthy 'https://projects.hub.son4l.local/api/health' $HealthTimeoutSeconds
    }
    catch {
        $failure = $_.Exception.Message
        $rollback = New-Object Microsoft.Web.Administration.ServerManager
        try {
            $rollbackConfig = $rollback.GetApplicationHostConfiguration()
            foreach ($siteName in $managedBySite.Keys) {
                $collection = $rollbackConfig.GetSection('system.webServer/aspNetCore', $siteName).GetCollection('environmentVariables')
                Restore-EnvironmentValues $collection $snapshots[$siteName] $managedBySite[$siteName]
            }
            $rollback.CommitChanges()
        }
        finally { $rollback.Dispose() }
        foreach ($pool in @($PortalSiteName, $QualitySiteName, $ProjectTrackerSiteName)) {
            Restart-WebAppPool -Name $pool
        }
        throw "Arda Web Push verification failed; all three IIS configurations were restored. $failure"
    }
    Write-Output 'ARDA_WEB_PUSH_CONFIGURED_AND_HEALTHY'
}
finally {
    if ($null -ne $manager) { $manager.Dispose() }
    $privatePlaintext = $null
    $qualityProducerKey = $null
    $projectTrackerProducerKey = $null
    if ($privatePointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($privatePointer)
    }
    [Array]::Clear($publicBytes, 0, $publicBytes.Length)
}
