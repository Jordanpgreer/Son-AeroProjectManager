Set-StrictMode -Version Latest

$script:ControlledFolderScheme = 'sonaero-folder'
$script:ControlledFolderHost = 'open'
$script:ControlledFolderRoot = 'S:\'
$script:QuoteShareRoot = '\\SON-FS01\Shared\- ESTIMATES\Quotes\'

function Test-SonAeroControlledRoot {
    param([Parameter(Mandatory = $true)][string]$Path)

    $normalizedPath = [System.IO.Path]::GetFullPath($Path)
    return $normalizedPath.StartsWith($script:ControlledFolderRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $normalizedPath.StartsWith($script:QuoteShareRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $normalizedPath.Equals($script:QuoteShareRoot.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)
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
    $root = $script:ControlledFolderRoot
    $driveMatch = [regex]::Match($requestedPath, '^([A-Za-z]):(.*)$')
    if ($requestedPath.StartsWith('\\', [System.StringComparison]::Ordinal)) {
        if (-not $requestedPath.StartsWith($script:QuoteShareRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Network paths must be under the Son-Aero estimating quote share.'
        }
        $root = $script:QuoteShareRoot
        $relativePath = $requestedPath.Substring($root.Length)
    }
    elseif ($driveMatch.Success) {
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
        $relativePath = $requestedPath.TrimStart('\')
    }

    if ($relativePath -match '[<>"|?*:]') {
        throw 'The controlled-folder path contained invalid Windows path characters.'
    }
    $segments = @($relativePath.Split('\') | Where-Object { $_ -ne '' })
    if ($segments | Where-Object { $_.EndsWith('.') -or $_.EndsWith(' ') }) {
        throw 'Traversal and ambiguous path segments are not allowed.'
    }
    if ($root -eq $script:QuoteShareRoot -and $segments.Count -eq 0) {
        throw 'Choose a folder under the Son-Aero estimating quote share.'
    }

    try {
        $resolvedPath = [System.IO.Path]::GetFullPath(
            $root + ($segments -join '\')
        )
    }
    catch {
        throw 'The controlled-folder path could not be normalized.'
    }
    if (-not (Test-SonAeroControlledRoot -Path $resolvedPath)) {
        throw 'The folder path resolved outside the approved locations.'
    }

    return $resolvedPath
}

function Get-SonAeroControlledOpenTarget {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ResolvedPath)

    if (-not (Test-SonAeroControlledRoot -Path $ResolvedPath)) {
        throw 'The controlled-folder target resolved outside the approved locations.'
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
        throw 'The Explorer target resolved outside the approved locations.'
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
