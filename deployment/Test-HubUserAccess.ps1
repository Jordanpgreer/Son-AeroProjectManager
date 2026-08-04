<#
    Read-only validation of Windows identity and SON-AERO Hub module access.
    Run interactively as the employee being tested. Do not run from N-central/System Shell.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]{0,253}$')]
    [string]$ServerName = 'SON-IIS2',

    [ValidateSet('http', 'https')]
    [string]$Scheme = 'http',

    [Parameter(Mandatory = $true)]
    [string]$ExpectedAccountName,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,62}$')]
    [string]$ExpectedDomain = 'SON4L',

    [Parameter(Mandatory = $true)]
    [ValidateSet('Viewer', 'Editor', 'Admin')]
    [string]$ExpectedPortalRole,

    [Parameter(Mandatory = $true)]
    [hashtable]$ExpectedPortalModuleRoles,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Access', 'NoAccess')]
    [string]$ExpectedProjectTrackerAccess,

    [string[]]$ExpectedProjectTrackerGroups,
    [string[]]$ExpectedProjectTrackerPermissions,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Viewer', 'Editor', 'Admin', 'NoAccess')]
    [string]$ExpectedEngineeringRole,

    [string[]]$ExpectedEngineeringPermissions,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Viewer', 'Editor', 'Admin', 'NoAccess')]
    [string]$ExpectedEstimatingRole,

    [string[]]$ExpectedEstimatingPermissions,

    [ValidateRange(2, 60)]
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]

function Normalize-AccountName {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $normalized = $Value.Trim().Replace('/', '\')
    $parts = $normalized.Split('\')
    if ($parts.Count -ne 2 -or [string]::IsNullOrWhiteSpace($parts[0]) -or
        [string]::IsNullOrWhiteSpace($parts[1])) {
        return $null
    }
    return ($parts[0].Trim() + '\' + $parts[1].Trim())
}

function Get-HttpErrorResponse {
    param([Management.Automation.ErrorRecord]$ErrorRecord)

    $response = $ErrorRecord.Exception.Response
    if ($null -eq $response) {
        return [pscustomobject]@{ StatusCode = 0; Body = ''; Error = $ErrorRecord.Exception.Message }
    }

    $statusCode = [int]$response.StatusCode
    $body = ''
    try {
        $stream = $response.GetResponseStream()
        if ($null -ne $stream) {
            $reader = New-Object IO.StreamReader($stream)
            try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
    }
    catch { }
    return [pscustomobject]@{ StatusCode = $statusCode; Body = $body; Error = $ErrorRecord.Exception.Message }
}

function Invoke-HubRequest {
    param([string]$Uri)

    try {
        $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $Uri -TimeoutSec $TimeoutSeconds
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Body = [string]$response.Content
            Error = $null
        }
    }
    catch {
        return Get-HttpErrorResponse -ErrorRecord $_
    }
}

function Convert-BodyFromJson {
    param([string]$Body, [string]$Label)

    try { return $Body | ConvertFrom-Json }
    catch {
        $failures.Add("$Label returned HTTP 200 with invalid JSON.")
        return $null
    }
}

function Test-ExpectedDenial {
    param(
        [object]$Response,
        [string]$Label,
        [string]$ExpectedCode
    )

    if ($Response.StatusCode -ne 403) {
        $failures.Add("$Label returned HTTP $($Response.StatusCode); expected HTTP 403 for NoAccess.")
        return
    }

    if ([string]::IsNullOrWhiteSpace($ExpectedCode)) { return }
    if ([string]::IsNullOrWhiteSpace([string]$Response.Body)) {
        $failures.Add("$Label returned HTTP 403 without the expected '$ExpectedCode' JSON error code.")
        return
    }

    try { $errorPayload = $Response.Body | ConvertFrom-Json }
    catch {
        $failures.Add("$Label returned HTTP 403 with invalid JSON; expected error code '$ExpectedCode'.")
        return
    }

    if ([string]$errorPayload.code -cne $ExpectedCode) {
        $failures.Add("$Label denial code '$($errorPayload.code)' does not match '$ExpectedCode'.")
    }
}

function Normalize-Set {
    param([object[]]$Values)

    return @($Values | ForEach-Object { ([string]$_).Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
}

function Test-ExactSet {
    param(
        [object[]]$Actual,
        [object[]]$Expected,
        [string]$Label
    )

    $actualSet = Normalize-Set $Actual
    $expectedSet = Normalize-Set $Expected
    $missing = @($expectedSet | Where-Object { $actualSet -inotcontains $_ })
    $unexpected = @($actualSet | Where-Object { $expectedSet -inotcontains $_ })
    if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
        $failures.Add("$Label mismatch. Missing: [$($missing -join ', ')]. Unexpected: [$($unexpected -join ', ')].")
    }
}

function Get-RolePermissions {
    param([string]$ModuleKey, [string]$Role)

    if ($Role -eq 'NoAccess') { return @() }
    if ($ModuleKey -eq 'engineering') {
        $permissions = @('engineering.module.view')
        if ($Role -in @('Editor', 'Admin')) { $permissions += 'engineering.module.edit' }
        if ($Role -eq 'Admin') { $permissions += 'engineering.module.admin' }
        return $permissions
    }
    if ($ModuleKey -eq 'estimating') {
        $permissions = @('estimating.view', 'estimating.calculate')
        if ($Role -in @('Editor', 'Admin')) {
            $permissions += @('estimating.quotes.manage', 'estimating.inputs.manage')
        }
        if ($Role -eq 'Admin') {
            $permissions += @('estimating.rates.admin', 'estimating.settings.admin')
        }
        return $permissions
    }
    throw "Unknown module key '$ModuleKey'."
}

$windowsIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentAccount = Normalize-AccountName $windowsIdentity.Name
if ($windowsIdentity.IsSystem -or $windowsIdentity.Name -ieq 'NT AUTHORITY\SYSTEM') {
    throw 'This test rejects Local System. Run it interactively as the SON4L employee being verified.'
}
if ($null -eq $currentAccount) {
    throw "The current Windows identity '$($windowsIdentity.Name)' is not in DOMAIN\user form."
}
$currentDomain = $currentAccount.Split('\')[0]
if ($currentDomain -ine $ExpectedDomain) {
    throw "The current Windows identity '$currentAccount' is not in the expected $ExpectedDomain domain."
}

$canonicalExpectedAccount = if ($PSBoundParameters.ContainsKey('ExpectedAccountName')) {
    Normalize-AccountName $ExpectedAccountName
} else {
    $currentAccount
}
if ($null -eq $canonicalExpectedAccount) {
    throw 'ExpectedAccountName must use DOMAIN\user or DOMAIN/user form.'
}
if ($canonicalExpectedAccount -ine $currentAccount) {
    throw "Current identity '$currentAccount' does not match expected identity '$canonicalExpectedAccount'."
}
if ($ExpectedProjectTrackerAccess -eq 'NoAccess' -and
    ($PSBoundParameters.ContainsKey('ExpectedProjectTrackerGroups') -or
        $PSBoundParameters.ContainsKey('ExpectedProjectTrackerPermissions'))) {
    throw 'Project Tracker group or permission expectations cannot be supplied when ExpectedProjectTrackerAccess is NoAccess.'
}
if ($ExpectedProjectTrackerAccess -eq 'Access' -and
    -not $PSBoundParameters.ContainsKey('ExpectedProjectTrackerGroups')) {
    throw 'ExpectedProjectTrackerGroups is required when Project Tracker access is expected.'
}
if ($ExpectedEngineeringRole -eq 'NoAccess' -and
    $PSBoundParameters.ContainsKey('ExpectedEngineeringPermissions') -and
    (Normalize-Set $ExpectedEngineeringPermissions).Count -gt 0) {
    throw 'Non-empty Engineering permissions cannot be expected with the NoAccess role.'
}
if ($ExpectedEstimatingRole -eq 'NoAccess' -and
    $PSBoundParameters.ContainsKey('ExpectedEstimatingPermissions') -and
    (Normalize-Set $ExpectedEstimatingPermissions).Count -gt 0) {
    throw 'Non-empty Estimating permissions cannot be expected with the NoAccess role.'
}

$modules = @(
    [pscustomobject]@{ Key = 'portal'; Name = 'Portal'; Port = 5140; DenialCode = '' },
    [pscustomobject]@{ Key = 'project-tracker'; Name = 'Project Tracker'; Port = 5135; DenialCode = '' },
    [pscustomobject]@{ Key = 'engineering'; Name = 'Engineering'; Port = 5150; DenialCode = 'ModuleAccessDenied' },
    [pscustomobject]@{ Key = 'estimating'; Name = 'Estimating'; Port = 5160; DenialCode = 'EstimatingAccessDenied' }
)

$report = foreach ($module in $modules) {
    $baseUri = '{0}://{1}:{2}' -f $Scheme, $ServerName, $module.Port
    $health = Invoke-HubRequest -Uri "$baseUri/api/health"
    if ($health.StatusCode -ne 200) {
        $failures.Add("$($module.Name) health failed: HTTP $($health.StatusCode) $($health.Error)")
    }

    $me = Invoke-HubRequest -Uri "$baseUri/api/me"
    $hasAccess = $me.StatusCode -eq 200
    if ($module.Key -eq 'portal' -and -not $hasAccess) {
        $failures.Add("Portal /api/me returned HTTP $($me.StatusCode); every domain employee must authenticate to the Portal with HTTP 200.")
    } elseif ($me.StatusCode -eq 401) {
        $failures.Add("$($module.Name) /api/me returned HTTP 401. Windows authentication failed; this is not a valid NoAccess result.")
    } elseif (-not $hasAccess -and $me.StatusCode -ne 403) {
        $failures.Add("$($module.Name) /api/me failed unexpectedly: HTTP $($me.StatusCode) $($me.Error)")
    }
    $payload = if ($hasAccess) { Convert-BodyFromJson -Body $me.Body -Label $module.Name } else { $null }

    if ($null -ne $payload) {
        if ([string]::IsNullOrWhiteSpace([string]$payload.accountName)) {
            $failures.Add("$($module.Name) /api/me omitted accountName.")
        } else {
            $returnedAccount = Normalize-AccountName ([string]$payload.accountName)
            if ($null -eq $returnedAccount -or $returnedAccount -ine $canonicalExpectedAccount) {
                $failures.Add("$($module.Name) returned account '$($payload.accountName)'; expected '$canonicalExpectedAccount'.")
            }
        }
    }

    switch ($module.Key) {
        'portal' {
            if ($PSBoundParameters.ContainsKey('ExpectedPortalRole')) {
                if ($hasAccess -and $payload.role -ine $ExpectedPortalRole) {
                    $failures.Add("Portal role '$($payload.role)' does not match '$ExpectedPortalRole'.")
                }
            }
            if ($hasAccess -and $PSBoundParameters.ContainsKey('ExpectedPortalModuleRoles')) {
                $expectedKeys = @($ExpectedPortalModuleRoles.Keys | ForEach-Object { ([string]$_).Trim().ToLowerInvariant() })
                foreach ($key in $expectedKeys) {
                    if ($key -notin @('engineering', 'estimating')) {
                        $failures.Add("Unknown portal module expectation '$key'. Use engineering or estimating.")
                        continue
                    }
                    $expectedRole = [string]$ExpectedPortalModuleRoles[$key]
                    if ($expectedRole -notin @('Viewer', 'Editor', 'Admin', 'NoAccess')) {
                        $failures.Add("Invalid expected portal role '$expectedRole' for $key.")
                        continue
                    }
                    $assignment = @($payload.modules | Where-Object { $_.moduleKey -ieq $key })
                    if ($expectedRole -eq 'NoAccess') {
                        if ($assignment.Count -ne 0) { $failures.Add("Portal unexpectedly returned a $key assignment.") }
                    } else {
                        if ($assignment.Count -ne 1) {
                            $failures.Add("Portal did not return exactly one $key assignment.")
                        } else {
                            if ($assignment[0].role -ine $expectedRole) {
                                $failures.Add("Portal $key role '$($assignment[0].role)' does not match '$expectedRole'.")
                            }
                            Test-ExactSet -Actual $assignment[0].permissions `
                                -Expected (Get-RolePermissions -ModuleKey $key -Role $expectedRole) `
                                -Label "Portal $key permissions"
                        }
                    }
                }
                $actualKeys = @($payload.modules | ForEach-Object { ([string]$_.moduleKey).ToLowerInvariant() })
                $expectedAccessibleKeys = @($expectedKeys | Where-Object {
                    [string]$ExpectedPortalModuleRoles[$_] -ne 'NoAccess'
                })
                Test-ExactSet -Actual $actualKeys -Expected $expectedAccessibleKeys -Label 'Portal module assignments'
            } elseif (-not $hasAccess -and $PSBoundParameters.ContainsKey('ExpectedPortalModuleRoles')) {
                $failures.Add('Portal denied access, so its expected module assignments could not be verified.')
            }
        }
        'project-tracker' {
            if ($PSBoundParameters.ContainsKey('ExpectedProjectTrackerAccess')) {
                if ($ExpectedProjectTrackerAccess -eq 'NoAccess') {
                    Test-ExpectedDenial -Response $me -Label 'Project Tracker' -ExpectedCode $module.DenialCode
                } elseif (-not $hasAccess) {
                    $failures.Add("Project Tracker returned HTTP $($me.StatusCode); expected HTTP 200 for Access.")
                }
            }
            if ($hasAccess -and $PSBoundParameters.ContainsKey('ExpectedProjectTrackerGroups')) {
                Test-ExactSet -Actual $payload.groups -Expected $ExpectedProjectTrackerGroups -Label 'Project Tracker groups'
            }
            if ($hasAccess -and $PSBoundParameters.ContainsKey('ExpectedProjectTrackerPermissions')) {
                Test-ExactSet -Actual $payload.permissions -Expected $ExpectedProjectTrackerPermissions -Label 'Project Tracker permissions'
            }
            if (-not $hasAccess -and
                ($PSBoundParameters.ContainsKey('ExpectedProjectTrackerGroups') -or
                    $PSBoundParameters.ContainsKey('ExpectedProjectTrackerPermissions'))) {
                $failures.Add('Project Tracker denied access, so expected groups or permissions could not be verified.')
            }
        }
        'engineering' {
            if ($PSBoundParameters.ContainsKey('ExpectedEngineeringRole')) {
                if ($ExpectedEngineeringRole -eq 'NoAccess') {
                    Test-ExpectedDenial -Response $me -Label 'Engineering' -ExpectedCode $module.DenialCode
                } elseif (-not $hasAccess) {
                    $failures.Add("Engineering returned HTTP $($me.StatusCode); expected HTTP 200 for role $ExpectedEngineeringRole.")
                } elseif ($hasAccess -and $payload.role -ine $ExpectedEngineeringRole) {
                    $failures.Add("Engineering role '$($payload.role)' does not match '$ExpectedEngineeringRole'.")
                }
            }
            if ($hasAccess) {
                $expectedPermissions = if ($PSBoundParameters.ContainsKey('ExpectedEngineeringPermissions')) {
                    $ExpectedEngineeringPermissions
                } elseif ($PSBoundParameters.ContainsKey('ExpectedEngineeringRole')) {
                    Get-RolePermissions -ModuleKey engineering -Role $ExpectedEngineeringRole
                }
                if ($null -ne $expectedPermissions) {
                    Test-ExactSet -Actual $payload.permissions -Expected $expectedPermissions -Label 'Engineering permissions'
                }
            } elseif ($PSBoundParameters.ContainsKey('ExpectedEngineeringPermissions') -and
                $ExpectedEngineeringRole -ne 'NoAccess') {
                $failures.Add('Engineering denied access, so expected permissions could not be verified.')
            }
        }
        'estimating' {
            if ($PSBoundParameters.ContainsKey('ExpectedEstimatingRole')) {
                if ($ExpectedEstimatingRole -eq 'NoAccess') {
                    Test-ExpectedDenial -Response $me -Label 'Estimating' -ExpectedCode $module.DenialCode
                } elseif (-not $hasAccess) {
                    $failures.Add("Estimating returned HTTP $($me.StatusCode); expected HTTP 200 for role $ExpectedEstimatingRole.")
                } elseif ($hasAccess -and $payload.role -ine $ExpectedEstimatingRole) {
                    $failures.Add("Estimating role '$($payload.role)' does not match '$ExpectedEstimatingRole'.")
                }
            }
            if ($hasAccess) {
                $expectedPermissions = if ($PSBoundParameters.ContainsKey('ExpectedEstimatingPermissions')) {
                    $ExpectedEstimatingPermissions
                } elseif ($PSBoundParameters.ContainsKey('ExpectedEstimatingRole')) {
                    Get-RolePermissions -ModuleKey estimating -Role $ExpectedEstimatingRole
                }
                if ($null -ne $expectedPermissions) {
                    Test-ExactSet -Actual $payload.permissions -Expected $expectedPermissions -Label 'Estimating permissions'
                }
            } elseif ($PSBoundParameters.ContainsKey('ExpectedEstimatingPermissions') -and
                $ExpectedEstimatingRole -ne 'NoAccess') {
                $failures.Add('Estimating denied access, so expected permissions could not be verified.')
            }
        }
    }

    [pscustomobject]@{
        Module = $module.Name
        Health = $health.StatusCode
        Access = if ($hasAccess) { 'Allowed' } else { 'Denied' }
        Role = if ($null -ne $payload -and $null -ne $payload.role) { [string]$payload.role } else { '' }
        Account = if ($null -ne $payload) { [string]$payload.accountName } else { '' }
    }
}

Write-Host "Windows identity: $currentAccount"
$report | Format-Table -AutoSize
if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'ACCESS VERIFICATION FAILURES:'
    $failures | ForEach-Object { Write-Host " - $_" }
    throw "Hub access verification failed with $($failures.Count) issue(s)."
}

Write-Host 'HUB_USER_ACCESS_VERIFIED'
