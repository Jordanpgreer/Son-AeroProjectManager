<#
    Read-only production HTTPS audit for SON-IIS2.

    This script validates the managed certificate, company DNS, the five IIS sites, the existing
    HTTP/pilot bindings, and production-host binding ownership. It never changes IIS. If all five
    production bindings already exist, it also proves authenticated HTTP and HTTPS health.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(?:[A-Fa-f0-9]{2}\s*){20}$')]
    [string]$CertificateThumbprint,
    [ValidateSet('SON-IIS2')]
    [string]$ExpectedComputerName = 'SON-IIS2',
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedServerAddress = '10.50.10.244',
    [ValidateRange(7, 365)]
    [int]$MinimumRemainingDays = 30,
    [ValidateRange(30, 600)]
    [int]$HealthTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'HubProductionHttps.Common.psm1'
if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
    throw "Required production HTTPS module was not found at '$modulePath'."
}
Import-Module $modulePath -Force -ErrorAction Stop

Assert-HubComputerName $ExpectedComputerName
Assert-HubAdministrator
Import-HubIisAdministration

$applications = @(Get-HubProductionApplicationMap)
$thumbprint = ConvertTo-HubThumbprint $CertificateThumbprint
$certificate = Assert-HubProductionCertificate -Thumbprint $thumbprint -Applications $applications `
    -MinimumRemainingDays $MinimumRemainingDays
Assert-HubProductionDns -Applications $applications -ExpectedServerAddress $ExpectedServerAddress

$snapshot = @(Get-HubIisBindingSnapshot)
Assert-HubBaseBindings -Snapshot $snapshot -Applications $applications
Assert-HubProductionBindingAvailability -Snapshot $snapshot -Applications $applications -Thumbprint $thumbprint
$desired = Test-HubDesiredBindings -Snapshot $snapshot -Applications $applications -Thumbprint $thumbprint

# The retained HTTP endpoints are the rollback safety net. Prove that baseline before reporting
# readiness, even when the production 443 bindings have not been created yet.
Wait-HubEndpointHealth -Applications $applications -Scheme http -TimeoutSeconds $HealthTimeoutSeconds `
    -ExpectedComputerName $ExpectedComputerName
Wait-HubEndpointHealth -Applications $applications -Scheme pilotHttps -TimeoutSeconds $HealthTimeoutSeconds `
    -ExpectedComputerName $ExpectedComputerName

$targetBindings = @(Get-HubTargetBindingSnapshot -Snapshot $snapshot -Applications $applications)
$results = [pscustomobject]@{
    Status = if ($desired) { 'PRODUCTION_HTTPS_CONFIGURED_AND_DUAL_SCHEME_HEALTHY' } else { 'PRODUCTION_HTTPS_PREREQUISITES_READY' }
    ComputerName = $env:COMPUTERNAME
    CertificateSubject = $certificate.Subject
    CertificateIssuer = $certificate.Issuer
    CertificateThumbprint = $thumbprint
    CertificateNotAfter = $certificate.NotAfter
    ExpectedServerAddress = $ExpectedServerAddress
    ProductionHostNames = @($applications.HostName)
    ExistingTargetBindingCount = $targetBindings.Count
    DesiredBindingsPresent = $desired
    HttpBindingsPreserved = $true
    PilotBindingsPreserved = $true
    WorkstationVerificationRequired = $true
}

if ($desired) {
    Wait-HubEndpointHealth -Applications $applications -Scheme https -TimeoutSeconds $HealthTimeoutSeconds `
        -ExpectedComputerName $ExpectedComputerName
}

$results | Format-List
Write-Warning 'This server audit cannot prove port 443 access, certificate trust, or Windows-auth behavior from employee workstations. Complete the documented workstation gate before changing shortcuts.'
Write-Output $results.Status
