<#
    Installs a shared Son-Aero Hub desktop shortcut for all users of a workstation.
    Run elevated locally, through an endpoint-management tool, or as Local System.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [string]$HubUri = 'http://SON-IIS2:5140',
    [string]$ShortcutName = 'Son-Aero Hub',
    [string]$IconSource
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$parsedUri = $null
if (-not [Uri]::TryCreate($HubUri, [UriKind]::Absolute, [ref]$parsedUri) -or
    $parsedUri.Scheme -notin @('http', 'https') -or
    [string]::IsNullOrWhiteSpace($parsedUri.Host) -or
    -not [string]::IsNullOrWhiteSpace($parsedUri.UserInfo)) {
    throw 'HubUri must be an absolute http or https URI without embedded credentials.'
}

$shortcutLeaf = $ShortcutName.Trim()
if ([string]::IsNullOrWhiteSpace($shortcutLeaf) -or $shortcutLeaf.Length -gt 100 -or
    $shortcutLeaf.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    throw 'ShortcutName must be a valid Windows file name containing 1 to 100 characters.'
}

if ([string]::IsNullOrWhiteSpace($IconSource)) {
    $packagedIcon = Join-Path $PSScriptRoot 'son-aero.ico'
    $repositoryIcon = Join-Path (Split-Path -Parent $PSScriptRoot) 'shared\branding\son-aero.ico'
    $IconSource = if (Test-Path -LiteralPath $packagedIcon -PathType Leaf) {
        $packagedIcon
    } else {
        $repositoryIcon
    }
}
$resolvedIconSource = [IO.Path]::GetFullPath($IconSource)
if (-not (Test-Path -LiteralPath $resolvedIconSource -PathType Leaf)) {
    throw "Shortcut icon was not found: $resolvedIconSource"
}
if ([IO.Path]::GetExtension($resolvedIconSource) -ine '.ico') {
    throw 'IconSource must be a Windows .ico file.'
}

$commonDesktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
if ([string]::IsNullOrWhiteSpace($commonDesktop)) {
    throw 'Windows did not return a Common Desktop directory.'
}

$brandingDirectory = Join-Path $env:ProgramData 'SonAero'
$installedIcon = Join-Path $brandingDirectory 'son-aero.ico'
$shortcutPath = Join-Path $commonDesktop ($shortcutLeaf + '.url')
$normalizedUri = $parsedUri.AbsoluteUri
$shortcutContent = @(
    '[InternetShortcut]'
    "URL=$normalizedUri"
    "IconFile=$installedIcon"
    'IconIndex=0'
) -join "`r`n"
$shortcutContent += "`r`n"

if ($WhatIfPreference) {
    $null = $PSCmdlet.ShouldProcess($brandingDirectory, 'Create shared Son-Aero branding directory')
    $null = $PSCmdlet.ShouldProcess($installedIcon, "Copy icon from '$resolvedIconSource'")
    $null = $PSCmdlet.ShouldProcess($shortcutPath, "Create shared shortcut to '$normalizedUri'")
    Write-Host 'WHATIF_READY: no shortcut files were changed.'
    return
}

if (-not (Test-IsAdministrator)) {
    throw 'Run this script from an elevated Windows PowerShell session.'
}

$performedUpdate = $false
if (-not (Test-Path -LiteralPath $brandingDirectory -PathType Container)) {
    if ($PSCmdlet.ShouldProcess($brandingDirectory, 'Create shared Son-Aero branding directory')) {
        New-Item -ItemType Directory -Path $brandingDirectory -Force | Out-Null
        $performedUpdate = $true
    }
}

$copyIcon = -not (Test-Path -LiteralPath $installedIcon -PathType Leaf)
if (-not $copyIcon) {
    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedIconSource).Hash
    $installedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installedIcon).Hash
    $copyIcon = $sourceHash -ne $installedHash
}
if ($copyIcon -and $PSCmdlet.ShouldProcess($installedIcon, "Copy icon from '$resolvedIconSource'")) {
    Copy-Item -LiteralPath $resolvedIconSource -Destination $installedIcon -Force
    $performedUpdate = $true
}

$writeShortcut = -not (Test-Path -LiteralPath $shortcutPath -PathType Leaf)
if (-not $writeShortcut) {
    $writeShortcut = [IO.File]::ReadAllText($shortcutPath) -ne $shortcutContent
}
if ($writeShortcut -and $PSCmdlet.ShouldProcess($shortcutPath, "Create shared shortcut to '$normalizedUri'")) {
    [IO.File]::WriteAllText($shortcutPath, $shortcutContent, [Text.Encoding]::ASCII)
    $performedUpdate = $true
}

[pscustomobject]@{
    Status = if ($performedUpdate) {
        'INSTALLED_OR_UPDATED'
    } elseif ($copyIcon -or $writeShortcut) {
        'CHANGE_NOT_APPROVED'
    } else {
        'ALREADY_CURRENT'
    }
    Shortcut = $shortcutPath
    Target = $normalizedUri
    Icon = $installedIcon
} | Format-List
