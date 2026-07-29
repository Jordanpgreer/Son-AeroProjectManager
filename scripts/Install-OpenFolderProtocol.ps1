param(
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$handlerScript = Join-Path $PSScriptRoot 'Open-ControlledFolder.ps1'
if (-not (Test-Path -LiteralPath $handlerScript)) {
    throw "Controlled-folder handler not found: $handlerScript"
}

$powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$protocolRoot = 'HKCU:\Software\Classes\sonaero-folder'
$commandKey = Join-Path $protocolRoot 'shell\open\command'
$iconKey = Join-Path $protocolRoot 'DefaultIcon'
$command = "`"$powerShell`" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$handlerScript`" -Uri `"%1`""

New-Item -Path $protocolRoot -Force | Out-Null
Set-Item -Path $protocolRoot -Value 'URL:SON-AERO Controlled Folder Protocol'
New-ItemProperty -Path $protocolRoot -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
New-Item -Path $commandKey -Force | Out-Null
Set-Item -Path $commandKey -Value $command
New-Item -Path $iconKey -Force | Out-Null
Set-Item -Path $iconKey -Value "$env:SystemRoot\explorer.exe,0"

if (-not $Quiet) {
    Write-Host 'Registered the SON-AERO controlled-folder link for the current Windows user.'
}
