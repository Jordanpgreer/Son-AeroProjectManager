<#
    Employee-facing bootstrap for the Arda desktop shortcut.
    The normal user process verifies Windows identity and Hub access. Only the
    shared-shortcut write is relaunched with elevation.
#>
[CmdletBinding()]
param(
    [string]$HubUri = '',

    [switch]$ElevatedInstall,

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

function Invoke-EmployeeRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Label
    )

    try {
        $response = Invoke-WebRequest `
            -UseBasicParsing `
            -UseDefaultCredentials `
            -Uri $Uri `
            -TimeoutSec 15
        return [pscustomobject]@{
            Label = $Label
            StatusCode = [int]$response.StatusCode
            Body = [string]$response.Content
            Error = ''
        }
    } catch [Net.WebException] {
        $statusCode = 0
        $body = ''
        if ($null -ne $_.Exception.Response) {
            $statusCode = [int]$_.Exception.Response.StatusCode
            $stream = $_.Exception.Response.GetResponseStream()
            if ($null -ne $stream) {
                $reader = New-Object IO.StreamReader($stream)
                try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
        }
        return [pscustomobject]@{
            Label = $Label
            StatusCode = $statusCode
            Body = $body
            Error = $_.Exception.Message
        }
    }
}

function Assert-PackageFile {
    param([string]$Path, [string]$Label)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing. Right-click the ZIP, choose Extract All, and run the installer from the extracted folder."
    }
}

try {
    $packageRoot = Split-Path -Parent $PSCommandPath
    $shortcutInstaller = Join-Path $packageRoot 'Install-EmployeeHubShortcut.ps1'
    $iconPath = Join-Path $packageRoot 'arda.ico'
    Assert-PackageFile -Path $shortcutInstaller -Label 'The shortcut installer'
    Assert-PackageFile -Path $iconPath -Label 'The Arda icon'

    if ([string]::IsNullOrWhiteSpace($HubUri)) {
        $configurationPath = Join-Path $packageRoot 'SonAeroHubInstaller.json'
        if (Test-Path -LiteralPath $configurationPath -PathType Leaf) {
            try { $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json }
            catch { throw "The packaged Hub configuration is unreadable: $($_.Exception.Message)" }
            if ([int]$configuration.SchemaVersion -ne 1) {
                throw 'The packaged Hub configuration has an unsupported schema version.'
            }
            $HubUri = [string]$configuration.HubUri
            if ([string]::IsNullOrWhiteSpace($HubUri)) {
                throw 'The packaged Hub configuration does not contain HubUri.'
            }
        } else {
            # Compatibility fallback for an unpackaged/manual copy of this script.
            $HubUri = 'https://hub.son4l.local'
        }
    }

    $parsedHubUri = $null
    if (-not [Uri]::TryCreate($HubUri, [UriKind]::Absolute, [ref]$parsedHubUri) -or
        $parsedHubUri.Scheme -notin @('http', 'https') -or
        [string]::IsNullOrWhiteSpace($parsedHubUri.Host) -or
        -not [string]::IsNullOrWhiteSpace($parsedHubUri.UserInfo) -or
        $parsedHubUri.AbsolutePath -ne '/' -or
        -not [string]::IsNullOrWhiteSpace($parsedHubUri.Query) -or
        -not [string]::IsNullOrWhiteSpace($parsedHubUri.Fragment)) {
        throw 'The packaged Hub address must be an absolute HTTP or HTTPS server origin without credentials, a path, a query, or a fragment.'
    }
    $approvedHubUris = @(
        'https://hub.son4l.local/',
        'http://son-iis2:5140/',
        'https://son-iis2:6140/'
    )
    if ($parsedHubUri.AbsoluteUri.ToLowerInvariant() -notin $approvedHubUris) {
        throw 'The packaged Hub address is not an approved production or pilot Portal origin.'
    }

    if ($ElevatedInstall) {
        if (-not (Test-IsAdministrator)) {
            throw 'Administrator approval is required to create the shared desktop shortcut.'
        }
        if ($null -eq (Normalize-AccountName $OriginalAccountName)) {
            throw 'The original employee identity was not preserved during elevation.'
        }

        & $shortcutInstaller `
            -HubUri $parsedHubUri.AbsoluteUri `
            -IconSource $iconPath `
            -Confirm:$false

        Write-Host 'SONAERO_EMPLOYEE_SHORTCUT_READY'
        exit 0
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $currentAccount = Normalize-AccountName $identity.Name
    if ($null -eq $currentAccount -or
        $currentAccount -match '^(NT AUTHORITY|NT SERVICE)\\' -or
        $currentAccount -match '\\SYSTEM$') {
        throw "Run this installer while signed into Windows as the intended employee, not '$($identity.Name)'."
    }

    Write-Host "Checking Son-Aero Hub for $currentAccount ..."
    $healthUri = [Uri]::new($parsedHubUri, 'api/health').AbsoluteUri
    $meUri = [Uri]::new($parsedHubUri, 'api/me').AbsoluteUri
    $health = Invoke-EmployeeRequest -Uri $healthUri -Label 'Hub health'
    if ($health.StatusCode -ne 200) {
        throw "The Hub health check failed at $healthUri (HTTP $($health.StatusCode)). $($health.Error)"
    }

    $me = Invoke-EmployeeRequest -Uri $meUri -Label 'Hub identity'
    if ($me.StatusCode -eq 401) {
        throw 'Windows authentication failed. Confirm this computer is on the SON4L domain and the site permits Windows authentication.'
    }
    if ($me.StatusCode -ne 200) {
        throw "The Hub identity check failed at $meUri (HTTP $($me.StatusCode)). $($me.Error)"
    }

    try {
        $profile = $me.Body | ConvertFrom-Json
    } catch {
        throw 'The Hub returned an unreadable identity response.'
    }
    $returnedAccount = Normalize-AccountName ([string]$profile.accountName)
    if ($null -eq $returnedAccount -or $returnedAccount -ine $currentAccount) {
        throw "The Hub authenticated '$returnedAccount' instead of the signed-in employee '$currentAccount'."
    }

    $moduleSummary = @($profile.modules | ForEach-Object { "$($_.moduleKey): $($_.role)" })
    Write-Host "Authenticated as $returnedAccount (Hub role: $($profile.role))."
    if ($moduleSummary.Count -gt 0) {
        Write-Host "Assigned module roles: $($moduleSummary -join ', ')"
    } else {
        Write-Warning 'No Portal-managed module role is currently assigned. Project Tracker access may still be assigned separately.'
    }

    if (Test-IsAdministrator) {
        & $shortcutInstaller `
            -HubUri $parsedHubUri.AbsoluteUri `
            -IconSource $iconPath `
            -Confirm:$false
    } else {
        Write-Host 'Administrator approval is required only to add the shared Arda desktop shortcut.'
        $argumentList = @(
            '-NoProfile'
            '-ExecutionPolicy'
            'Bypass'
            '-File'
            ('"{0}"' -f $PSCommandPath)
            '-ElevatedInstall'
            '-HubUri'
            ('"{0}"' -f $parsedHubUri.AbsoluteUri)
            '-OriginalAccountName'
            ('"{0}"' -f $currentAccount)
        ) -join ' '

        try {
            $elevated = Start-Process `
                -FilePath (Join-Path $PSHOME 'powershell.exe') `
                -ArgumentList $argumentList `
                -Verb RunAs `
                -Wait `
                -PassThru
        } catch {
            throw 'Administrator approval was canceled or could not be started. No shortcut was installed.'
        }
        if ($elevated.ExitCode -ne 0) {
            throw "The elevated shortcut install failed with exit code $($elevated.ExitCode)."
        }
    }

    Write-Host ''
    Write-Host 'SONAERO_HUB_EMPLOYEE_INSTALL_COMPLETE'
    Write-Host "Desktop shortcut target: $($parsedHubUri.AbsoluteUri)"
    exit 0
} catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
