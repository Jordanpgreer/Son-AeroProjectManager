[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Use Windows PowerShell 5.1.' }
$deployment = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\deployment'))
$files = @('Deploy-SmallBusinessSubcontractingRelease.ps1','SubcontractingProductionConfiguration.psm1',
    'SubcontractingReleaseFiles.psm1','SubcontractingReleaseIis.psm1','SubcontractingReleaseTransaction.psm1')
foreach ($file in $files) {
    $tokens=$null; $errors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $deployment $file),[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw "$file parser errors: $($errors.Message -join '; ')" }
    if (@(Get-Content -LiteralPath (Join-Path $deployment $file)).Count -ge 500) { throw "$file exceeds repository size limit." }
}
Import-Module (Join-Path $deployment 'SubcontractingProductionConfiguration.psm1') -Force
Import-Module (Join-Path $deployment 'SubcontractingReleaseFiles.psm1') -Force
Import-Module (Join-Path $deployment 'SubcontractingReleaseIis.psm1') -Force
Import-Module (Join-Path $deployment 'SubcontractingReleaseTransaction.psm1') -Force
$script:checks=0
function Assert-True { param([bool]$Condition,[string]$Label) if(-not $Condition){throw "FAIL: $Label"}; $script:checks++ }
function Assert-Throws { param([scriptblock]$Action,[string]$Label) $thrown=$false; try{& $Action}catch{$thrown=$true}; Assert-True $thrown $Label }
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('subcontracting-release-tests-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporary
try {
    $configPath=Join-Path $temporary 'Production.json'
    $template=Join-Path $deployment 'templates\small-business-subcontracting.appsettings.Production.json'
    $original=Get-Content -LiteralPath $template -Raw
    Set-Content -LiteralPath $configPath -Value $original
    $config=Read-SubcontractingProductionConfiguration $configPath
    Assert-True ($config.Authentication.Mode -eq 'Windows') 'valid production config'
    foreach($case in @(
        @{ Old='"Windows"'; New='"Development"'; Label='development auth' },
        @{ Old='tcp:SON-SQL2,1433'; New='tcp:OTHER,1433'; Label='wrong SQL server' },
        @{ Old='Integrated Security=True'; New='User ID=bad'; Label='SQL login credentials' },
        @{ Old='"SqlServer"'; New='"Sqlite"'; Label='SQLite provider' },
        @{ Old='"RequireUncPath": true'; New='"RequireUncPath": false'; Label='local storage fallback' },
        @{ Old='subcontracting.hub.son4l.local'; New='subcontracting.son4l.local'; Label='wrong AllowedHosts' }
    )) {
        Assert-True ($original.Contains($case.Old)) "test input $($case.Label) exists"
        Set-Content -LiteralPath $configPath -Value $original.Replace($case.Old,$case.New)
        Assert-Throws { Read-SubcontractingProductionConfiguration $configPath } $case.Label
    }
    Set-Content -LiteralPath $configPath -Value $original
    Assert-Throws { Read-SubcontractingProductionConfiguration $configPath -ApprovedDocumentRoot '\\SON-SQL2\Other$\Documents' } 'wrong approved UNC'
    foreach($path in @('C:\data','\\SON-SQL2\share\..\escape','\\SON-SQL2\share\foo/escape','\\SON-SQL2\share\bad.','\\SON-SQL2\<approved-share>\Documents')) {
        Assert-Throws { Assert-SubcontractingDocumentRoot $path } "unsafe storage $path"
    }
    foreach($name in @('AllowedHosts','Authentication__Mode','ConnectionStrings:RoleStore','CUSTOMCONNSTR_SubcontractingStore','VendorDocumentStorage__RootPath')) {
        Assert-Throws { Assert-SubcontractingEnvironmentVariable -Name $name -Value 'bad' } "override $name"
    }
    Assert-Throws { Assert-SubcontractingEnvironmentVariable -Name DOTNET_ENVIRONMENT -Value Development } 'nonProduction environment'
    Assert-SubcontractingEnvironmentVariable -Name ASPNETCORE_ENVIRONMENT -Value Production
    $healthy=[pscustomobject]@{status='ok'; databaseProvider='SqlServer'; migrations='ready'; roleStore='ready'; documentStorage='ready'}
    Assert-SubcontractingHealthBody $healthy
    foreach($property in @('status','databaseProvider','migrations','roleStore','documentStorage')) {
        $bad=$healthy | ConvertTo-Json | ConvertFrom-Json
        $bad.$property='bad'
        Assert-Throws { Assert-SubcontractingHealthBody $bad } "health checks $property"
    }
    $existingBinding=[pscustomobject]@{Site='SonAeroPortal';Protocol='https';BindingInformation='*:443:hub.son4l.local';
        SslFlags=1;CertificateHash=('A'*40);CertificateStoreName='My'}
    Assert-SubcontractingBindingConflicts -Bindings @($existingBinding) -Thumbprint ('A'*40) -FirstInstall $true
    foreach($conflict in @(
        [pscustomobject]@{Site='Other';Protocol='http';BindingInformation='*:5180:';SslFlags=0},
        [pscustomobject]@{Site='Other';Protocol='https';BindingInformation='*:443:subcontracting.hub.son4l.local';SslFlags=1;CertificateHash=('A'*40);CertificateStoreName='My'},
        [pscustomobject]@{Site='Other';Protocol='https';BindingInformation='*:443:';SslFlags=0}
    )) {
        Assert-Throws { Assert-SubcontractingBindingConflicts -Bindings @($existingBinding,$conflict) -Thumbprint ('A'*40) -FirstInstall $true } 'IIS hostname/port/nonSNI conflict'
    }
    Assert-Throws { Get-SubcontractingFullPath 'C:\staging\..\escape' } 'path traversal'
    Assert-Throws { Get-SubcontractingFullPath 'C:\release\file:stream' } 'alternate data stream'
    Assert-True (Test-SubcontractingPathOverlap 'C:\folder' 'C:\folder\child') 'containment overlap'
    Assert-True (-not (Test-SubcontractingPathOverlap 'C:\folder' 'C:\folder-other')) 'sibling containment'
    $stateRoot=Join-Path $temporary 'protected-state'
    $null=New-Item -ItemType Directory -Path $stateRoot
    $journal=[pscustomobject]@{Version=1;Id=[guid]::NewGuid().ToString('N');ReleaseId='test';FirstInstall=$true;
        Candidate='C:\SonAero\releases\small-business-subcontracting\test';Phase='Preparing';Thumbprint=('A'*40)}
    # Run the actual file/flush/replace path under WinPS5.1. ACL application requires
    # elevation on SON-IIS2, so only those boundaries are stubbed for an ordinary dev user.
    & (Get-Module SubcontractingReleaseFiles) {
        param($directory,$value)
        $originalAcl=(Get-Item Function:Assert-SubcontractingStateAcl).ScriptBlock
        function Assert-SubcontractingStateAcl { param($Path) }
        function Set-Acl { param($LiteralPath,$AclObject) }
        try {
            Write-SubcontractingTransaction $directory $value
            $value.Phase='Switching'
            Write-SubcontractingTransaction $directory $value
        }
        finally {
            Set-Item Function:Assert-SubcontractingStateAcl $originalAcl
            Remove-Item Function:Set-Acl
        }
    } $stateRoot $journal
    Assert-True ((Get-Content -LiteralPath (Join-Path $stateRoot 'transaction.json') -Raw | ConvertFrom-Json).Phase -eq 'Switching') 'real journal atomic second write'
    Assert-True (@(Get-ChildItem -LiteralPath $stateRoot -Filter '*.previous.json').Count -eq 1) 'previous journal evidence retained'
    $package=Join-Path $temporary 'package'; $copy=Join-Path $temporary 'copy'
    $null=New-Item -ItemType Directory -Path (Join-Path $package 'wwwroot') -Force
    foreach($path in @('SmallBusinessSubcontracting.Api.dll','web.config','appsettings.json','wwwroot\index.html')) { Set-Content -LiteralPath (Join-Path $package $path) -Value 'content' }
    Set-Content -LiteralPath (Join-Path $package 'appsettings.Development.json') -Value '{"secret":"test"}'
    $manifest=@(Get-SubcontractingPackageManifest $package)
    Assert-True ($manifest.Count -eq 4) 'development configuration excluded'
    $null=New-Item -ItemType Directory -Path $copy
    Copy-SubcontractingPackage $package $copy $manifest
    Assert-True (-not (Test-Path (Join-Path $copy 'appsettings.Development.json'))) 'copy sanitized'
    Set-Content -LiteralPath (Join-Path $package 'runtime.db') -Value 'runtime data'
    Assert-Throws { Get-SubcontractingPackageManifest $package } 'database in package rejected'
    Remove-Item -LiteralPath (Join-Path $package 'runtime.db')
    Set-Content -LiteralPath (Join-Path $package 'SmallBusinessSubcontracting.Api.dll') -Value 'changed'
    Assert-Throws { Copy-SubcontractingPackage $package $copy $manifest } 'package mutation after preview rejected'

    # Execute the actual transaction coordinator with injected operation boundaries. Cover
    # first install, upgrade, health failure, preparation failure, and failed rollback.
    foreach($first in @($true,$false)) {
        foreach($failure in @('none','Prepare','Switch','Health','Verify','Restore')) {
            $events=New-Object Collections.Generic.List[string]
            $state=[pscustomobject]@{Id='test';FirstInstall=$first;Phase='Preparing'}
            $actions=@{
                Persist={param($s) $events.Add('Persist:'+$s.Phase)}
                Prepare={param($s) $events.Add('Prepare');if($failure -eq 'Prepare'){throw 'copy failed'}}
                Switch={param($s) $events.Add('Switch');if($failure -eq 'Switch'){throw 'switch failed'}}
                Health={param($s) $events.Add('Health');if($failure -in @('Health','Restore')){throw 'health failed'}}
                Verify={param($s) $events.Add('Verify');if($failure -eq 'Verify'){throw 'ownership changed'}}
                Restore={param($s) $events.Add('Restore');if($failure -eq 'Restore'){throw 'rollback failed'}}
            }
            if($failure -eq 'none') {
                Invoke-SubcontractingReleaseTransaction $state $actions
                Assert-True ($state.Phase -eq 'Healthy') "success first=$first"
                Assert-True (($events -join ',') -eq 'Persist:Preparing,Prepare,Persist:Switching,Switch,Health,Verify,Persist:Healthy') 'success ordering'
            }
            else {
                Assert-Throws { Invoke-SubcontractingReleaseTransaction $state $actions } "transaction failure $failure first=$first"
                $expected=if($failure -eq 'Restore'){'RollbackFailed'}else{'RolledBack'}
                Assert-True ($state.Phase -eq $expected) "durable failure phase $failure"
                Assert-True ($events.Contains('Restore')) 'rollback executes'
                Assert-True (-not $events.Contains('Persist:Healthy')) 'failed release never marked healthy'
            }
        }
    }
    # Exercise the real recovery planner with IIS/HTTP boundaries mocked. An operator
    # changing ownership or the prior Production file must prevent rollback mutations.
    & (Get-Module SubcontractingReleaseTransaction) {
        param($assert,$throws)
        $script:recoveryEvents=New-Object Collections.Generic.List[string]
        $script:boundary=[pscustomobject]@{Path='C:\release\new';SiteId=12;SiteState='Started';PoolState='Started'}
        $script:recoveryHash='approved'
        function Get-SubcontractingIisBoundary { param($Thumbprint,[switch]$SkipEnvironment) $script:boundary }
        function Get-FileHash { param($LiteralPath,$Algorithm) [pscustomobject]@{Hash=$script:recoveryHash} }
        function Set-SubcontractingRuntimeState { param($State) $script:recoveryEvents.Add($State) }
        function Set-SubcontractingPhysicalPath { param($Expected,$Destination) $script:recoveryEvents.Add('Path:'+ $Destination) }
        function Wait-SubcontractingHealth { param($TimeoutSeconds) $script:recoveryEvents.Add('Health') }
        function Remove-SubcontractingOwnedSite { param($State) $script:recoveryEvents.Add('RemoveOwned') }
        try {
            $state=[pscustomobject]@{FirstInstall=$false;Thumbprint=('A'*40);Candidate='C:\release\new';ConfigurationHash='approved';
                Prior=[pscustomobject]@{Path='C:\release\old';SiteId=12}}
            Restore-SubcontractingTransaction $state
            & $assert (($script:recoveryEvents -join ',') -eq 'Stopped,Path:C:\release\old,Started,Health') 'recovery restores old path before health'
            $script:recoveryEvents.Clear()
            $script:boundary.Path='C:\release\someone-else'
            & $throws { Restore-SubcontractingTransaction $state } 'recovery rejects foreign path'
            & $assert ($script:recoveryEvents.Count -eq 0) 'foreign path no mutation'
            $script:boundary.Path=$state.Candidate
            $script:boundary.SiteId=99
            & $throws { Restore-SubcontractingTransaction $state } 'recovery rejects foreign site ID'
            & $assert ($script:recoveryEvents.Count -eq 0) 'foreign ID no mutation'
            $script:boundary.SiteId=12; $script:recoveryHash='changed'
            & $throws { Restore-SubcontractingTransaction $state } 'recovery rejects changed prior config'
            & $assert ($script:recoveryEvents.Count -eq 0) 'changed config no mutation'
            $script:recoveryHash='approved'; $script:boundary.Path=$state.Prior.Path
            Restore-SubcontractingTransaction $state
            & $assert (($script:recoveryEvents -join ',') -eq 'Health') 'preparation failure avoids existing runtime restart'
            $script:recoveryEvents.Clear(); $state.FirstInstall=$true
            Restore-SubcontractingTransaction $state
            & $assert (($script:recoveryEvents -join ',') -eq 'RemoveOwned') 'first install recovery removes only owned resources'
        }
        finally {
            foreach($name in @('Get-SubcontractingIisBoundary','Get-FileHash','Set-SubcontractingRuntimeState',
                'Set-SubcontractingPhysicalPath','Wait-SubcontractingHealth','Remove-SubcontractingOwnedSite')) {
                Remove-Item -LiteralPath ("Function:"+$name)
            }
        }
    } ${function:Assert-True} ${function:Assert-Throws}
    $source=Get-Content -LiteralPath (Join-Path $deployment 'Deploy-SmallBusinessSubcontractingRelease.ps1') -Raw
    $gate=$source.IndexOf("if (-not `$PSCmdlet.ShouldProcess(`$candidate")
    Assert-True ($gate -gt 0) 'preview gate present'
    foreach($write in @('Initialize-SubcontractingStateDirectory $stateDirectory','Invoke-SubcontractingReleaseTransaction -State $state')) {
        Assert-True ($source.IndexOf($write) -gt $gate) "mutation follows preview: $write"
    }
    foreach($guard in @('Assert-HubComputerName -ExpectedComputerName SON-IIS2','Assert-HubAdministrator','-not [Environment]::UserInteractive',
        '[IO.FileShare]::None','-BackupsVerified','RecoverTransactionId','WHATIF_READY_SMALL_BUSINESS_SUBCONTRACTING_RELEASE',
        'SMALL_BUSINESS_SUBCONTRACTING_RELEASE_DEPLOYED_AND_HEALTHY')) {
        Assert-True ($source.Contains($guard)) "entry guard $guard"
    }
    Write-Output "SMALL_BUSINESS_SUBCONTRACTING_RELEASE_TESTS_PASSED ($script:checks assertions; Windows PowerShell 5.1)"
}
finally {
    $resolved=[IO.Path]::GetFullPath($temporary)
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if(-not $resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test cleanup path.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
