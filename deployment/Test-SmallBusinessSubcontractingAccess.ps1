<# Read-only checks from the employee's normal Windows session. Never performs a Fulcrum sync. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^SON4L\\[^\\\s]+$')][string]$ExpectedAccountName,
    [Parameter(Mandatory = $true)][ValidateSet('Viewer', 'Editor', 'Admin', 'NoAccess')][string]$ExpectedRole,
    [string[]]$ExpectedPermissions,
    [ValidateRange(2, 60)][int]$TimeoutSeconds = 15
)
$ErrorActionPreference = 'Stop'

function Get-SubcontractingDefaultPermissions {
    param([Parameter(Mandatory = $true)][ValidateSet('Viewer', 'Editor', 'Admin', 'NoAccess')][string]$Role)
    if ($Role -eq 'NoAccess') { return @() }
    $keys = @('module.view', 'vendors.view', 'dashboard.view', 'export')
    if ($Role -in @('Editor', 'Admin')) { $keys += @('compliance.manage', 'documents.manage') }
    if ($Role -eq 'Admin') { $keys += 'fulcrum.sync' }
    return @($keys | ForEach-Object { 'small-business-subcontracting.' + $_ })
}

function Assert-SubcontractingAccessResponse {
    param([Parameter(Mandatory = $true)][int]$StatusCode, [AllowEmptyString()][string]$Body,
        [Parameter(Mandatory = $true)][string]$Account, [Parameter(Mandatory = $true)][string]$Role,
        [AllowEmptyCollection()][string[]]$Permissions)
    if ($Role -eq 'NoAccess') {
        if ($StatusCode -ne 403) { throw "Expected HTTP 403, received $StatusCode. HTTP 401 is an authentication failure." }
        return
    }
    if ($StatusCode -ne 200) { throw "Expected module access, received HTTP $StatusCode." }
    $payload = $Body | ConvertFrom-Json -ErrorAction Stop
    if ([string]$payload.accountName -ine $Account -or [string]$payload.role -ine $Role) {
        throw 'The returned Windows account or module role does not match the expected employee.'
    }
    $actual = @($payload.permissions | Sort-Object -Unique)
    $expected = @($Permissions | Sort-Object -Unique)
    if (($actual -join '|') -cne ($expected -join '|')) { throw 'The returned module permissions do not match the expected set.' }
}

function Invoke-SubcontractingAccessRequest {
    param([Parameter(Mandatory = $true)][string]$Path)
    $uri = 'https://subcontracting.hub.son4l.local' + $Path
    try {
        $response = Invoke-WebRequest -UseBasicParsing -UseDefaultCredentials -Uri $uri -TimeoutSec $TimeoutSeconds
        return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Body = [string]$response.Content }
    } catch {
        if ($null -eq $_.Exception.Response) { throw }
        return [pscustomobject]@{ StatusCode = [int]$_.Exception.Response.StatusCode; Body = '' }
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ($identity.IsSystem -or $identity.Name -ine $ExpectedAccountName) {
    throw 'Run interactively as the exact employee being tested, not Local System or another administrator.'
}
$permissions = if ($PSBoundParameters.ContainsKey('ExpectedPermissions')) {
    @($ExpectedPermissions)
} else { @(Get-SubcontractingDefaultPermissions -Role $ExpectedRole) }
if ($ExpectedRole -eq 'NoAccess' -and $permissions.Count) { throw 'NoAccess cannot have expected permissions.' }
$health = Invoke-SubcontractingAccessRequest -Path '/api/health'
if ($health.StatusCode -ne 200 -or ($health.Body | ConvertFrom-Json).status -cne 'ok') {
    throw 'Subcontracting health failed. Verify the application, database, and document storage.'
}
$me = Invoke-SubcontractingAccessRequest -Path '/api/me'
Assert-SubcontractingAccessResponse -StatusCode $me.StatusCode -Body $me.Body `
    -Account $ExpectedAccountName -Role $ExpectedRole -Permissions $permissions
foreach ($probe in @(
    @{ Path = '/api/vendors'; Permission = 'small-business-subcontracting.vendors.view' },
    @{ Path = '/api/dashboard'; Permission = 'small-business-subcontracting.dashboard.view' }
)) {
    $result = Invoke-SubcontractingAccessRequest -Path $probe.Path
    $expectedStatus = if ($permissions -contains $probe.Permission) { 200 } else { 403 }
    if ($result.StatusCode -ne $expectedStatus) { throw "$($probe.Path) returned HTTP $($result.StatusCode), expected $expectedStatus." }
}
Write-Output "Verified account: $ExpectedAccountName; role: $ExpectedRole"
Write-Output 'SMALL_BUSINESS_SUBCONTRACTING_USER_ACCESS_VERIFIED'
