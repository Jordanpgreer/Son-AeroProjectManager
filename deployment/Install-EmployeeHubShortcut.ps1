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

function New-InternetShortcutContent {
    param(
        [Parameter(Mandatory = $true)][string]$TargetUri,
        [Parameter(Mandatory = $true)][string]$IconPath
    )

    return (@(
        '[InternetShortcut]'
        "URL=$TargetUri"
        "IconFile=$IconPath"
        'IconIndex=0'
    ) -join "`r`n") + "`r`n"
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
    $packagedIcon = Join-Path $PSScriptRoot 'arda-transparent.ico'
    $repositoryIcon = Join-Path (Split-Path -Parent $PSScriptRoot) 'shared\branding\arda-transparent.ico'
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
$installedIcon = Join-Path $brandingDirectory 'arda-transparent.ico'
$shortcutPath = Join-Path $commonDesktop ($shortcutLeaf + '.url')
$normalizedUri = $parsedUri.AbsoluteUri
$shortcutContent = New-InternetShortcutContent -TargetUri $normalizedUri -IconPath $installedIcon

# Migrate only exact shortcut payloads written by prior versions of this installer.
# Same-named files with any different content are user-owned and must be preserved.
$legacyArdaIcon = Join-Path $brandingDirectory 'arda.ico'
$legacySonAeroIcon = Join-Path (Join-Path $env:ProgramData 'SonAero') 'son-aero.ico'
$approvedArdaShortcutContent = @(
    foreach ($approvedHubUri in $approvedHubUris) {
        New-InternetShortcutContent -TargetUri $approvedHubUri -IconPath $installedIcon
        New-InternetShortcutContent -TargetUri $approvedHubUri -IconPath $legacyArdaIcon
    }
)
$approvedSonAeroShortcutContent = @(
    foreach ($approvedHubUri in $approvedHubUris) {
        New-InternetShortcutContent -TargetUri $approvedHubUri -IconPath $installedIcon
        New-InternetShortcutContent -TargetUri $approvedHubUri -IconPath $legacyArdaIcon
        New-InternetShortcutContent -TargetUri $approvedHubUri -IconPath $legacySonAeroIcon
    }
)
$legacyShortcutDefinitions = @(
    [pscustomobject]@{
        Path = Join-Path $commonDesktop 'Arda Hub.url'
        ApprovedContent = $approvedArdaShortcutContent
    },
    [pscustomobject]@{
        Path = Join-Path $commonDesktop 'Son-Aero Hub.url'
        ApprovedContent = $approvedSonAeroShortcutContent
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
$approvedArdaIconSha256 = 'FC8744D2DD0E4D5E0426BA37032977E2466C1AE264AF29EAE0A274D0146537E4'
$approvedSonAeroIconSha256 = '681AC700BBECB109D5D6A85C1D997BCF3044C91038FCF9C5E889FF0E30379AC6'
$legacyIconDefinitions = @(
    [pscustomobject]@{ Path = $legacyArdaIcon; ApprovedSha256 = $approvedArdaIconSha256 }
    [pscustomobject]@{ Path = (Join-Path $brandingDirectory 'son-aero.ico'); ApprovedSha256 = $approvedSonAeroIconSha256 }
    [pscustomobject]@{ Path = $legacySonAeroIcon; ApprovedSha256 = $approvedSonAeroIconSha256 }
)
$legacyIconPaths = @()
foreach ($legacyIconDefinition in $legacyIconDefinitions) {
    if ($legacyIconDefinition.Path -ieq $installedIcon -or
        -not (Test-Path -LiteralPath $legacyIconDefinition.Path -PathType Leaf)) {
        continue
    }
    try {
        $legacyIconSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $legacyIconDefinition.Path).Hash
        if ($legacyIconSha256 -ceq $legacyIconDefinition.ApprovedSha256) {
            $legacyIconPaths += $legacyIconDefinition.Path
        }
        else {
            Write-Warning "Preserved legacy icon because its installer signature did not match: $($legacyIconDefinition.Path)"
        }
    }
    catch {
        Write-Warning "Preserved legacy icon because its installer signature could not be verified: $($legacyIconDefinition.Path)"
    }
}

if ($WhatIfPreference) {
    $null = $PSCmdlet.ShouldProcess($brandingDirectory, 'Create shared Arda branding directory')
    $null = $PSCmdlet.ShouldProcess($installedIcon, "Copy icon from '$resolvedIconSource'")
    $null = $PSCmdlet.ShouldProcess($shortcutPath, "Create shared shortcut to '$normalizedUri'")
    foreach ($legacyShortcutPath in $legacyShortcutsToRemove) {
        $null = $PSCmdlet.ShouldProcess($legacyShortcutPath, 'Remove verified installer-owned legacy shortcut')
    }
    foreach ($legacyIconPath in $legacyIconPaths) {
        $null = $PSCmdlet.ShouldProcess($legacyIconPath, 'Remove obsolete installer-owned desktop icon')
    }
    Write-Host 'WHATIF_READY: no shortcut files were changed.'
    [pscustomobject]@{
        Status = 'WHATIF_READY'
        Shortcut = $shortcutPath
        Target = $normalizedUri
        Icon = $installedIcon
        LegacyShortcutsToRemove = @($legacyShortcutsToRemove)
        LegacyIconsToRemove = @($legacyIconPaths)
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
foreach ($legacyIconPath in $legacyIconPaths) {
    if ($PSCmdlet.ShouldProcess($legacyIconPath, 'Remove obsolete installer-owned desktop icon')) {
        Remove-Item -LiteralPath $legacyIconPath -Force
        $performedUpdate = $true
    }
}

if ($performedUpdate) {
    $iconRefresh = Join-Path $env:SystemRoot 'System32\ie4uinit.exe'
    if (Test-Path -LiteralPath $iconRefresh -PathType Leaf) {
        try { & $iconRefresh -show | Out-Null }
        catch { Write-Warning "The shortcut was updated, but Windows icon refresh failed: $($_.Exception.Message)" }
    }
}

[pscustomobject]@{
    Status = if ($performedUpdate) {
        'INSTALLED_OR_UPDATED'
    } elseif ($copyIcon -or $writeShortcut -or $legacyShortcutsToRemove.Count -gt 0 -or
        $legacyIconPaths.Count -gt 0) {
        'CHANGE_NOT_APPROVED'
    } else {
        'ALREADY_CURRENT'
    }
    Shortcut = $shortcutPath
    Target = $normalizedUri
    Icon = $installedIcon
} | Format-List
