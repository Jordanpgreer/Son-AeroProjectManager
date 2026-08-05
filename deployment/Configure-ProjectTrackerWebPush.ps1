<#
    Configures Project Tracker Web Push values in the IIS applicationHost.config environment.

    The VAPID private key is accepted only as a SecureString and is never written to the console,
    command history, Git-tracked settings, or an application log. IIS administrators can still
    read applicationHost.config, so its backups must be protected as secrets.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,62}$')]
    [string]$ExpectedComputerName = 'SON-IIS2',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$SiteName = 'ProjectTracker',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$AppPoolName = 'ProjectTracker',

    [ValidatePattern('^https?://[A-Za-z0-9.-]+(?::[0-9]{1,5})?/api/push/public-key$')]
    [string]$VerificationUri = 'http://SON-IIS2:5135/api/push/public-key',

    [string]$VapidPublicKey,

    [Security.SecureString]$VapidPrivateKey,

    [string]$VapidSubject,

    [switch]$GenerateKeys,

    [switch]$Disable,

    [ValidateRange(10, 300)]
    [int]$HealthTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertFrom-Base64Url {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

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

function ConvertTo-Base64Url {
    param([Parameter(Mandatory = $true)][byte[]]$Value)

    return [Convert]::ToBase64String($Value).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-EnvironmentVariableSnapshot {
    # Do not use a Microsoft.Web.Administration parameter type here. PowerShell parses function
    # signatures before the IIS assembly is loaded below, which would make this script fail in a
    # fresh Windows PowerShell process.
    param([object]$Collection)

    $snapshot = @{}
    foreach ($element in $Collection) {
        $name = [string]$element.GetAttributeValue('name')
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $snapshot[$name] = [string]$element.GetAttributeValue('value')
        }
    }
    return $snapshot
}

function Set-EnvironmentVariableValue {
    param(
        [object]$Collection,
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowEmptyString()][string]$Value
    )

    $existing = $Collection | Where-Object {
        [string]$_.GetAttributeValue('name') -ceq $Name
    } | Select-Object -First 1
    if ($null -eq $existing) {
        $existing = $Collection.CreateElement('environmentVariable')
        $existing.SetAttributeValue('name', $Name)
        $Collection.Add($existing)
    }
    $existing.SetAttributeValue('value', $Value)
}

function Remove-EnvironmentVariableValue {
    param(
        [object]$Collection,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $existing = $Collection | Where-Object {
        [string]$_.GetAttributeValue('name') -ceq $Name
    } | Select-Object -First 1
    if ($null -ne $existing) { $Collection.Remove($existing) }
}

function Restore-EnvironmentVariables {
    param(
        [object]$Collection,
        [hashtable]$Snapshot,
        [string[]]$Names
    )

    foreach ($name in $Names) {
        if ($Snapshot.ContainsKey($name)) {
            Set-EnvironmentVariableValue -Collection $Collection -Name $name -Value $Snapshot[$name]
        }
        else {
            Remove-EnvironmentVariableValue -Collection $Collection -Name $name
        }
    }
}

function Wait-ForConfiguration {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][bool]$ExpectedEnabled,
        [string]$ExpectedPublicKey,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = 'No response was received.'
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $Uri -TimeoutSec 10
            if ($response.StatusCode -eq 200) {
                $payload = $response.Content | ConvertFrom-Json
                $enabled = [bool]$payload.enabled
                $publicKey = [string]$payload.publicKey
                if ($enabled -eq $ExpectedEnabled -and
                    (-not $ExpectedEnabled -or $publicKey -ceq $ExpectedPublicKey)) {
                    return
                }
                $lastError = 'The endpoint responded, but its enabled/public-key state did not match the requested configuration.'
            }
            else {
                $lastError = "HTTP $($response.StatusCode)"
            }
        }
        catch { $lastError = $_.Exception.Message }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Project Tracker did not confirm the Web Push configuration at $Uri. Last error: $lastError"
}

if ($env:COMPUTERNAME -ine $ExpectedComputerName) {
    throw "This script is for $ExpectedComputerName; the current computer is '$env:COMPUTERNAME'."
}
if (-not $WhatIfPreference -and -not (Test-IsAdministrator)) {
    throw 'Run this script from an elevated Windows PowerShell session.'
}

$managedNames = @(
    'WebPush__Enabled',
    'WebPush__PublicKey',
    'WebPush__PrivateKey',
    'WebPush__Subject'
)

if ($Disable -and ($GenerateKeys -or $PSBoundParameters.ContainsKey('VapidPublicKey') -or
        $PSBoundParameters.ContainsKey('VapidPrivateKey') -or $PSBoundParameters.ContainsKey('VapidSubject'))) {
    throw 'Disable cannot be combined with GenerateKeys or supplied VAPID values.'
}
if ($GenerateKeys -and ($PSBoundParameters.ContainsKey('VapidPublicKey') -or
        $PSBoundParameters.ContainsKey('VapidPrivateKey'))) {
    throw 'GenerateKeys is mutually exclusive with VapidPublicKey and VapidPrivateKey.'
}

if (-not $Disable) {
    if (-not $GenerateKeys -and [string]::IsNullOrWhiteSpace($VapidPublicKey)) {
        throw 'VapidPublicKey is required when enabling Web Push.'
    }
    if (-not $GenerateKeys -and ($null -eq $VapidPrivateKey -or $VapidPrivateKey.Length -eq 0)) {
        throw 'VapidPrivateKey is required as a SecureString when enabling Web Push.'
    }
    if ([string]::IsNullOrWhiteSpace($VapidSubject)) {
        throw 'VapidSubject is required when enabling Web Push.'
    }

    if (-not $GenerateKeys) {
        $publicKeyBytes = ConvertFrom-Base64Url -Value $VapidPublicKey -Label 'VapidPublicKey'
        if ($publicKeyBytes.Length -ne 65 -or $publicKeyBytes[0] -ne 4) {
            throw 'VapidPublicKey must be an uncompressed P-256 public key (65 bytes beginning with 0x04).'
        }
    }
    $subjectUri = $null
    if (-not [Uri]::TryCreate($VapidSubject.Trim(), [UriKind]::Absolute, [ref]$subjectUri) -or
        $subjectUri.Scheme -notin @('mailto', 'https')) {
        throw 'VapidSubject must be an absolute mailto: or https: URI controlled by Son-Aero.'
    }
}

$verification = [Uri]$VerificationUri
if ($verification.Host -ine $ExpectedComputerName) {
    throw "VerificationUri must use the expected host $ExpectedComputerName."
}

$assemblyPath = Join-Path $env:WINDIR 'System32\inetsrv\Microsoft.Web.Administration.dll'
if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
    throw "The IIS administration assembly was not found: $assemblyPath"
}
if (-not ('Microsoft.Web.Administration.ServerManager' -as [type])) {
    Add-Type -Path $assemblyPath -ErrorAction Stop
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
finally { $WhatIfPreference = $priorWhatIfPreference }

$serverManager = New-Object Microsoft.Web.Administration.ServerManager
$snapshot = $null
$plainPrivateKey = $null
$privateKeyPointer = [IntPtr]::Zero
$generatedKey = $null
$generatedPrivateBytes = $null
$generatedPublicBytes = $null
$effectivePublicKey = $null
$publicKeyFingerprint = $null
try {
    if ($null -eq $serverManager.Sites[$SiteName]) {
        throw "Required IIS site '$SiteName' does not exist."
    }
    if ($null -eq $serverManager.ApplicationPools[$AppPoolName]) {
        throw "Required IIS application pool '$AppPoolName' does not exist."
    }

    $configuration = $serverManager.GetApplicationHostConfiguration()
    $section = $configuration.GetSection('system.webServer/aspNetCore', $SiteName)
    $environmentVariables = $section.GetCollection('environmentVariables')
    $snapshot = Get-EnvironmentVariableSnapshot -Collection $environmentVariables

    $action = if ($Disable) {
        'Disable Project Tracker Web Push and remove its IIS VAPID values'
    }
    else {
        'Enable Project Tracker Web Push with server-only IIS VAPID values'
    }
    if (-not $PSCmdlet.ShouldProcess("$ExpectedComputerName/$SiteName", $action)) {
        Write-Host 'WHATIF_READY: no IIS configuration or application pool was changed.'
        return
    }

    if ($Disable) {
        Set-EnvironmentVariableValue -Collection $environmentVariables -Name 'WebPush__Enabled' -Value 'false'
        foreach ($name in @('WebPush__PublicKey', 'WebPush__PrivateKey', 'WebPush__Subject')) {
            Remove-EnvironmentVariableValue -Collection $environmentVariables -Name $name
        }
    }
    else {
        if ($GenerateKeys) {
            # ECDsaCng is available on the supported Windows Server and uses the Windows CNG
            # provider. Export only long enough to convert the P-256 scalar and point into the
            # Web Push Base64URL representation, then clear the byte arrays in finally.
            $generatedKey = New-Object System.Security.Cryptography.ECDsaCng 256
            $parameters = $generatedKey.ExportParameters($true)
            $generatedPrivateBytes = $parameters.D
            $generatedPublicBytes = New-Object byte[] 65
            $generatedPublicBytes[0] = 4
            [Array]::Copy($parameters.Q.X, 0, $generatedPublicBytes, 1, 32)
            [Array]::Copy($parameters.Q.Y, 0, $generatedPublicBytes, 33, 32)
            $plainPrivateKey = ConvertTo-Base64Url -Value $generatedPrivateBytes
            $effectivePublicKey = ConvertTo-Base64Url -Value $generatedPublicBytes
        }
        else {
            $privateKeyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($VapidPrivateKey)
            $plainPrivateKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($privateKeyPointer)
            $privateKeyBytes = ConvertFrom-Base64Url -Value $plainPrivateKey -Label 'VapidPrivateKey'
            if ($privateKeyBytes.Length -ne 32) {
                throw 'VapidPrivateKey must be a 32-byte P-256 private key.'
            }
            $effectivePublicKey = $VapidPublicKey.Trim()
        }

        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $fingerprintBytes = $sha256.ComputeHash(
                (ConvertFrom-Base64Url -Value $effectivePublicKey -Label 'VapidPublicKey'))
        }
        finally { $sha256.Dispose() }
        $publicKeyFingerprint = ([BitConverter]::ToString($fingerprintBytes)).Replace('-', ':')

        Set-EnvironmentVariableValue -Collection $environmentVariables -Name 'WebPush__Enabled' -Value 'true'
        Set-EnvironmentVariableValue -Collection $environmentVariables -Name 'WebPush__PublicKey' -Value $effectivePublicKey
        Set-EnvironmentVariableValue -Collection $environmentVariables -Name 'WebPush__PrivateKey' -Value $plainPrivateKey
        Set-EnvironmentVariableValue -Collection $environmentVariables -Name 'WebPush__Subject' -Value $VapidSubject.Trim()
    }

    $serverManager.CommitChanges()
    $serverManager.Dispose()
    $serverManager = $null

    $expectedPublicKey = if ($Disable) { '' } else { $effectivePublicKey }
    try {
        Restart-WebAppPool -Name $AppPoolName
        Wait-ForConfiguration -Uri $VerificationUri -ExpectedEnabled (-not $Disable) `
            -ExpectedPublicKey $expectedPublicKey -TimeoutSeconds $HealthTimeoutSeconds
    }
    catch {
        $failure = $_.Exception.Message
        $rollbackManager = New-Object Microsoft.Web.Administration.ServerManager
        try {
            $rollbackConfiguration = $rollbackManager.GetApplicationHostConfiguration()
            $rollbackSection = $rollbackConfiguration.GetSection('system.webServer/aspNetCore', $SiteName)
            $rollbackCollection = $rollbackSection.GetCollection('environmentVariables')
            Restore-EnvironmentVariables -Collection $rollbackCollection -Snapshot $snapshot -Names $managedNames
            $rollbackManager.CommitChanges()
        }
        finally { $rollbackManager.Dispose() }
        try {
            Restart-WebAppPool -Name $AppPoolName
            throw "Web Push verification failed and the prior IIS values were restored. $failure"
        }
        catch {
            if ($_.Exception.Message -like 'Web Push verification failed*') { throw }
            throw "Web Push verification failed. The prior IIS values were restored, but the app pool could not be restarted: $($_.Exception.Message). Original failure: $failure"
        }
    }

    $completionStatus = if ($Disable) {
        'PROJECT_TRACKER_WEB_PUSH_DISABLED_AND_HEALTHY'
    } else {
        'PROJECT_TRACKER_WEB_PUSH_CONFIGURED_AND_HEALTHY'
    }
    if (-not $Disable) {
        Write-Host "VAPID public key: $effectivePublicKey"
        Write-Host "VAPID public-key SHA-256 fingerprint: $publicKeyFingerprint"
        if ($GenerateKeys) {
            Write-Warning 'Save this public key and fingerprint. The generated private key was written only to IIS and was not displayed.'
        }
    }
    Write-Host $completionStatus
}
finally {
    if ($null -ne $serverManager) { $serverManager.Dispose() }
    $plainPrivateKey = $null
    if ($privateKeyPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($privateKeyPointer)
    }
    if ($null -ne $generatedPrivateBytes) {
        [Array]::Clear($generatedPrivateBytes, 0, $generatedPrivateBytes.Length)
    }
    if ($null -ne $generatedPublicBytes) {
        [Array]::Clear($generatedPublicBytes, 0, $generatedPublicBytes.Length)
    }
    if ($null -ne $generatedKey) { $generatedKey.Dispose() }
}
