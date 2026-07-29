param(
    [Parameter(Mandatory = $true)]
    [string]$Uri,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

function Show-OpenFolderError([string]$message) {
    try {
        Add-Type -AssemblyName PresentationFramework -ErrorAction Stop
        [System.Windows.MessageBox]::Show(
            $message,
            'SON-AERO Controlled Folder',
            'OK',
            'Error'
        ) | Out-Null
    }
    catch {
        Write-Error $message
    }
}

try {
    $requestUri = [System.Uri]$Uri
    if ($requestUri.Scheme -ne 'sonaero-folder') {
        throw 'The folder request did not use the SON-AERO protocol.'
    }

    $pathMatch = [regex]::Match($requestUri.Query, '(?:^\?|&)path=([^&]+)')
    if (-not $pathMatch.Success) {
        throw 'The folder request did not contain a controlled path.'
    }

    $folderPath = [System.Uri]::UnescapeDataString($pathMatch.Groups[1].Value)
    if (-not [System.IO.Path]::IsPathRooted($folderPath)) {
        throw 'The controlled folder path must be an absolute Windows or network-share path.'
    }

    $resolvedPath = [System.IO.Path]::GetFullPath($folderPath)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Container)) {
        throw "The controlled folder is unavailable: $resolvedPath"
    }

    if ($ValidateOnly) {
        Write-Output $resolvedPath
        exit 0
    }

    Start-Process -FilePath "$env:SystemRoot\explorer.exe" -ArgumentList @("`"$resolvedPath`"")
}
catch {
    Show-OpenFolderError $_.Exception.Message
    exit 1
}
