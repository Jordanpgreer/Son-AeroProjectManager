$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$modulePath = Join-Path $repoRoot 'deployment\PortalApplicationCatalog.psm1'
Import-Module $modulePath -Force

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("portal-catalog-test-{0}" -f [Guid]::NewGuid().ToString('N'))
try {
    $candidate = Join-Path $testRoot 'Portal'
    New-Item -ItemType Directory -Path $candidate -Force | Out-Null

    @'
{
  "Portal": {
    "Applications": [
      { "Id": "project-tracker", "Name": "Project Tracker", "Url": "http://localhost:5135" },
      { "Id": "engineering-hub", "Name": "Engineering Hub", "Url": "http://localhost:5150" },
      { "Id": "estimating-dashboard", "Name": "Estimating Dashboard", "Url": "http://localhost:5160" },
      { "Id": "quality-assurance", "Name": "Quality Assurance", "Url": "http://localhost:5170" },
      { "Id": "admin-console", "Name": "Admin Console", "Url": "/#/admin/access" }
    ]
  }
}
'@ | Set-Content -LiteralPath (Join-Path $candidate 'appsettings.json') -Encoding UTF8

    @'
{
  "Portal": {
    "Admins": [ "SON4L\\jordan.greer" ],
    "Applications": [
      { "Id": "project-tracker", "Name": "Project Tracker", "Url": "http://SON-IIS2:5135", "AllowedRoles": [ "Viewer" ] },
      { "Id": "engineering-hub", "Name": "Engineering Hub", "Url": "http://SON-IIS2:5150", "AllowedRoles": [], "ServerNote": "retain me" },
      { "Id": "estimating-dashboard", "Name": "Estimating Dashboard", "Url": "http://SON-IIS2:5160" },
      { "Id": "admin-console", "Name": "Admin Console", "Url": "/#/admin/access" },
      { "Id": "custom-tool", "Name": "Custom Tool", "Url": "http://SON-IIS2:5180", "AllowedRoles": [ "Admin" ] }
    ]
  }
}
'@ | Set-Content -LiteralPath (Join-Path $candidate 'appsettings.Production.json') -Encoding UTF8

    $template = Join-Path $testRoot 'portal.appsettings.Production.json'
    @'
{
  "Portal": {
    "Applications": [
      { "Id": "project-tracker", "Name": "Project Tracker", "Url": "https://projects.hub.son4l.local", "AllowedRoles": [] },
      { "Id": "engineering-hub", "Name": "Engineering Hub", "Url": "https://engineering.hub.son4l.local", "AllowedRoles": [ "__production-disabled__" ] },
      { "Id": "estimating-dashboard", "Name": "Estimating Dashboard", "Url": "http://SON-IIS2:5160" },
      { "Id": "quality-assurance", "Name": "Quality Assurance", "Url": "http://SON-IIS2:5170", "AllowedRoles": [ "__production-disabled__" ] },
      { "Id": "admin-console", "Name": "Admin Console", "Url": "/#/admin/access" }
    ]
  }
}
'@ | Set-Content -LiteralPath $template -Encoding UTF8

    $result = Sync-PortalProductionApplicationCatalog `
        -CandidatePortalPath $candidate `
        -ProductionTemplatePath $template
    $updated = Get-Content -LiteralPath (Join-Path $candidate 'appsettings.Production.json') -Raw |
        ConvertFrom-Json
    $ids = @($updated.Portal.Applications | ForEach-Object Id)
    $expected = @(
        'project-tracker',
        'engineering-hub',
        'estimating-dashboard',
        'quality-assurance',
        'admin-console',
        'custom-tool'
    )
    if (($ids -join '|') -ne ($expected -join '|')) {
        throw "Unexpected merged application order: $($ids -join ', ')"
    }
    if (@($ids | Where-Object { $_ -eq 'admin-console' }).Count -ne 1) {
        throw 'The synchronized catalog did not contain exactly one Admin Console.'
    }
    $quality = @($updated.Portal.Applications | Where-Object Id -eq 'quality-assurance')
    if ($quality.Count -ne 1 -or $quality[0].Url -ne 'http://SON-IIS2:5170') {
        throw 'Quality Assurance was not added from the production template.'
    }
    if ($updated.Portal.Admins[0] -ne 'SON4L\jordan.greer') {
        throw 'Existing production administrator configuration was not preserved.'
    }
    if (($result.AddedApplicationIds -join '|') -ne 'quality-assurance') {
        throw 'The synchronization result did not report the added Quality Assurance entry.'
    }
    $projectTracker = @($updated.Portal.Applications | Where-Object Id -eq 'project-tracker')[0]
    if ((@($projectTracker.AllowedRoles) -join '|') -ne 'Viewer' -or
        $projectTracker.Url -ne 'http://SON-IIS2:5135') {
        throw 'Synchronization changed the non-target Project Tracker role policy or production URL.'
    }
    $engineering = @($updated.Portal.Applications | Where-Object Id -eq 'engineering-hub')[0]
    if ((@($engineering.AllowedRoles) -join '|') -ne '__production-disabled__' -or
        $engineering.Url -ne 'http://SON-IIS2:5150' -or
        $engineering.ServerNote -ne 'retain me') {
        throw 'The template disabled-role policy did not preserve unrelated Engineering production settings.'
    }
    if ((@($quality[0].AllowedRoles) -join '|') -ne '__production-disabled__') {
        throw 'A newly added first-party application did not retain the template AllowedRoles policy.'
    }
    $custom = @($updated.Portal.Applications | Where-Object Id -eq 'custom-tool')[0]
    if ((@($custom.AllowedRoles) -join '|') -ne 'Admin' -or $custom.Url -ne 'http://SON-IIS2:5180') {
        throw 'The synchronization changed a custom application visibility policy or URL.'
    }

    $beforeInvalidPolicyTest = Get-Content -LiteralPath (Join-Path $candidate 'appsettings.Production.json') -Raw
    $invalidTemplate = Get-Content -LiteralPath $template -Raw | ConvertFrom-Json
    $invalidTemplate.Portal.Applications[1].AllowedRoles = '__production-disabled__'
    $invalidTemplate | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $template -Encoding UTF8
    $invalidPolicyFailed = $false
    try {
        Sync-PortalProductionApplicationCatalog `
            -CandidatePortalPath $candidate `
            -ProductionTemplatePath $template | Out-Null
    }
    catch {
        $invalidPolicyFailed = $_.Exception.Message -match 'AllowedRoles.*must be a JSON array'
    }
    if (-not $invalidPolicyFailed) {
        throw 'A scalar first-party AllowedRoles deployment policy was not rejected.'
    }
    $afterInvalidPolicyTest = Get-Content -LiteralPath (Join-Path $candidate 'appsettings.Production.json') -Raw
    if ($afterInvalidPolicyTest -cne $beforeInvalidPolicyTest) {
        throw 'Invalid template policy validation changed the production configuration.'
    }

    # Restore the valid template before testing carried-forward duplicate rejection.
    $invalidTemplate.Portal.Applications[1].AllowedRoles = @('__production-disabled__')
    $invalidTemplate | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $template -Encoding UTF8

    $beforeDuplicateTest = Get-Content -LiteralPath (Join-Path $candidate 'appsettings.Production.json') -Raw
    $duplicate = $beforeDuplicateTest | ConvertFrom-Json
    $duplicate.Portal.Applications += $duplicate.Portal.Applications[4]
    $duplicate | ConvertTo-Json -Depth 100 |
        Set-Content -LiteralPath (Join-Path $candidate 'appsettings.Production.json') -Encoding UTF8
    $duplicateFailed = $false
    try {
        Sync-PortalProductionApplicationCatalog `
            -CandidatePortalPath $candidate `
            -ProductionTemplatePath $template | Out-Null
    }
    catch {
        $duplicateFailed = $_.Exception.Message -match 'more than one.*admin-console'
    }
    if (-not $duplicateFailed) {
        throw 'A duplicate application Id was not rejected.'
    }

    Write-Output 'PORTAL_APPLICATION_CATALOG_TESTS_PASSED'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
