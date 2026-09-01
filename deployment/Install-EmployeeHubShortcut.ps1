<#
    Installs a shared Arda desktop shortcut for all users of a workstation.
    Run elevated locally, through an endpoint-management tool, or as Local System.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [string]$HubUri = 'https://hub.son4l.local',
    [string]$ShortcutName = 'Arda',
    [string]$IconSource,
    [string]$DesktopPath
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
    -not [string]::IsNullOrWhiteSpace($parsedUri.UserInfo) -or
    $parsedUri.AbsolutePath -ne '/' -or
    -not [string]::IsNullOrWhiteSpace($parsedUri.Query) -or
    -not [string]::IsNullOrWhiteSpace($parsedUri.Fragment)) {
    throw 'HubUri must be an absolute HTTP or HTTPS server origin without credentials, a path, a query, or a fragment.'
}
$approvedHubUris = @(
    'https://hub.son4l.local/',
    'http://son-iis2:5140/',
    'https://son-iis2:6140/'
)
if ($parsedUri.AbsoluteUri.ToLowerInvariant() -notin $approvedHubUris) {
    throw 'HubUri must be the permanent Portal origin or one of the retained SON-IIS2 Portal origins.'
}

$shortcutLeaf = $ShortcutName.Trim()
if ([string]::IsNullOrWhiteSpace($shortcutLeaf) -or $shortcutLeaf.Length -gt 100 -or
    $shortcutLeaf.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    throw 'ShortcutName must be a valid Windows file name containing 1 to 100 characters.'
}

if ([string]::IsNullOrWhiteSpace($IconSource)) {
    $packagedIcon = Join-Path $PSScriptRoot 'arda.ico'
    $repositoryIcon = Join-Path (Split-Path -Parent $PSScriptRoot) 'shared\branding\arda.ico'
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

$commonDesktop = if ([string]::IsNullOrWhiteSpace($DesktopPath)) {
    [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
} else {
    [IO.Path]::GetFullPath($DesktopPath)
}
if ([string]::IsNullOrWhiteSpace($commonDesktop) -or
    -not (Test-Path -LiteralPath $commonDesktop -PathType Container)) {
    throw "Common Desktop directory was not found: $commonDesktop"
}

$brandingDirectory = Join-Path $env:ProgramData 'Arda'
$installedIcon = Join-Path $brandingDirectory 'arda.ico'
$shortcutPath = Join-Path $commonDesktop ($shortcutLeaf + '.url')
$normalizedUri = $parsedUri.AbsoluteUri
$shortcutContent = @(
    '[InternetShortcut]'
    "URL=$normalizedUri"
    "IconFile=$installedIcon"
    'IconIndex=0'
) -join "`r`n"
$shortcutContent += "`r`n"

# Migrate only exact shortcut payloads written by prior versions of this installer.
# Same-named files with any different content are user-owned and must be preserved.
$legacySonAeroIcon = Join-Path (Join-Path $env:ProgramData 'SonAero') 'son-aero.ico'
$legacySonAeroContent = @(
    '[InternetShortcut]'
    "URL=$normalizedUri"
    "IconFile=$legacySonAeroIcon"
    'IconIndex=0'
) -join "`r`n"
$legacySonAeroContent += "`r`n"
$legacyShortcutDefinitions = @(
    [pscustomobject]@{
        Path = Join-Path $commonDesktop 'Arda Hub.url'
        ApprovedContent = @($shortcutContent)
    },
    [pscustomobject]@{
        Path = Join-Path $commonDesktop 'Son-Aero Hub.url'
        ApprovedContent = @($shortcutContent, $legacySonAeroContent)
    }
)
$legacyShortcutsToRemove = @()
foreach ($legacyDefinition in $legacyShortcutDefinitions) {
    if ($legacyDefinition.Path -ieq $shortcutPath -or
        -not (Test-Path -LiteralPath $legacyDefinition.Path -PathType Leaf)) {
        continue
    }
    try {
        $existingContent = [IO.File]::ReadAllText($legacyDefinition.Path)
        if (@($legacyDefinition.ApprovedContent | Where-Object { $_ -ceq $existingContent }).Count -gt 0) {
            $legacyShortcutsToRemove += $legacyDefinition.Path
        }
    }
    catch {
        Write-Warning "Preserved legacy shortcut because its installer signature could not be verified: $($legacyDefinition.Path)"
    }
}

if ($WhatIfPreference) {
    $null = $PSCmdlet.ShouldProcess($brandingDirectory, 'Create shared Arda branding directory')
    $null = $PSCmdlet.ShouldProcess($installedIcon, "Copy icon from '$resolvedIconSource'")
    $null = $PSCmdlet.ShouldProcess($shortcutPath, "Create shared shortcut to '$normalizedUri'")
    foreach ($legacyShortcutPath in $legacyShortcutsToRemove) {
        $null = $PSCmdlet.ShouldProcess($legacyShortcutPath, 'Remove verified installer-owned legacy shortcut')
    }
    Write-Host 'WHATIF_READY: no shortcut files were changed.'
    [pscustomobject]@{
        Status = 'WHATIF_READY'
        Shortcut = $shortcutPath
        Target = $normalizedUri
        Icon = $installedIcon
        LegacyShortcutsToRemove = @($legacyShortcutsToRemove)
    }
    return
}

if (-not (Test-IsAdministrator)) {
    throw 'Run this script from an elevated Windows PowerShell session.'
}

$performedUpdate = $false
if (-not (Test-Path -LiteralPath $brandingDirectory -PathType Container)) {
    if ($PSCmdlet.ShouldProcess($brandingDirectory, 'Create shared Arda branding directory')) {
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

foreach ($legacyShortcutPath in $legacyShortcutsToRemove) {
    if ($PSCmdlet.ShouldProcess($legacyShortcutPath, 'Remove verified installer-owned legacy shortcut')) {
        Remove-Item -LiteralPath $legacyShortcutPath -Force
        $performedUpdate = $true
    }
}

[pscustomobject]@{
    Status = if ($performedUpdate) {
        'INSTALLED_OR_UPDATED'
    } elseif ($copyIcon -or $writeShortcut -or $legacyShortcutsToRemove.Count -gt 0) {
        'CHANGE_NOT_APPROVED'
    } else {
        'ALREADY_CURRENT'
    }
    Shortcut = $shortcutPath
    Target = $normalizedUri
    Icon = $installedIcon
} | Format-List
