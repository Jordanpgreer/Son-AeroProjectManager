$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Run these tests in Windows PowerShell 5.1.' }
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $repoRoot 'deployment\Install-ArdaWithSubcontracting.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors.Message -join '; ') }
$source = Get-Content -LiteralPath $scriptPath -Raw
$utf8 = New-Object Text.UTF8Encoding($false)
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('arda-installer-tests-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testRoot)

function Assert([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function MustFail([scriptblock]$Action, [string]$Expected) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Assert ($message -like "*$Expected*") "Expected failure '$Expected'; got '$message'."
}
# Extract pure helper definitions only. Never execute the installer in tests.
foreach ($name in @('FullPath','AssertPlainPath','ReadJson','WriteNewText','WriteNewJson','Native','InvokeHub','AuditPackage','AssertEnvironmentValues','Request','Health','Identity','ProtectDirectory','ConfirmReleaseSource')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    Assert ($null -ne $definition) "Missing helper $name"
    Invoke-Expression $definition.Extent.Text
}
try {
    $commit = '721585513a5333bb1069e70883e7b4b6e4e23706'
    $script:sourceCase = 'success'
    $script:fetchCalls = 0
    $script:fsckCalls = 0
    function Start-Sleep { param($Seconds) }
    function Git([string[]]$Arguments) {
        if ($Arguments -contains 'fetch') {
            $script:fetchCalls++
            Assert ($Arguments -contains '--ipv4') 'Fetch must use the reviewed IPv4 retry path.'
            if ($script:sourceCase -eq 'auth') { throw 'git.exe exited 128. Authentication failed.' }
            if ($script:sourceCase -eq 'tls') { throw 'SSL certificate problem; Failed to connect.' }
            if ($script:sourceCase -ne 'success') { throw 'git.exe exited 128. Failed to connect to github.com:443 after 22810 ms: Could not connect to server' }
            return
        }
        if ($Arguments[0] -eq 'rev-parse' -and $Arguments[-1] -eq 'origin/main') {
            if ($script:sourceCase -eq 'wrong-ref') { return ('0' * 40) }
            return $commit
        }
        if ($Arguments[0] -eq 'cat-file') {
            if ($script:sourceCase -eq 'missing-commit') { throw 'Missing cached commit.' }
            return
        }
        if ($Arguments[0] -eq 'rev-parse' -and $Arguments[-1] -eq ($commit + '^{tree}')) {
            if ($script:sourceCase -eq 'wrong-tree') { return ('1' * 40) }
            return '56941f4d1ee001a3ad78ab8a49aad6466431139a'
        }
        if ($Arguments[0] -eq 'fsck') {
            $script:fsckCalls++
            if ($script:sourceCase -eq 'missing-objects') { throw 'Missing cached source objects.' }
            return
        }
        throw ('Unexpected Git operation in source regression test: ' + ($Arguments -join ' '))
    }
    Assert ((ConfirmReleaseSource) -ceq 'GitHubFetch') 'Successful fetch was not recorded.'
    Assert ($script:fetchCalls -eq 1 -and $script:fsckCalls -eq 0) 'Successful fetch ran unnecessary retries.'
    $script:sourceCase = 'network'; $script:fetchCalls = 0
    Assert ((ConfirmReleaseSource) -ceq 'PreviouslyVerifiedLocalCommit') 'Exact cached source was not accepted after connectivity failure.'
    Assert ($script:fetchCalls -eq 2 -and $script:fsckCalls -eq 1) 'Network retries or cached-object verification were not enforced.'
    foreach ($case in @('auth','tls','wrong-ref','missing-commit','wrong-tree','missing-objects')) {
        $script:sourceCase = $case; $script:fetchCalls = 0
        $failure = ''
        try { [void](ConfirmReleaseSource) } catch { $failure = $_.Exception.Message }
        Assert (-not [string]::IsNullOrWhiteSpace($failure)) "Unsafe source case was accepted: $case"
        if ($case -in @('auth','tls')) { Assert ($script:fetchCalls -eq 1) 'Authentication or TLS failure was retried or treated as an offline source exception.' }
    }
    Remove-Item Function:\Git
    Remove-Item Function:\Start-Sleep
    # Exercise the installer's actual setters: PS 5.1 treats SqlConnectionStringBuilder
    # property assignments as dictionary keys. CLR names such as ConnectTimeout and
    # InitialCatalog are not valid connection-string keywords and fail before SQL opens.
    Add-Type -AssemblyName System.Data
    $roleConnection = 'Server=tcp:SON-SQL2,1433;Database=ProjectTracker;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
    $sqlBuilder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($roleConnection)
    $sqlSetters = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -match '^\$sqlBuilder(\.|\[)'
    }, $true))
    Assert ($sqlSetters.Count -eq 3) 'Unexpected number of SQL connection builder setters; review each setter.'
    foreach ($setter in $sqlSetters) {
        Invoke-Expression $setter.Extent.Text
        if ($setter.Extent.Text.Contains("'master'")) {
            Assert ($sqlBuilder.InitialCatalog -ceq 'master') 'Master preflight connection does not target master.'
        }
    }
    Assert ($sqlBuilder.ConnectTimeout -eq 15) 'SQL connection timeout was not set.'
    Assert ($sqlBuilder.InitialCatalog -ceq 'SmallBusinessSubcontracting') 'Module connection does not target its separate database.'
    Assert ($sqlBuilder.DataSource -ceq 'tcp:SON-SQL2,1433') 'SQL server changed during connection preparation.'
    Assert ($sqlBuilder.IntegratedSecurity -and $sqlBuilder.Encrypt -and $sqlBuilder.TrustServerCertificate) 'Connection security options were not preserved.'
    Assert ((New-Object System.Data.SqlClient.SqlConnectionStringBuilder($roleConnection)).InitialCatalog -ceq 'ProjectTracker') 'Original role-store connection was changed.'
    Assert ((FullPath 'C:/SonAero/src/SonAeroInternalHub/') -ieq (FullPath 'C:\SonAero\src\SonAeroInternalHub')) 'Git path slash normalization failed.'
    $marker = Join-Path $testRoot 'marker.json'
    WriteNewJson $marker ([pscustomobject]@{ Status='Test'; Number=7 })
    Assert ((ReadJson $marker).Number -eq 7) 'PS 5.1 JSON object recognition failed.'
    $beforeHash = (Get-FileHash -LiteralPath $marker).Hash
    MustFail { WriteNewJson $marker ([pscustomobject]@{ Number=8 }) } 'already exists'
    Assert ((Get-FileHash -LiteralPath $marker).Hash -ceq $beforeHash) 'Exclusive marker writer overwrote evidence.'
    $arrayPath = Join-Path $testRoot 'array.json'
    WriteNewText $arrayPath '[{"Number":7}]'
    MustFail { ReadJson $arrayPath } 'JSON root must be an object'
    $nativeOutput = Native "$env:SystemRoot\System32\cmd.exe" @('/d','/c','echo stderr-warning 1>&2 & exit /b 0')
    Assert (($nativeOutput -join ' ') -like '*stderr-warning*') 'Native stderr was not retained.'
    Assert ($ErrorActionPreference -eq 'Stop') 'Native helper leaked Continue preference.'
    MustFail { Native "$env:SystemRoot\System32\cmd.exe" @('/d','/c','exit /b 7') } 'exited 7'
    $deployScript = Join-Path $testRoot 'fake-deploy.ps1'
    WriteNewText $deployScript @'
[CmdletBinding(SupportsShouldProcess)]param()
[pscustomobject]@{Status='internal formatting'} | Format-List
if ($WhatIfPreference) { 'WHATIF_READY' } else { 'HUB_RELEASE_DEPLOYED_AND_HEALTHY' }
'@
    InvokeHub @{} -Preview
    InvokeHub @{}
    $deployScript = Join-Path $testRoot 'missing-marker.ps1'
    WriteNewText $deployScript '[CmdletBinding(SupportsShouldProcess)]param(); "no success marker"'
    MustFail { InvokeHub @{} } 'Required deployment marker missing'
    $cleanPackage = Join-Path $testRoot 'clean'
    [void](New-Item -ItemType Directory -Path $cleanPackage)
    WriteNewText (Join-Path $cleanPackage 'appsettings.Development.json') '{}'
    AuditPackage $cleanPackage
    WriteNewText (Join-Path $cleanPackage 'live.db-wal') 'data must never enter a release'
    MustFail { AuditPackage $cleanPackage } 'Data or secrets in publication'
    AssertEnvironmentValues @([pscustomobject]@{ Name='ASPNETCORE_ENVIRONMENT'; Value='Production' }) -NewModule
    MustFail { AssertEnvironmentValues @([pscustomobject]@{ Name='ConnectionStrings__SubcontractingStore'; Value='wrong database' }) -NewModule } 'protected configuration override'
    MustFail { AssertEnvironmentValues @([pscustomobject]@{ Name='VendorDocumentStorage:RootPath'; Value='C:\bad' }) -NewModule } 'protected configuration override'
    MustFail { AssertEnvironmentValues @([pscustomobject]@{ Name='QualityIntegration__Enabled'; Value='true' }) } 'not false'
    MustFail { AssertEnvironmentValues @([pscustomobject]@{ Name='DOTNET_ENVIRONMENT'; Value='Development' }) -NewModule } 'Non-Production'
    $account = 'SON4L\JoshGreer'
    function Invoke-WebRequest {
        param($Uri, [switch]$UseBasicParsing, [switch]$UseDefaultCredentials, $TimeoutSec, $MaximumRedirection)
        Assert ([bool]$UseDefaultCredentials) 'Request did not use signed-in Windows credentials.'
        return [pscustomobject]@{ StatusCode=200; Content='{"accountName":"SON4L\\UnexpectedUser"}' }
    }
    MustFail { Identity 'https://unit-test.invalid' } 'Identity mismatch'
    Remove-Item Function:\Invoke-WebRequest

    # DirectorySecurity construction must work in PS 5.1 before an apply reaches it.
    # Do not apply a restrictive ACL to the test runner's temp files.
    function Set-Acl { param($LiteralPath, $AclObject) $script:testAcl = $AclObject }
    $protectedTest = Join-Path $testRoot 'protected'
    ProtectDirectory $protectedTest
    Assert $script:testAcl.AreAccessRulesProtected 'New state directory ACL inherits broader access.'
    Assert ($script:testAcl.Access.Count -eq 2) 'New state directory ACL must contain SYSTEM and Administrators only.'
    Remove-Item Function:\Set-Acl

    # Verify the actual six-entry application base can be merged with a legacy production catalog.
    Import-Module (Join-Path $repoRoot 'deployment\PortalApplicationCatalog.psm1') -Force
    $candidate = Join-Path $testRoot 'Portal'
    [void](New-Item -ItemType Directory -Path $candidate)
    Copy-Item -LiteralPath (Join-Path $repoRoot 'apps\portal\src\Portal.Api\appsettings.json') -Destination $candidate
    $template = ReadJson (Join-Path $repoRoot 'deployment\templates\portal.appsettings.Production.json')
    $originalConnection = $template.ConnectionStrings.RoleStore
    $legacyProduction = ($template | ConvertTo-Json -Depth 100) | ConvertFrom-Json
    $legacyProduction.Portal.Applications = @($legacyProduction.Portal.Applications | Where-Object Id -ne 'small-business-subcontracting')
    WriteNewJson (Join-Path $candidate 'appsettings.Production.json') $legacyProduction
    $templateFile = Join-Path $testRoot 'template.json'
    WriteNewJson $templateFile $template
    [void](Sync-PortalProductionApplicationCatalog -CandidatePortalPath $candidate -ProductionTemplatePath $templateFile)
    $merged = ReadJson (Join-Path $candidate 'appsettings.Production.json')
    Assert ($merged.ConnectionStrings.RoleStore -ceq $originalConnection) 'Portal SQL settings changed.'
    $ids = @($merged.Portal.Applications | ForEach-Object Id)
    Assert (($ids -join ',') -eq 'project-tracker,engineering-hub,estimating-dashboard,quality-assurance,small-business-subcontracting,admin-console') 'Catalog array ordering would break ASP.NET configuration overlay.'
    Assert (@($merged.Portal.Applications | Where-Object { $_.Id -eq 'small-business-subcontracting' -and $_.Url -eq 'https://subcontracting.hub.son4l.local' }).Count -eq 1) 'New-module URL missing.'

    # Protect apply ordering and refusal of unknown pre-existing stores.
    $journalPosition = $source.IndexOf('WriteNewJson $startMarker $record')
    $backupPosition = $source.IndexOf("`$attestation.Trim() -cne 'BACKUPS_VERIFIED'")
    $databasePosition = $source.IndexOf('CREATE DATABASE [SmallBusinessSubcontracting]')
    $hubApplyPosition = $source.IndexOf('InvokeHub $deployArgs' + "`n" + '        $hubVerified = $true')
    Assert ($backupPosition -gt 0 -and $journalPosition -gt $backupPosition -and $databasePosition -gt $journalPosition) 'SQL changes can occur before backup/journal gates.'
    Assert ($hubApplyPosition -gt $source.IndexOf('if ([int]$tableCount -ne 5)')) 'Existing Hub changes can precede new-module health/schema validation.'
    foreach ($guard in @('First-install script will not reuse or replace it', 'New module site or pool already exists', 'New-module document folder already exists', 'POSTCHECK_STOP_AFTER_VERIFIED_APPLY', 'APPLY_NOT_VERIFIED')) {
        Assert ($source.Contains($guard)) "Missing preservation guard: $guard"
    }
    Assert (-not ($source -match '(?i)DROP\s+DATABASE|TRUNCATE\s+TABLE|iisreset|git reset|Get-Credential|\$_\s*\|\s*Out-Host')) 'Destructive operation, password prompt or unsafe output handling found.'
    'SUBCONTRACTING_INSTALLER_TESTS_PASSED'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $allowed = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\arda-installer-tests-'
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
