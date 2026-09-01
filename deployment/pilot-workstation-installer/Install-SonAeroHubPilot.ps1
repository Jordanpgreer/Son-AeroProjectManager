<# Installs the HTTPS pilot trust and shortcut on one manifest-locked workstation. #>
[CmdletBinding()]
param(
    [ValidateSet('Trust', 'Shortcut')]
    [string]$ElevatedAction,

    [string]$OriginalAccountName
)

$ErrorActionPreference = 'Stop'

function Normalize-AccountName {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $normalized = $Value.Trim().Replace('/', '\')
    if ($normalized -notmatch '^[^\\\s]+\\[^\\\s]+$') { return $null }
    return $normalized
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-PackageFile {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing. Extract the complete ZIP before running the installer."
    }
}

function Assert-PackageIntegrity {
    param([Parameter(Mandatory)]$Configuration, [Parameter(Mandatory)][string]$PackageRoot)
    foreach ($file in @($Configuration.Files)) {
        $relativePath = [string]$file.RelativePath
        $expectedHash = [string]$file.Sha256
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [IO.Path]::IsPathRooted($relativePath) -or $relativePath.Contains('..') -or
            $expectedHash -notmatch '^[A-Fa-f0-9]{64}$') {
            throw 'The pilot installer manifest contains an unsafe file entry.'
        }
        $path = [IO.Path]::GetFullPath((Join-Path $PackageRoot $relativePath))
        if (-not $path.StartsWith($PackageRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "The pilot installer is incomplete: $relativePath"
        }
        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
        if ($actualHash -ne $expectedHash.ToUpperInvariant()) {
            throw "The pilot installer integrity check failed: $relativePath"
        }
    }
}

function Invoke-EmployeeRequest {
    param([Parameter(Mandatory)][string]$Uri)
    try {
        return Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $Uri -TimeoutSec 20
    }
    catch {
        throw "Could not securely reach $Uri. $($_.Exception.Message)"
    }
}

function Invoke-ElevatedAction {
    param(
        [Parameter(Mandatory)][ValidateSet('Trust', 'Shortcut')][string]$Action,
        [Parameter(Mandatory)][string]$AccountName
    )
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath),
        '-ElevatedAction', $Action, '-OriginalAccountName', ('"{0}"' -f $AccountName)
    ) -join ' '
    try {
        $process = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') `
            -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    }
    catch { throw "Administrator approval for the pilot $Action step was canceled or could not start." }
    if ($process.ExitCode -ne 0) { throw "The elevated pilot $Action step failed with exit code $($process.ExitCode)." }
}

try {
    $packageRoot = Split-Path -Parent $PSCommandPath
    $configurationPath = Join-Path $packageRoot 'pilot-installer-config.json'
    $trustInstaller = Join-Path $packageRoot 'Set-HubPilotWorkstationTrust.ps1'
    $shortcutInstaller = Join-Path $packageRoot 'Install-EmployeeHubShortcut.ps1'
    $iconPath = Join-Path $packageRoot 'arda.ico'
    Assert-PackageFile $configurationPath 'The pilot installer configuration'
    Assert-PackageFile $trustInstaller 'The pilot trust installer'
    Assert-PackageFile $shortcutInstaller 'The shortcut installer'
    Assert-PackageFile $iconPath 'The Arda icon'

    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    if ($configuration.SchemaVersion -ne 1 -or $configuration.PilotOnly -ne $true) {
        throw 'This is not a supported Son-Aero two-person pilot package.'
    }
    Assert-PackageIntegrity -Configuration $configuration -PackageRoot $packageRoot

    $expectedComputer = [string]$configuration.ExpectedComputerName
    $expectedAccount = Normalize-AccountName ([string]$configuration.ExpectedAccountName)
    $hubUri = $null
    if ($expectedComputer -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,62}$' -or
        $env:COMPUTERNAME -ine $expectedComputer) {
        throw "This ZIP is restricted to '$expectedComputer'; this computer is '$env:COMPUTERNAME'."
    }
    if ($null -eq $expectedAccount -or
        -not [Uri]::TryCreate([string]$configuration.HubUri, [UriKind]::Absolute, [ref]$hubUri) -or
        $hubUri.Scheme -ne 'https' -or $hubUri.Host -ine 'SON-IIS2' -or $hubUri.Port -ne 6140 -or
        $hubUri.AbsolutePath -ne '/' -or $hubUri.Query -or $hubUri.Fragment -or $hubUri.UserInfo) {
        throw 'The pilot package identity or HTTPS Hub address is invalid.'
    }

    if ($ElevatedAction) {
        if (-not (Test-IsAdministrator)) { throw 'Administrator approval is required for this pilot step.' }
        if ((Normalize-AccountName $OriginalAccountName) -ine $expectedAccount) {
            throw 'The original employee identity was not preserved during elevation.'
        }
        & $trustInstaller -BundleDirectory $packageRoot -ExpectedComputerName $expectedComputer `
            -Operation Install -Confirm:$false
        if ($ElevatedAction -eq 'Shortcut') {
            & $shortcutInstaller -HubUri $hubUri.AbsoluteUri -IconSource $iconPath -Confirm:$false
        }
        Write-Host "SONAERO_HUB_PILOT_$($ElevatedAction.ToUpperInvariant())_READY"
        exit 0
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $currentAccount = Normalize-AccountName $identity.Name
    if ($currentAccount -ine $expectedAccount) {
        throw "This ZIP is restricted to '$expectedAccount'; the signed-in account is '$currentAccount'."
    }

    Write-Host "Pilot root SHA-256: $($configuration.RootCertificateSha256)"
    Write-Host "Trusting it on $expectedComputer only requires the first administrator approval."
    Invoke-ElevatedAction -Action Trust -AccountName $currentAccount

    $healthUri = [Uri]::new($hubUri, 'api/health').AbsoluteUri
    $meUri = [Uri]::new($hubUri, 'api/me').AbsoluteUri
    $health = Invoke-EmployeeRequest $healthUri
    if ($health.StatusCode -ne 200) { throw "The HTTPS Hub health check returned HTTP $($health.StatusCode)." }
    $me = Invoke-EmployeeRequest $meUri
    if ($me.StatusCode -ne 200) { throw "The HTTPS Hub identity check returned HTTP $($me.StatusCode)." }
    try { $profile = $me.Content | ConvertFrom-Json }
    catch { throw 'The HTTPS Hub returned an unreadable identity response.' }
    $returnedAccount = Normalize-AccountName ([string]$profile.accountName)
    if ($returnedAccount -ine $currentAccount) {
        throw "The HTTPS Hub authenticated '$returnedAccount' instead of '$currentAccount'."
    }

    Write-Host "Authenticated securely as $returnedAccount."
    Write-Host 'Creating the shared HTTPS shortcut requires the second administrator approval.'
    Invoke-ElevatedAction -Action Shortcut -AccountName $currentAccount
    Write-Host ''
    Write-Host 'SONAERO_HUB_PILOT_INSTALL_COMPLETE'
    Write-Host "HTTPS shortcut target: $($hubUri.AbsoluteUri)"
    exit 0
}
catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
