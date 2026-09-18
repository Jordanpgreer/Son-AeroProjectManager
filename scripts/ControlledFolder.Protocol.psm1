Set-StrictMode -Version Latest

$script:ControlledFolderScheme = 'sonaero-folder'
$script:ControlledFolderHost = 'open'
$script:ControlledFolderRoot = 'S:\'

function Test-SonAeroControlledRoot {
    param([Parameter(Mandatory = $true)][string]$Path)

    $normalizedRoot = [System.IO.Path]::GetFullPath($script:ControlledFolderRoot)
    $normalizedPath = [System.IO.Path]::GetFullPath($Path)
    return $normalizedPath.Equals(
        $normalizedRoot,
        [System.StringComparison]::OrdinalIgnoreCase
    ) -or $normalizedPath.StartsWith(
        $normalizedRoot,
        [System.StringComparison]::OrdinalIgnoreCase
    )
}

function Resolve-SonAeroControlledPath {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Uri)

    if ([string]::IsNullOrWhiteSpace($Uri)) {
        throw 'The controlled-folder request was empty.'
    }

    $requestUri = $null
    if (-not [System.Uri]::TryCreate($Uri, [System.UriKind]::Absolute, [ref]$requestUri)) {
        throw 'The controlled-folder request was not a valid absolute URI.'
    }
    if (-not $requestUri.Scheme.Equals(
        $script:ControlledFolderScheme,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        throw 'The folder request did not use the SON-AERO protocol.'
    }
    if (-not $requestUri.Host.Equals(
        $script:ControlledFolderHost,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        throw 'The controlled-folder request did not use the approved open action.'
    }
    if (
        -not [string]::IsNullOrEmpty($requestUri.UserInfo) -or
        -not [string]::IsNullOrEmpty($requestUri.Fragment) -or
        $requestUri.Port -ne -1 -or
        ($requestUri.AbsolutePath -ne '' -and $requestUri.AbsolutePath -ne '/')
    ) {
        throw 'The controlled-folder request contained unsupported URI components.'
    }

    $pathMatch = [regex]::Match(
        $requestUri.Query,
        '^\?path=([^&]+)$',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    )
    if (-not $pathMatch.Success) {
        throw 'The folder request must contain exactly one controlled path.'
    }

    try {
        $requestedPath = [System.Uri]::UnescapeDataString($pathMatch.Groups[1].Value).Trim()
    }
    catch {
        throw 'The controlled-folder path was not valid URL-encoded text.'
    }
    if ([string]::IsNullOrWhiteSpace($requestedPath)) {
        throw 'The controlled-folder path was empty.'
    }
    if ($requestedPath.ToCharArray() | Where-Object { [char]::IsControl($_) }) {
        throw 'The controlled-folder path contained control characters.'
    }

    $requestedPath = $requestedPath.Replace('/', '\')
    if ($requestedPath.StartsWith('\\', [System.StringComparison]::Ordinal)) {
        throw 'Network-share and device paths are not allowed; use a path on S:\.'
    }

    $relativePath = $requestedPath
    $driveMatch = [regex]::Match($requestedPath, '^([A-Za-z]):(.*)$')
    if ($driveMatch.Success) {
        if (-not $driveMatch.Groups[1].Value.Equals(
            'S',
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
            throw 'Controlled-folder paths are limited to the S:\ drive.'
        }
        if (-not $driveMatch.Groups[2].Value.StartsWith('\')) {
            throw 'Drive-relative paths are not allowed; use an absolute S:\ path.'
        }
        $relativePath = $driveMatch.Groups[2].Value.TrimStart('\')
    }
    else {
        if ($requestedPath -match '^[A-Za-z][A-Za-z0-9+.-]*:') {
            throw 'Nested URI schemes are not allowed in controlled-folder paths.'
        }
        if ($requestedPath.Contains(':')) {
            throw 'Controlled-folder paths cannot contain a drive, scheme, or alternate data stream.'
        }
        $relativePath = $requestedPath.TrimStart('\')
    }

    if ($relativePath -match '[<>"|?*]') {
        throw 'The controlled-folder path contained invalid Windows path characters.'
    }
    $segments = @($relativePath.Split('\') | Where-Object { $_ -ne '' })
    if ($segments | Where-Object { $_ -eq '.' -or $_ -eq '..' }) {
        throw 'Relative and traversal path segments are not allowed.'
    }

    try {
        $resolvedPath = [System.IO.Path]::GetFullPath(
            [System.IO.Path]::Combine($script:ControlledFolderRoot, $relativePath)
        )
    }
    catch {
        throw 'The controlled-folder path could not be normalized.'
    }
    if (-not (Test-SonAeroControlledRoot -Path $resolvedPath)) {
        throw 'The controlled-folder path resolved outside S:\.'
    }

    return $resolvedPath
}

function Get-SonAeroControlledOpenTarget {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ResolvedPath)

    if (-not (Test-SonAeroControlledRoot -Path $ResolvedPath)) {
        throw 'The controlled-folder target resolved outside S:\.'
    }

    $item = Get-Item -LiteralPath $ResolvedPath -Force -ErrorAction Stop
    if ($item.PSIsContainer) {
        return New-SonAeroControlledExplorerTarget -TargetPath $item.FullName -IsContainer
    }
    if (-not $item.Directory) {
        throw 'The selected file did not have a controlled containing folder.'
    }

    return New-SonAeroControlledExplorerTarget `
        -TargetPath $item.FullName `
        -ContainingFolder $item.Directory.FullName
}

function New-SonAeroControlledExplorerTarget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [switch]$IsContainer,
        [string]$ContainingFolder = ''
    )

    if (-not (Test-SonAeroControlledRoot -Path $TargetPath)) {
        throw 'The Explorer target resolved outside S:\.'
    }
    $normalizedTarget = [System.IO.Path]::GetFullPath($TargetPath)
    if ($IsContainer) {
        return [pscustomobject]@{
            Kind = 'Folder'
            TargetPath = $normalizedTarget
            ExplorerArguments = @("`"$normalizedTarget`"")
        }
    }
    if (
        [string]::IsNullOrWhiteSpace($ContainingFolder) -or
        -not (Test-SonAeroControlledRoot -Path $ContainingFolder)
    ) {
        throw 'The selected file did not have a controlled containing folder.'
    }

    return [pscustomobject]@{
        Kind = 'File'
        TargetPath = $normalizedTarget
        ExplorerArguments = @("/select,`"$normalizedTarget`"")
    }
}

Export-ModuleMember -Function `
    Resolve-SonAeroControlledPath, `
    Get-SonAeroControlledOpenTarget, `
    New-SonAeroControlledExplorerTarget
