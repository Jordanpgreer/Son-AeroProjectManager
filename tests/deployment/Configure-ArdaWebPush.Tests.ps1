[CmdletBinding()]
param([string]$ScriptPath = '')

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Configure-ArdaWebPush.ps1'
}
$source = Get-Content -LiteralPath $ScriptPath -Raw
$tokens = $null
$errors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw "Configure-ArdaWebPush.ps1 does not parse: $($errors[0].Message)" }

function Assert-Contains([string]$Needle, [string]$Message) {
    if (-not $source.Contains($Needle)) { throw $Message }
}

Assert-Contains "'WebPush__ProducerKeys__quality-assurance'" 'Portal Quality producer key is not managed.'
Assert-Contains "'WebPush__ProducerKeys__project-tracker'" 'Portal Project Tracker producer key is not managed.'
Assert-Contains "'PortalPush__ProducerKey'" 'Producer-side secret is not managed.'
Assert-Contains "Set-EnvironmentValue `$collections[`$ProjectTrackerSiteName] 'WebPush__Enabled' 'false'" `
    'Legacy Project Tracker Web Push is not disabled in the paired cutover.'
Assert-Contains 'all three IIS configurations were restored' 'Health failure does not report paired rollback.'
Assert-Contains '/api/push/public-key' 'Portal public-key health is not verified.'
Assert-Contains "'https://hub.son4l.local'" 'Canonical Portal production origin is not the default.'
if ($source.Contains('arda.hub.son4l.local')) { throw 'Obsolete Portal hostname is present.' }
if ($source -match 'Write-(Host|Output).*ProducerKey') {
    throw 'The deployment script appears to print a producer secret.'
}
Write-Output 'CONFIGURE_ARDA_WEB_PUSH_TESTS_PASSED'
