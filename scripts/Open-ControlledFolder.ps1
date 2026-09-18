param(
    [Parameter(Mandatory = $true)]
    [string]$Uri,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

$protocolModule = Join-Path $PSScriptRoot 'ControlledFolder.Protocol.psm1'
Import-Module $protocolModule -Force -ErrorAction Stop

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
    $resolvedPath = Resolve-SonAeroControlledPath -Uri $Uri
    $openTarget = Get-SonAeroControlledOpenTarget -ResolvedPath $resolvedPath

    if ($ValidateOnly) {
        Write-Output $openTarget.TargetPath
        exit 0
    }

    Start-Process `
        -FilePath "$env:SystemRoot\explorer.exe" `
        -ArgumentList @($openTarget.ExplorerArguments)
}
catch {
    Show-OpenFolderError $_.Exception.Message
    exit 1
}
