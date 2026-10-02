[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Run these tests with Windows PowerShell 5.1.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
function Import-TestFunctions {
    param([string]$RelativePath, [string[]]$Names)
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repoRoot $RelativePath), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw ($errors.Message -join '; ') }
    $definitions = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $Names
    }, $true))
    if ($definitions.Count -ne $Names.Count) { throw "Missing test function in $RelativePath" }
    return [scriptblock]::Create(($definitions | ForEach-Object { $_.Extent.Text }) -join "`n")
}
function Assert-That { param([bool]$Condition, [string]$Message) if (-not $Condition) { throw $Message } }
function Assert-Rejected { param([scriptblock]$Action, [string]$Message)
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert-That $rejected $Message
}
. (Import-TestFunctions 'deployment\Configure-PortalProductionModuleVisibility.ps1' @(
    'Get-ApplicationMap', 'Get-NormalizedAllowedRoles', 'Set-VisibleApplicationPolicy', 'Test-VisibleApplicationPolicy'))
. (Import-TestFunctions 'deployment\Configure-IisWarmStart.ps1' @('Resolve-WarmStartEndpoint', 'New-StartupRecoveryArguments'))
. (Import-TestFunctions 'deployment\Test-SmallBusinessSubcontractingAccess.ps1' @(
    'Get-SubcontractingDefaultPermissions', 'Assert-SubcontractingAccessResponse'))
. (Import-TestFunctions 'deployment\Configure-SmallBusinessSubcontractingStorage.ps1' @(
    'Assert-StoragePath', 'New-SubcontractingStorageAcl', 'Assert-SubcontractingStorageAcl'))
. (Import-TestFunctions 'deployment\Install-SmallBusinessSubcontracting.ps1' @(
    'Invoke-InstallationStep', 'Assert-InstallationHiddenCatalog'))

$fixture = @'
{"Portal":{"Applications":[
 {"Id":"engineering-hub","AllowedRoles":["Admin"],"Url":"https://engineering.hub.son4l.local","Status":"Active"},
 {"Id":"small-business-subcontracting","AllowedRoles":["__production-disabled__"],"Url":"http://localhost:5180","Status":"Maintenance"},
 {"Id":"admin-console","AllowedRoles":["Admin"],"Url":"/#/admin/access","Status":"Active"}
]}}
'@ | ConvertFrom-Json
$before = $fixture.Portal.Applications[0] | ConvertTo-Json -Compress
Set-VisibleApplicationPolicy -Configuration $fixture -ApplicationIds @('small-business-subcontracting')
Assert-That (Test-VisibleApplicationPolicy $fixture @('small-business-subcontracting')) 'Scoped activation did not set roles, URL, and status.'
Assert-That (($fixture.Portal.Applications[0] | ConvertTo-Json -Compress) -ceq $before) 'Activation altered Engineering.'
Assert-That ($fixture.Portal.Applications[2].AllowedRoles[0] -ceq 'Admin') 'Activation altered Admin access.'
$fixture.Portal.Applications[1].Url = 'http://localhost:5180'
Assert-That (-not (Test-VisibleApplicationPolicy $fixture @('small-business-subcontracting'))) 'Localhost was accepted as active Production URL.'

$endpoint = Resolve-WarmStartEndpoint -Site SmallBusinessSubcontracting -SelectedScheme https `
    -DefaultHostName SON-IIS2 -HttpPort 5180 -HttpsPort 443 -UsePermanentHttps
Assert-That ($endpoint.HostName -ceq 'subcontracting.hub.son4l.local' -and $endpoint.Port -eq 443) 'Warm start chose the wrong hostname.'
$argumentSet = @{ InstalledScriptPath = 'C:\ProgramData\SonAero\Operations\Configure-IisWarmStart.ps1'
    ComputerName = 'SON-IIS2'; SelectedScheme = 'https'; UsePermanentHttps = $true
    ProjectTrackerPort = 6135; PortalPort = 6140; EngineeringPort = 6150
    EstimatingPort = 6160; QualityAssurancePort = 6170 }
$oldArguments = New-StartupRecoveryArguments @argumentSet
$newArguments = New-StartupRecoveryArguments @argumentSet -IncludeSmallBusinessSubcontracting
Assert-That (-not $oldArguments.Contains('-IncludeSmallBusinessSubcontracting')) 'Legacy recovery was changed.'
Assert-That ($newArguments.Contains('-IncludeSmallBusinessSubcontracting')) 'New module was omitted from reboot recovery.'

$viewer = @(Get-SubcontractingDefaultPermissions Viewer)
$editor = @(Get-SubcontractingDefaultPermissions Editor)
$admin = @(Get-SubcontractingDefaultPermissions Admin)
Assert-That ($viewer.Count -eq 4 -and $editor.Count -eq 6 -and $admin.Count -eq 7) 'Permission defaults are incorrect.'
$payload = @{ accountName = 'SON4L\example'; role = 'Viewer'; permissions = $viewer } | ConvertTo-Json
Assert-SubcontractingAccessResponse -StatusCode 200 -Body $payload -Account 'SON4L\example' -Role Viewer -Permissions $viewer
Assert-SubcontractingAccessResponse -StatusCode 403 -Body '' -Account 'SON4L\example' -Role NoAccess -Permissions @()
Assert-Rejected { Assert-SubcontractingAccessResponse 401 '' 'SON4L\example' NoAccess @() } 'Authentication failure passed NoAccess verification.'
Assert-Rejected { Assert-SubcontractingAccessResponse 200 $payload 'SON4L\another' Viewer $viewer } 'Wrong Windows identity passed.'
Assert-Rejected { Assert-SubcontractingAccessResponse 200 $payload 'SON4L\example' Viewer $admin } 'Wrong permission set passed.'

$testSid = 'S-1-5-21-100-200-300-1001'
$acl = New-SubcontractingStorageAcl -IisSid $testSid
Assert-SubcontractingStorageAcl -Acl $acl -IisSid $testSid
$extraRule = New-Object Security.AccessControl.FileSystemAccessRule(
    (New-Object Security.Principal.SecurityIdentifier('S-1-1-0')),
    [Security.AccessControl.FileSystemRights]::Modify,
    [Security.AccessControl.AccessControlType]::Allow)
[void]$acl.AddAccessRule($extraRule)
Assert-Rejected { Assert-SubcontractingStorageAcl -Acl $acl -IisSid $testSid } 'Unexpected world write grant was accepted.'
Assert-Rejected { Assert-StoragePath 'C:\SonAero\Data\..\unrelated' } 'Traversal path was accepted.'

# Real file/catalog round trip: hidden on insertion, activation persists across later releases.
Import-Module (Join-Path $repoRoot 'deployment\PortalApplicationCatalog.psm1') -Force
$tempRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('subcontracting-ops-' + [Guid]::NewGuid().ToString('N'))))
[void][IO.Directory]::CreateDirectory($tempRoot)
try {
    Copy-Item -LiteralPath (Join-Path $repoRoot 'apps\portal\src\Portal.Api\appsettings.json') -Destination (Join-Path $tempRoot 'appsettings.json')
    $templatePath = Join-Path $repoRoot 'deployment\templates\portal.appsettings.Production.json'
    $production = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json
    $production.Portal.Applications = @($production.Portal.Applications | Where-Object Id -NE 'small-business-subcontracting')
    Assert-InstallationHiddenCatalog -Configuration $production
    Assert-Rejected { Assert-InstallationHiddenCatalog -Configuration $production -RequireEntry } 'Missing card passed post-installation verification.'
    $activePath = Join-Path $tempRoot 'appsettings.Production.json'
    $production | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $activePath -Encoding UTF8
    Sync-PortalProductionApplicationCatalog -CandidatePortalPath $tempRoot -ProductionTemplatePath $templatePath | Out-Null
    $merged = Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json
    $entry = @($merged.Portal.Applications | Where-Object Id -EQ 'small-business-subcontracting')[0]
    Assert-That ($entry.Status -ceq 'Maintenance' -and $entry.AllowedRoles[0] -ceq '__production-disabled__') 'New card became available before first installation.'
    Assert-InstallationHiddenCatalog -Configuration $merged -RequireEntry
    Set-VisibleApplicationPolicy -Configuration $merged -ApplicationIds @('small-business-subcontracting')
    Assert-Rejected { Assert-InstallationHiddenCatalog -Configuration $merged } 'Already-active card passed first-installation preflight.'
    $merged | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $activePath -Encoding UTF8
    Sync-PortalProductionApplicationCatalog -CandidatePortalPath $tempRoot -ProductionTemplatePath $templatePath | Out-Null
    $upgraded = Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json
    Assert-That (Test-VisibleApplicationPolicy $upgraded @('small-business-subcontracting')) 'A later release undid module activation.'
    # Windows PowerShell Write-Host emits stream 6; existing deployment markers use it.
    $markerScript = Join-Path $tempRoot 'marker.ps1'
    'Write-Host "HOST_MARKER"' | Set-Content -LiteralPath $markerScript -Encoding UTF8
    Invoke-InstallationStep -Script $markerScript -Arguments @{} -ExpectedMarker 'HOST_MARKER'
    Assert-Rejected { Invoke-InstallationStep $markerScript @{} 'MISSING_MARKER' } 'Missing completion marker was accepted.'
    'Write-Host "HOST_MARKER"; throw "Failure after marker"' | Set-Content -LiteralPath $markerScript -Encoding UTF8
    Assert-Rejected { Invoke-InstallationStep $markerScript @{} 'HOST_MARKER' } 'Exception after completion marker was swallowed.'
} finally {
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $tempRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $tempRoot) -notlike 'subcontracting-ops-*') { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
Write-Output 'SUBCONTRACTING_OPERATIONS_TESTS_PASSED'
