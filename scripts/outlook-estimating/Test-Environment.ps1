#Requires -Version 5.1
# Read-only setup diagnostics: never activates Outlook or reads mailbox items.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'Connector.Core.ps1')

Write-Host 'Arda Outlook connector setup check'
Write-Host 'No mail is read, imported, created or sent. No configuration is changed.'
$issues = 0
try {
    $packaged = Read-ConnectorJson (Join-Path $PSScriptRoot 'configuration.json')
    if (-not $packaged) { $packaged = Read-ConnectorJson (Join-Path $PSScriptRoot 'configuration.json.example') }
    if (-not $packaged) { $packaged = @{} }
    $defaults = Get-ConnectorConfiguration $packaged
    $localRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Arda\OutlookEstimating'
    $configurationPath = Join-Path $localRoot ('configuration-' + (Get-ConnectorHash $defaults.baseUrl).Substring(0, 20) + '.json')
    $stored = Read-ConnectorJson $configurationPath
    $config = $defaults
    if ($stored) { $config = Get-ConnectorConfiguration $stored }
    Write-Host ('Arda address: ' + $config.baseUrl)
    if (-not $stored -or -not $config.mailbox) {
        Write-Host 'ATTENTION: Setup has not been saved for this package. Run Start.cmd -Setup after opening classic Outlook.'
        $issues++
    } else { Write-Host 'PASS: Saved connector setup found (mailbox address withheld).' }

    if ([Type]::GetTypeFromProgID('Outlook.Application')) { Write-Host 'PASS: Classic Outlook is registered.' }
    else { Write-Host 'ATTENTION: Classic Outlook is not installed or registered.'; $issues++ }
    if (Get-Process OUTLOOK -ErrorAction SilentlyContinue) { Write-Host 'PASS: Classic Outlook is running.' }
    else { Write-Host 'NOTE: Classic Outlook is not running. Open it normally and verify your account before starting the connector.' }
    Write-Host 'NOTE: This check does not activate Outlook or verify its mailbox profile.'

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host 'ATTENTION: The connector must run normally, without administrator elevation.'; $issues++
    }
    $uri = [Uri]$config.baseUrl
    $resolved = $false
    try { $resolved = @([Net.Dns]::GetHostAddresses($uri.DnsSafeHost)).Count -gt 0 } catch { $resolved = $false }
    if (-not $resolved) {
        Write-Host 'ATTENTION: Arda host does not resolve. Check your company network/VPN and the HTTPS address in Start.cmd -Setup.'
        $issues++
    } else {
        Write-Host 'PASS: Arda host resolves.'
        try {
            # Read only; never posts a heartbeat/import or follows a redirect with Windows credentials.
            $response = Invoke-WebRequest -Uri ($config.baseUrl + '/api/vendor-quotes/sync') -Method Get -UseBasicParsing `
                -UseDefaultCredentials -MaximumRedirection 0 -TimeoutSec 10 -ErrorAction Stop
            $mediaType = [string]$response.Headers['Content-Type']
            if ($response.StatusCode -eq 200 -and $mediaType -match '^application/json(?:;|$)') {
                Write-Host 'PASS: Arda accepted the read-only Windows-authenticated sync-status request.'
            } else { Write-Host 'ATTENTION: Arda returned an unexpected response; check the configured Estimating address.'; $issues++ }
        } catch {
            $status = 0
            if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
                try { $status = [int]$_.Exception.Response.StatusCode } catch { $status = 0 }
            }
            $message = switch ($status) {
                401 { 'Windows authentication was not accepted. Use the approved company account and network.' }
                403 { 'Your Windows account does not have Quote Status access in Arda.' }
                404 { 'The configured address does not expose the Estimating connector API.' }
                default {
                    if ($status -ge 300 -and $status -lt 400) { 'Arda redirected the request. Configure the final HTTPS Estimating address.' }
                    else { 'Arda could not be reached securely. Check network/VPN, the server, and the trusted TLS certificate.' }
                }
            }
            Write-Host ('ATTENTION: ' + $message); $issues++
        }
    }
} catch { Write-Host ('ATTENTION: ' + (Get-ConnectorErrorCode $_ 'local-setup-check-failed')); $issues++ }
Write-Host 'Draft email files can be opened separately in classic Outlook; correspondence syncing still requires a running connector.'
if ($issues -gt 0) { exit 1 }
exit 0
