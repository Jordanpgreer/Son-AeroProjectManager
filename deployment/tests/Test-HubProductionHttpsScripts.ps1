[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw "Run this focused compatibility test with Windows PowerShell 5.1; current version is $($PSVersionTable.PSVersion)."
}
$deploymentRoot = Split-Path -Parent $PSScriptRoot
$modulePath = Join-Path $deploymentRoot 'HubProductionHttps.Common.psm1'
$readinessPath = Join-Path $deploymentRoot 'Test-HubProductionHttpsReadiness.ps1'
$configurePath = Join-Path $deploymentRoot 'Configure-HubProductionHttps.ps1'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

foreach ($path in @($modulePath, $readinessPath, $configurePath)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Missing script '$path'."
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    Assert-True (@($errors).Count -eq 0) "PowerShell parse failure in '$path': $(@($errors | ForEach-Object { $_.Message }) -join '; ')"
}

Import-Module $modulePath -Force -ErrorAction Stop
$applications = @(Get-HubProductionApplicationMap)
Assert-True ($applications.Count -eq 5) 'Expected exactly five production applications.'
Assert-True (@($applications.Site | Sort-Object -Unique).Count -eq 5) 'Production site names must be unique.'
Assert-True (@($applications.HostName | Sort-Object -Unique).Count -eq 5) 'Production host names must be unique.'
Assert-True (@($applications.HttpPort | Sort-Object -Unique).Count -eq 5) 'HTTP rollback ports must be unique.'
Assert-True (@($applications.PilotHttpsPort | Sort-Object -Unique).Count -eq 5) 'Pilot HTTPS guard ports must be unique.'
Assert-True (($applications | Where-Object { $_.Site -eq 'SonAeroPortal' }).HostName -eq 'hub.son4l.local') 'Portal hostname is incorrect.'

Assert-True (Test-HubDnsNameMatch '*.hub.son4l.local' 'projects.hub.son4l.local') 'Wildcard SAN should cover one module label.'
Assert-True (-not (Test-HubDnsNameMatch '*.hub.son4l.local' 'hub.son4l.local')) 'Wildcard SAN must not cover the zone apex.'
Assert-True (-not (Test-HubDnsNameMatch '*.hub.son4l.local' 'deep.projects.hub.son4l.local')) 'Wildcard SAN must not cover multiple labels.'
Assert-True (Test-HubDnsNameMatch 'hub.son4l.local' 'HUB.SON4L.LOCAL') 'Exact SAN matching must be case-insensitive.'

Assert-HubProductionCertificateDnsCoverage `
    -DnsNames @('hub.son4l.local', '*.hub.son4l.local') -Applications $applications
$missingWildcardRejected = $false
try {
    Assert-HubProductionCertificateDnsCoverage `
        -DnsNames @($applications.HostName) -Applications $applications
}
catch { $missingWildcardRejected = $true }
Assert-True $missingWildcardRejected 'Individually covered names must not substitute for the required managed wildcard SAN.'

$thumbprint = '00112233445566778899AABBCCDDEEFF00112233'
$hashBytes = ConvertTo-HubCertificateHashBytes $thumbprint
Assert-True ($hashBytes -is [byte[]]) 'Certificate hash conversion must return one [byte[]] object under Windows PowerShell 5.1.'
Assert-True ($hashBytes.Length -eq 20) 'A SHA-1 certificate-store thumbprint must convert to exactly 20 bytes.'
Assert-True ((ConvertFrom-HubCertificateHash $hashBytes) -eq $thumbprint) 'Certificate hash byte conversion must round-trip exactly.'

$snapshot = @()
foreach ($application in $applications) {
    $snapshot += [pscustomobject]@{
        Site = $application.Site; Protocol = 'http'; BindingInformation = "*:$($application.HttpPort):"
        CertificateHash = ''; CertificateStoreName = ''; SslFlags = 0
    }
    $snapshot += [pscustomobject]@{
        Site = $application.Site; Protocol = 'https'; BindingInformation = "*:$($application.PilotHttpsPort):"
        CertificateHash = $thumbprint; CertificateStoreName = 'My'; SslFlags = 0
    }
    $snapshot += [pscustomobject]@{
        Site = $application.Site; Protocol = 'https'; BindingInformation = "*:443:$($application.HostName)"
        CertificateHash = $thumbprint; CertificateStoreName = 'My'; SslFlags = 1
    }
}
Assert-HubBaseBindings -Snapshot $snapshot -Applications $applications
Assert-HubProductionBindingAvailability -Snapshot $snapshot -Applications $applications -Thumbprint $thumbprint
Assert-True (Test-HubDesiredBindings -Snapshot $snapshot -Applications $applications -Thumbprint $thumbprint) 'Desired SNI bindings were not recognized.'
Assert-True (@(Get-HubTargetBindingSnapshot -Snapshot $snapshot -Applications $applications).Count -eq 5) 'Target binding filter must exclude HTTP and 61xx pilot bindings.'
$planned = @(New-HubDesiredBindingSnapshot -Applications $applications -Thumbprint $thumbprint)
Assert-True ($planned.Count -eq 5) 'Planned state must contain all five production SNI bindings.'
Assert-True ((Get-HubComparableBindings $planned) -eq
    (Get-HubComparableBindings @(Get-HubTargetBindingSnapshot -Snapshot $snapshot -Applications $applications))) `
    'Planned state must exactly match the desired target-binding snapshot.'

$missingPilot = @($snapshot | Where-Object {
    -not ($_.Site -eq 'ProjectTracker' -and $_.BindingInformation -eq '*:6135:')
})
$missingPilotRejected = $false
try { Assert-HubBaseBindings -Snapshot $missingPilot -Applications $applications }
catch { $missingPilotRejected = $true }
Assert-True $missingPilotRejected 'A missing 61xx pilot binding must fail the immutable rollback-surface guard.'

$safeSharedBinding = @($snapshot) + [pscustomobject]@{
    Site = 'UnrelatedSniSite'; Protocol = 'https'; BindingInformation = '*:443:other.son4l.local'
    CertificateHash = $thumbprint; CertificateStoreName = 'My'; SslFlags = 1
}
Assert-HubProductionBindingAvailability -Snapshot $safeSharedBinding -Applications $applications -Thumbprint $thumbprint

$conflict = @($snapshot) + [pscustomobject]@{
    Site = 'Default Web Site'; Protocol = 'https'; BindingInformation = '*:443:'
    CertificateHash = $thumbprint; CertificateStoreName = 'My'; SslFlags = 0
}
$conflictRejected = $false
try { Assert-HubProductionBindingAvailability -Snapshot $conflict -Applications $applications -Thumbprint $thumbprint }
catch { $conflictRejected = $true }
Assert-True $conflictRejected 'A non-SNI catch-all TCP 443 conflict must be rejected.'

$nonHttpsConflict = @($snapshot) + [pscustomobject]@{
    Site = 'Default Web Site'; Protocol = 'http'; BindingInformation = '*:443:legacy.son4l.local'
    CertificateHash = ''; CertificateStoreName = ''; SslFlags = 0
}
$nonHttpsConflictRejected = $false
try { Assert-HubProductionBindingAvailability -Snapshot $nonHttpsConflict -Applications $applications -Thumbprint $thumbprint }
catch { $nonHttpsConflictRejected = $true }
Assert-True $nonHttpsConflictRejected 'A non-HTTPS binding reserving TCP 443 must be rejected.'

$wildcardConflict = @($snapshot) + [pscustomobject]@{
    Site = 'Default Web Site'; Protocol = 'https'; BindingInformation = '*:443:*.hub.son4l.local'
    CertificateHash = $thumbprint; CertificateStoreName = 'My'; SslFlags = 1
}
$wildcardConflictRejected = $false
try { Assert-HubProductionBindingAvailability -Snapshot $wildcardConflict -Applications $applications -Thumbprint $thumbprint }
catch { $wildcardConflictRejected = $true }
Assert-True $wildcardConflictRejected 'An ambiguous wildcard host binding on TCP 443 must be rejected.'

$addressConflict = @($snapshot | ForEach-Object {
    if ($_.BindingInformation -ieq '*:443:hub.son4l.local') {
        [pscustomobject]@{
            Site = $_.Site; Protocol = $_.Protocol; BindingInformation = '10.50.10.244:443:hub.son4l.local'
            CertificateHash = $_.CertificateHash; CertificateStoreName = $_.CertificateStoreName; SslFlags = $_.SslFlags
        }
    }
    else { $_ }
})
$addressConflictRejected = $false
try { Assert-HubProductionBindingAvailability -Snapshot $addressConflict -Applications $applications -Thumbprint $thumbprint }
catch { $addressConflictRejected = $true }
Assert-True $addressConflictRejected 'A target hostname on a non-wildcard IIS IP address must be rejected.'

$renewalThumbprint = 'FFEEDDCCBBAA99887766554433221100FFEEDDCC'
$renewalSnapshot = @($snapshot | ForEach-Object {
    if ($_.Protocol -ieq 'https' -and $_.BindingInformation -like '*:443:*') {
        [pscustomobject]@{
            Site = $_.Site; Protocol = $_.Protocol; BindingInformation = $_.BindingInformation
            CertificateHash = $renewalThumbprint; CertificateStoreName = $_.CertificateStoreName; SslFlags = $_.SslFlags
        }
    }
    else { $_ }
})
Assert-HubProductionBindingAvailability -Snapshot $renewalSnapshot -Applications $applications -Thumbprint $thumbprint
Assert-True (-not (Test-HubDesiredBindings -Snapshot $renewalSnapshot -Applications $applications -Thumbprint $thumbprint)) `
    'A correctly owned binding on an old certificate must be safely reconcilable but not already desired.'

$configureAst = [Management.Automation.Language.Parser]::ParseFile($configurePath, [ref]$null, [ref]$null)
$aclFunction = @($configureAst.FindAll({
    param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'New-ProtectedFileSystemSecurity'
}, $true))[0]
Assert-True ($null -ne $aclFunction) 'Protected ACL constructor function was not found.'
. ([scriptblock]::Create($aclFunction.Extent.Text))
foreach ($directory in @($false, $true)) {
    $security = if ($directory) {
        New-ProtectedFileSystemSecurity -Directory
    }
    else { New-ProtectedFileSystemSecurity }
    Assert-True $security.AreAccessRulesProtected 'Production state ACLs must disable inherited access rules.'
    Assert-True ($security.GetOwner([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-32-544') `
        'Production state ACL owner must be BUILTIN\Administrators.'
    $rules = @($security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $ruleSids = @($rules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
    Assert-True ($rules.Count -eq 2) 'Production state ACLs must contain only two explicit access rules.'
    Assert-True (($ruleSids -join '|') -eq 'S-1-5-18|S-1-5-32-544') `
        'Production state ACLs must grant access only to SYSTEM and BUILTIN\Administrators.'
    foreach ($rule in $rules) {
        Assert-True ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow) `
            'Production state ACL rules must be allow rules.'
        Assert-True (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq
            [Security.AccessControl.FileSystemRights]::FullControl) `
            'SYSTEM and BUILTIN\Administrators must have FullControl over production state.'
    }
}

$configureSource = Get-Content -LiteralPath $configurePath -Raw
$moduleSource = Get-Content -LiteralPath $modulePath -Raw
Assert-True ($configureSource -match 'SupportsShouldProcess') 'Configuration must support -WhatIf.'
Assert-True ($configureSource -match 'Invoke-AutomaticRollback') 'Configuration must include automatic rollback.'
Assert-True ($configureSource -match 'NonTargetBindingsBefore') 'Configuration must guard non-target bindings.'
Assert-True ($configureSource -match 'PlannedTargetBindings') 'Configuration must persist and enforce exact planned target bindings.'
Assert-True ($configureSource -match 'Global\\SonAero-HubHttpsBindingTransactions') `
    'Pilot and production HTTPS binding transactions must share one global mutex.'
Assert-True ($configureSource -match 'PRODUCTION_HTTPS_ROLLED_BACK_AND_RETAINED_HTTP_PILOT_HTTPS_HEALTHY') `
    'Production rollback must report both retained HTTP and pilot HTTPS health.'
Assert-True ($configureSource -match 'Assert-ProductionStateProtection') 'Existing privileged transaction state must pass ACL verification before use.'
Assert-True ($configureSource -match 'StatePath must be a JSON file directly under') 'Transaction state must remain in the protected deployment-state directory.'
Assert-True ($configureSource -match 'Never begin an IIS transaction unless every retained HTTP endpoint is already healthy') 'Configuration must verify the HTTP rollback baseline before mutation.'
Assert-True ((Get-Content -LiteralPath $readinessPath -Raw) -match 'rollback safety net') 'Readiness must verify the HTTP rollback baseline even before 443 exists.'
Assert-True ($moduleSource -match 'SslFlags\]::Sni') 'Production bindings must use IIS SNI.'
Assert-True ($moduleSource -match "ValidateSet\('http', 'pilotHttps', 'https'\)") 'Health verification must include retained 61xx pilot HTTPS endpoints.'
Assert-True ($configureSource -match 'SetAccessRuleProtection\(\$true, \$false\)') 'Production transaction state must use protected ACLs.'
Assert-True ($configureSource -match "S-1-5-18" -and $configureSource -match "S-1-5-32-544") `
    'Production transaction state ACLs must name SYSTEM and BUILTIN\Administrators.'
Assert-True ($moduleSource -notmatch 'New-NetFirewallRule|Set-NetFirewallRule|Remove-NetFirewallRule') 'Production HTTPS scripts must not mutate firewall policy.'
Assert-True ($configureSource -notmatch 'New-NetFirewallRule|Set-NetFirewallRule|Remove-NetFirewallRule') 'Configuration must not mutate firewall policy.'

Write-Output 'HUB_PRODUCTION_HTTPS_SCRIPT_TESTS_PASSED'
