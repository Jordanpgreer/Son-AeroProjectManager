#requires -Version 5.1
<#
Run on SON-IIS2 in elevated Windows PowerShell 5.1. Copy this file to the
server; it need not be committed or placed inside the production checkout.
Pinned application source: 721585513a5333bb1069e70883e7b4b6e4e23706.
First installation only. -PrepareOnly builds and previews without applying.
Creates a separate SQL database and document subfolder for the new module.
Existing application migrations still require verified, restorable backups.
Never rerun after APPLY_NOT_VERIFIED or POSTCHECK_STOP_AFTER_VERIFIED_APPLY.
#>
[CmdletBinding()]
param([switch]$PrepareOnly)

& {
    $ErrorActionPreference = 'Stop'
    $commit = '721585513a5333bb1069e70883e7b4b6e4e23706'
    $repo = 'C:\SonAero\src\SonAeroInternalHub'
    $releaseRoot = 'C:\SonAero\releases'
    $packageBase = 'C:\SonAero\packages'
    $stateRoot = 'C:\ProgramData\SonAero\deployment-state\subcontracting-installer'
    $siteName = 'SmallBusinessSubcontracting'
    $moduleUrl = 'https://hub.son4l.local:6180'
    $firewallName = 'Arda-SmallBusinessSubcontracting-HTTPS-6180'
    $computerAccount = 'SON4L\SON-IIS2$'
    $started = $false
    $hubVerified = $false
    $moduleIisAttempted = $false
    $transcriptStarted = $false
    $mutex = $null
    $lockHeld = $false
    $utf8 = New-Object Text.UTF8Encoding($false)
    $folders = [ordered]@{
        ProjectTracker = 'ProjectTracker'; SonAeroPortal = 'Portal'
        EngineeringHub = 'EngineeringHub'; EstimatingDashboard = 'EstimatingDashboard'
        QualityAssurance = 'QualityAssurance'
    }
    $origins = @(
        'https://hub.son4l.local', 'https://projects.hub.son4l.local',
        'https://engineering.hub.son4l.local', 'https://estimating.hub.son4l.local',
        'https://quality.hub.son4l.local', 'https://hub.son4l.local/project-tracker-api'
    )

    function FullPath([string]$Path) {
        return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path)).TrimEnd('\')
    }
    function AssertPlainPath([string]$Path) {
        $cursor = FullPath $Path
        while ($cursor) {
            if (Test-Path -LiteralPath $cursor) {
                $item = Get-Item -LiteralPath $cursor -Force
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Reparse point is not allowed: $cursor"
                }
            }
            $parent = Split-Path -Parent $cursor
            if ($parent -eq $cursor) { break }
            $cursor = $parent
        }
    }
    function ReadJson([string]$Path) {
        $json = Get-Content -LiteralPath $Path -Raw
        $value = $json | ConvertFrom-Json
        if (-not $json.TrimStart().StartsWith('{') -or $value -isnot [System.Management.Automation.PSCustomObject]) {
            throw "JSON root must be an object: $Path"
        }
        return $value
    }
    function WriteNewText([string]$Path, [string]$Value) {
        $bytes = $utf8.GetBytes($Value)
        $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
    }
    function WriteNewJson([string]$Path, $Value) {
        WriteNewText $Path ($Value | ConvertTo-Json -Depth 100)
        [void](ReadJson $Path)
    }
    function Native([string]$Program, [string[]]$Arguments) {
        # PS 5.1 wraps native stderr as NativeCommandError even on successful Git fetch.
        # Only this local function scope relaxes preference; exit code remains mandatory.
        $ErrorActionPreference = 'Continue'
        $output = @(& $Program @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
        $lines = @($output | ForEach-Object { [string]$_ })
        if ($exitCode -ne 0) { throw "$Program exited $exitCode. $($lines -join [Environment]::NewLine)" }
        return $lines
    }
    function Git([string[]]$Arguments) {
        return Native 'git.exe' (@('-C', $repo) + $Arguments)
    }
    function ConfirmReleaseSource {
        # This exact commit was independently verified on GitHub and was already
        # fetched by the operator's earlier SOURCE_COMMIT_VERIFIED attempt.
        $expectedTree = '56941f4d1ee001a3ad78ab8a49aad6466431139a'
        $fetched = $false
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            try {
                Write-Host "GITHUB_FETCH_ATTEMPT $attempt/2 (IPv4)"
                Git @('-c', 'http.lowSpeedLimit=1', '-c', 'http.lowSpeedTime=20', 'fetch', '--ipv4', 'origin', 'main') | ForEach-Object { Write-Host $_ }
                $fetched = $true
                break
            }
            catch {
                $message = $_.Exception.Message
                if ($message -match '(?i)certificate|SSL|TLS|authentication|401|403|repository not found' -or
                    $message -notmatch '(?i)Failed to connect|Could not connect to server|Could not resolve (host|proxy)|Connection timed out|Connection reset|Operation timed out|Recv failure') {
                    throw
                }
                Write-Warning "GitHub connection attempt $attempt failed. Checking only the previously reviewed release is permitted."
                if ($attempt -lt 2) { Start-Sleep -Seconds 3 }
            }
        }
        # These checks apply even after a successful fetch. Never substitute another
        # branch, an incomplete download, a different tree, or a working-directory copy.
        if (((Git @('rev-parse', '--verify', 'origin/main')) -join '').Trim() -cne $commit) {
            throw 'SOURCE_NOT_VERIFIED: origin/main is not the exact reviewed release. Restore GitHub connectivity before continuing.'
        }
        [void](Git @('cat-file', '-e', ($commit + '^{commit}')))
        if (((Git @('rev-parse', '--verify', ($commit + '^{tree}'))) -join '').Trim() -cne $expectedTree) {
            throw 'SOURCE_NOT_VERIFIED: the cached source tree does not match the independently reviewed release.'
        }
        if ($fetched) { return 'GitHubFetch' }
        # Validate object connectivity before git archive later reads the source.
        Git @('fsck', '--connectivity-only', '--no-reflogs', '--no-dangling', $commit) | ForEach-Object { Write-Host $_ }
        Write-Warning "GITHUB_UNREACHABLE_USING_VERIFIED_LOCAL_SOURCE $commit. npm/NuGet downloads may still require network access."
        return 'PreviouslyVerifiedLocalCommit'
    }
    function Request([string]$Uri, [int[]]$Allowed = @(200)) {
        try {
            $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -UseDefaultCredentials -TimeoutSec 30 -MaximumRedirection 0
            $status = [int]$response.StatusCode
            $content = [string]$response.Content
        }
        catch {
            if ($null -eq $_.Exception.Response) { throw }
            $status = [int]$_.Exception.Response.StatusCode
            $content = ''
            if ($Allowed -notcontains $status) { throw "HTTP $status from $Uri. Windows identity or application access must be corrected; no credentials are substituted." }
        }
        if ($Allowed -notcontains $status) { throw "Unexpected HTTP $status from $Uri" }
        return [pscustomobject]@{ Status = $status; Content = $content }
    }
    function Health([string]$Origin) {
        $response = Request "$Origin/api/health"
        $health = $response.Content | ConvertFrom-Json
        if ([string]$health.status -ine 'ok') { throw "Health payload was not ok: $Origin" }
    }
    function Identity([string]$Origin, [int[]]$Allowed = @(200)) {
        $response = Request "$Origin/api/me" $Allowed
        if ($response.Status -eq 200) {
            $me = $response.Content | ConvertFrom-Json
            if ([string]$me.accountName -ine $account) {
                throw "Identity mismatch at $Origin. Expected signed-in account '$account'; received '$($me.accountName)'."
            }
            return $me
        }
        return $null
    }
    function SqlScalar([string]$ConnectionString, [string]$Sql, [hashtable]$Parameters = @{}) {
        $connection = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
        try {
            $connection.Open()
            $command = $connection.CreateCommand()
            $command.CommandTimeout = 120
            $command.CommandText = $Sql
            foreach ($key in $Parameters.Keys) { [void]$command.Parameters.AddWithValue($key, $Parameters[$key]) }
            return $command.ExecuteScalar()
        }
        finally { $connection.Dispose() }
    }
    function Snapshot {
        $manager = New-Object Microsoft.Web.Administration.ServerManager
        try {
            $result = @{}
            foreach ($name in $folders.Keys) {
                $site = $manager.Sites[$name]
                if (-not $site) { throw "Required site missing: $name" }
                $path = FullPath $site.Applications['/'].VirtualDirectories['/'].PhysicalPath
                AssertPlainPath $path
                $config = Join-Path $path 'appsettings.Production.json'
                $result[$name] = [pscustomobject]@{
                    Path = $path; Config = $config
                    Hash = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash
                    Bindings = @($site.Bindings | ForEach-Object {
                        $hash = if ($_.Protocol -eq 'https' -and $_.CertificateHash) { [Convert]::ToBase64String($_.CertificateHash) } else { '' }
                        "$($_.Protocol)|$($_.BindingInformation)|$($_.SslFlags)|$hash"
                    })
                }
            }
            return $result
        }
        finally { $manager.Dispose() }
    }
    function AssertBaseline($Expected) {
        $current = Snapshot
        foreach ($name in $folders.Keys) {
            if ($current[$name].Path -ine $Expected[$name].Path -or $current[$name].Hash -cne $Expected[$name].Hash -or
                ($current[$name].Bindings -join ';') -cne ($Expected[$name].Bindings -join ';')) {
                throw "Active IIS/configuration changed since preflight: $name"
            }
        }
    }
    function InvokeHub([hashtable]$Parameters, [switch]$Preview) {
        $marker = if ($Preview) { 'WHATIF_READY' } else { 'HUB_RELEASE_DEPLOYED_AND_HEALTHY' }
        $strings = New-Object 'System.Collections.Generic.List[string]'
        $options = @{} + $Parameters
        if ($Preview) { $options.WhatIf = $true } else { $options.Confirm = $false }
        & $deployScript @options | ForEach-Object {
            # Discard internal Format-List records. Never feed them to Out-Host.
            if ($_ -is [string]) { $strings.Add($_); Write-Host $_ }
        }
        if ($strings.ToArray() -cnotcontains $marker) { throw "Required deployment marker missing: $marker" }
    }
    function AuditPackage([string]$Root) {
        AssertPlainPath $Root
        foreach ($item in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package contains a reparse point: $($item.FullName)" }
            if ($item.PSIsContainer) {
                if ($item.Name -in @('subcontracting-files', 'node_modules', '.git')) { throw "Runtime/source directory in package: $($item.FullName)" }
                continue
            }
            if ($item.Name -match '(?i)(\.(db|sqlite|sqlite3)(-(wal|shm|journal))?$|\.(mdf|ldf|bak|pfx|p12|key|pem)$|^\.env($|\.)|^appsettings\.Production\.json$|^secrets\.json$)') {
                throw "Data or secrets in publication; nothing will be deployed: $($item.FullName)"
            }
        }
    }
    function ProtectDirectory([string]$Path) {
        AssertPlainPath $Path
        [void](New-Item -ItemType Directory -Path $Path)
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
            $rule = New-Object Security.AccessControl.FileSystemAccessRule(
                (New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $Path -AclObject $acl
    }
    function WaitModuleHealth {
        $deadline = [DateTime]::UtcNow.AddSeconds(300)
        do {
            try { Health $moduleUrl; return } catch { $last = $_.Exception.Message }
            Start-Sleep -Seconds 2
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "New module health failed: $last"
    }
    function AssertEnvironmentValues($Variables, [switch]$NewModule) {
        foreach ($variable in $Variables) {
            $name = [string]$variable.Name
            $value = [string]$variable.Value
            if ($name -in @('ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT')) {
                if ($value -cne 'Production') { throw "Non-Production environment override: $name" }
            }
            elseif ($name -match '^(?i)(Authentication(__|:)|Database(__|:)|SubcontractingDatabase(__|:)|ConnectionStrings(__|:)|VendorDocumentStorage(__|:)|(SQL|SQLAZURE|MYSQL|CUSTOM)CONNSTR_)' -and $NewModule) {
                throw "New-module protected configuration override must be reviewed: $name"
            }
            elseif ($name -match '^(?i)QualityIntegration(__|:)Enabled$' -and $value -ine 'false') {
                throw 'Quality automatic-sync environment override is not false.'
            }
        }
    }

    try {
        if ($PSVersionTable.PSVersion.Major -ne 5 -or -not [Environment]::Is64BitProcess) {
            throw 'Use 64-bit Windows PowerShell 5.1, not PowerShell 7 or the x86 console.'
        }
        if ($env:COMPUTERNAME -ine 'SON-IIS2') { throw 'Run on SON-IIS2 only.' }
        $windowsIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $account = $windowsIdentity.Name
        $principal = New-Object Security.Principal.WindowsPrincipal($windowsIdentity)
        if ($windowsIdentity.IsSystem -or $account -notlike 'SON4L\*' -or
            -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Use an elevated interactive PowerShell session under your SON4L Windows account.'
        }
        $mutex = New-Object Threading.Mutex($false, 'Global\ArdaSubcontractingInstaller')
        $lockHeld = $mutex.WaitOne(0)
        if (-not $lockHeld) { throw 'Another copy of this installer is running.' }
        foreach ($path in @($repo, $releaseRoot, $packageBase, $stateRoot)) { AssertPlainPath $path }
        $startMarker = Join-Path $stateRoot "$($commit.Substring(0,12)).started.json"
        $completeMarker = Join-Path $stateRoot "$($commit.Substring(0,12)).complete.json"
        if (Test-Path -LiteralPath $startMarker) {
            throw "PREVIOUS_ATTEMPT_NEEDS_REVIEW: retained start record exists at $startMarker. Do not delete it or rerun deployment; reconcile live state first."
        }
        if (Test-Path -LiteralPath $completeMarker) { throw 'This installation has already completed. Use read-only verification instead of rerunning.' }
        if ((FullPath ((Git @('rev-parse', '--show-toplevel')) -join '')) -ine (FullPath $repo)) {
            throw "The production checkout must resolve to $repo. Git slash direction is normalized."
        }
        if (((Git @('remote', 'get-url', 'origin')) -join '').Trim() -notmatch '^https://github\.com/Jordanpgreer/Son-AeroProjectManager(?:\.git)?/?$') {
            throw 'Unexpected origin remote.'
        }
        if (((Git @('branch', '--show-current')) -join '').Trim() -cne 'main') { throw 'Production source must be on main.' }
        if (@(Git @('status', '--porcelain')).Count) { throw 'Production checkout has local changes. Preserve them and review; no reset or stash is performed.' }
        $sourceVerification = ConfirmReleaseSource
        if (((Git @('rev-parse', 'origin/main')) -join '').Trim() -cne $commit) { throw "origin/main no longer matches reviewed commit $commit. Prepare an updated release script." }
        [void](Git @('merge-base', '--is-ancestor', 'HEAD', $commit))
        Write-Host "SOURCE_COMMIT_VERIFIED $commit"
        Import-Module WebAdministration -ErrorAction Stop
        Add-Type -Path (Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll')
        Add-Type -AssemblyName System.Data
        $machineVariables = [Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Machine)
        AssertEnvironmentValues @($machineVariables.Keys | ForEach-Object { [pscustomobject]@{Name=$_;Value=$machineVariables[$_]} }) -NewModule
        $before = Snapshot
        foreach ($name in $folders.Keys) {
            # An incomplete apply affecting the active release must be reconciled first.
            $activeId = Split-Path -Leaf (Split-Path -Parent $before[$name].Path)
            $activePackage = Join-Path $packageBase $activeId
            if ((Test-Path -LiteralPath (Join-Path $activePackage 'ARDA_CHANGE_STARTED.json')) -and
                -not (Test-Path -LiteralPath (Join-Path $activePackage 'ARDA_CHECKS_PASSED.json'))) {
                throw "Active release $activeId has an unreconciled apply marker. Preserve it and reconcile before upgrading."
            }
        }
        $portalMe = Identity $origins[0]
        if ([string]$portalMe.accountStatus -ine 'configured' -or [string]$portalMe.role -ine 'Admin') {
            throw "The IIS-authenticated account '$account' must be a configured Arda administrator."
        }
        $identityStatuses = @{}
        foreach ($origin in $origins) {
            Health $origin
            $identityResponse = Request "$origin/api/me" @(200,403)
            $identityStatuses[$origin] = $identityResponse.Status
            if ($identityResponse.Status -eq 200) { [void](Identity $origin) }
            else { Write-Host "MODULE_IDENTITY_ACCESS_DENIED_AS_CONFIGURED $origin/api/me" }
        }
        $portalConfig = ReadJson $before.SonAeroPortal.Config
        $qualityConfig = ReadJson $before.QualityAssurance.Config
        if ($qualityConfig.QualityIntegration.Enabled -isnot [bool] -or $qualityConfig.QualityIntegration.Enabled) {
            throw 'QualityIntegration.Enabled must already be false. Active settings are preserved; a separately reviewed correction is required.'
        }
        $manager = New-Object Microsoft.Web.Administration.ServerManager
        try {
            $qualityAsp = $manager.GetWebConfiguration('QualityAssurance').GetSection('system.webServer/aspNetCore')
            AssertEnvironmentValues @($qualityAsp.GetCollection('environmentVariables') | ForEach-Object { [pscustomobject]@{Name=$_.GetAttributeValue('name');Value=$_.GetAttributeValue('value')} })
            AssertEnvironmentValues @($manager.ApplicationPools['QualityAssurance'].GetCollection('environmentVariables') | ForEach-Object { [pscustomobject]@{Name=$_.GetAttributeValue('name');Value=$_.GetAttributeValue('value')} })
        }
        finally { $manager.Dispose() }
        $roleConnection = [string]$portalConfig.ConnectionStrings.RoleStore
        $sqlBuilder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($roleConnection)
        if ($portalConfig.Authentication.Mode -ine 'Windows' -or $portalConfig.Database.Provider -ine 'SqlServer' -or
            -not $sqlBuilder.IntegratedSecurity -or $sqlBuilder.InitialCatalog -ine 'ProjectTracker' -or
            $sqlBuilder.DataSource -notmatch '^(?i)(tcp:)?SON-SQL2(,1433)?$') {
            throw 'Expected preserved Windows-integrated ProjectTracker SQL connection on SON-SQL2.'
        }
        $sqlBuilder['Connect Timeout'] = 15
        $sqlBuilder['Initial Catalog'] = 'master'
        $masterConnection = $sqlBuilder.ConnectionString
        if ([int](SqlScalar $masterConnection "SELECT IS_SRVROLEMEMBER('sysadmin');") -ne 1) {
            throw "SQL_PREREQUISITE_REQUIRED: '$account' lacks SQL authority to provision the separate SmallBusinessSubcontracting database. Have the SQL administrator review provisioning; do not change existing database permissions or restart SQL."
        }
        if ([int](SqlScalar $masterConnection "SELECT COUNT(*) FROM sys.databases WHERE name=N'SmallBusinessSubcontracting';") -ne 0) {
            throw 'SmallBusinessSubcontracting database already exists. First-install script will not reuse or replace it; inspect its ownership/data first.'
        }
        if ([int](SqlScalar $masterConnection 'SELECT COUNT(*) FROM sys.server_principals WHERE name=@account;' @{'@account'=$computerAccount}) -ne 1) {
            throw 'The existing SON4L\SON-IIS2$ SQL login is missing. SQL administrator provisioning is required.'
        }
        $isArdaAdmin = SqlScalar $roleConnection @'
SELECT COUNT(*) FROM dbo.Users u
JOIN dbo.UserGroupMemberships m ON m.AppUserId=u.Id
JOIN dbo.Groups g ON g.Id=m.AppGroupId
WHERE u.AccountName=@account AND u.IsActive=1 AND g.Name=N'Administrators';
'@ @{'@account'=$account}
        if ([int]$isArdaAdmin -lt 1) { throw 'The signed-in account must belong to the existing Arda Administrators group to verify the new module. No memberships are changed.' }
        $drawingRoot = [string]$portalConfig.DrawingStorage.RootPath
        if ($drawingRoot -notmatch '^\\\\SON-SQL2\\[^\\]+\$(\\.*)?$' -or -not (Test-Path -LiteralPath $drawingRoot -PathType Container)) {
            throw 'The preserved Portal document share on SON-SQL2 must be reachable.'
        }
        $documentRoot = Join-Path $drawingRoot 'ArdaSmallBusinessSubcontractingDocuments'
        AssertPlainPath $documentRoot
        if (Test-Path -LiteralPath $documentRoot) { throw "New-module document folder already exists; inspect it instead of overwriting: $documentRoot" }
        $manager = New-Object Microsoft.Web.Administration.ServerManager
        try {
            if ($manager.Sites[$siteName] -or $manager.ApplicationPools[$siteName]) { throw 'New module site or pool already exists. Use a reviewed update/reconciliation instead of this first-install script.' }
            $tls = @($manager.Sites['SonAeroPortal'].Bindings | Where-Object { $_.Protocol -eq 'https' -and $_.BindingInformation -eq '*:443:hub.son4l.local' })
            if ($tls.Count -ne 1 -or -not $tls[0].CertificateHash) { throw 'Expected Hub HTTPS certificate binding is missing.' }
            $certificateHash = [byte[]]$tls[0].CertificateHash.Clone()
            $certificateStore = [string]$tls[0].CertificateStoreName
            $thumbprint = ([BitConverter]::ToString($certificateHash)).Replace('-', '')
            $certificate = Get-Item -LiteralPath "Cert:\LocalMachine\$certificateStore\$thumbprint"
            if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -lt (Get-Date).AddDays(7)) { throw 'Hub certificate is missing its key or expires within seven days.' }
            foreach ($site in $manager.Sites) {
                if (@($site.Bindings | Where-Object { $_.BindingInformation -match ':6180:' }).Count) { throw "TCP 6180 already belongs to $($site.Name)." }
            }
        }
        finally { $manager.Dispose() }
        if (Get-NetTCPConnection -LocalPort 6180 -State Listen -ErrorAction SilentlyContinue) { throw 'TCP 6180 already has a listener.' }
        if (Get-NetFirewallRule -Name $firewallName -ErrorAction SilentlyContinue) { throw 'A retained module firewall rule exists; inspect the previous attempt.' }
        if (@(Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'DomainAuthenticated').Count -eq 0) { throw 'A domain-authenticated network is required for the new HTTPS firewall rule.' }
        $releaseId = $commit.Substring(0,12) + '-hub-' + (Get-Date -Format 'yyyyMMddHHmmss') + '-' + ([guid]::NewGuid().ToString('N').Substring(0,6))
        $work = Join-Path $packageBase $releaseId
        $source = Join-Path $work 'source'
        $package = Join-Path $work 'published'
        $moduleRelease = Join-Path $releaseRoot ($releaseId + '-sbs')
        $modulePath = Join-Path $moduleRelease $siteName
        foreach ($path in @($work, (Join-Path $releaseRoot $releaseId), $moduleRelease)) {
            AssertPlainPath $path
            if (Test-Path -LiteralPath $path) { throw "Fresh destination already exists: $path" }
        }
        ProtectDirectory $work
        Start-Transcript -LiteralPath (Join-Path $work 'installer.log') -NoClobber | Out-Null
        $transcriptStarted = $true
        Write-Host "Preparing $releaseId. New module: $moduleUrl. Documents: $documentRoot"
        $archive = Join-Path $work 'source.zip'
        [void](Git @('archive', '--format=zip', "--output=$archive", $commit))
        Expand-Archive -LiteralPath $archive -DestinationPath $source
        # Only the isolated deployment template is extended; active Production files stay intact.
        $templatePath = Join-Path $source 'deployment\templates\portal.appsettings.Production.json'
        $template = ReadJson $templatePath
        if (@($template.Portal.Applications | Where-Object Id -eq 'small-business-subcontracting').Count) { throw 'Unexpected source template: module entry already exists.' }
        $entry = [pscustomobject]@{
            Id='small-business-subcontracting'; Name='Small Business Subcontracting'
            Description='Vendor certifications, compliance documents, and subcontracting reporting.'
            Category='Compliance'; Icon='building-2'; Url=$moduleUrl; Order=45; Status='Active'; AllowedRoles=@()
        }
        $template.Portal.Applications = @($template.Portal.Applications) + @($entry)
        [IO.File]::WriteAllText($templatePath, ($template | ConvertTo-Json -Depth 100), $utf8)
        $deployScript = Join-Path $source 'deployment\Deploy-HubRelease.ps1'
        & (Join-Path $source 'deployment\Publish-Hub.ps1') -OutputRoot $package -ProjectTrackerUrl '/project-tracker-api' -Configuration Release
        $sdkCandidates = @(
            (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
            (Join-Path $env:LOCALAPPDATA 'CodexDotnetSdk8\dotnet.exe'),
            'C:\Program Files\dotnet\dotnet.exe'
        )
        $sdkCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if ($sdkCommand) { $sdkCandidates = @($sdkCommand.Source) + $sdkCandidates }
        $dotnet = $sdkCandidates | Where-Object { (Test-Path -LiteralPath $_) -and ((Native $_ @('--list-sdks')) -match '^8\.') } | Select-Object -First 1
        if (-not $dotnet) { throw 'A .NET 8 SDK is required.' }
        $newProject = Join-Path $source 'apps\small-business-subcontracting\src\SmallBusinessSubcontracting.Api\SmallBusinessSubcontracting.Api.csproj'
        $modulePackage = Join-Path $package $siteName
        Native $dotnet @('publish', $newProject, '-c', 'Release', '-o', $modulePackage, '--property:UseAppHost=false') | ForEach-Object { Write-Host $_ }
        AuditPackage $package
        if (@(Git @('status', '--porcelain')).Count) { throw 'Production checkout changed during preparation.' }
        # Validate the extended catalog before any provisioning or existing-site apply.
        Import-Module (Join-Path $source 'deployment\PortalApplicationCatalog.psm1') -Force
        $catalogTest = Join-Path $work 'catalog-validation'
        [void](New-Item -ItemType Directory -Path $catalogTest)
        Copy-Item -LiteralPath (Join-Path $package 'Portal\appsettings.json') -Destination $catalogTest
        Copy-Item -LiteralPath $before.SonAeroPortal.Config -Destination $catalogTest
        [void](Sync-PortalProductionApplicationCatalog -CandidatePortalPath $catalogTest -ProductionTemplatePath $templatePath)
        $expectedPortal = ReadJson (Join-Path $catalogTest 'appsettings.Production.json')
        $moduleCards = @($expectedPortal.Portal.Applications | Where-Object Id -eq $entry.Id)
        if ($moduleCards.Count -ne 1 -or $moduleCards[0].Url -cne $moduleUrl) { throw 'Preserved Portal configuration conflicts with the new module URL.' }
        $deployArgs = @{ PackageRoot=$package; ReleaseId=$releaseId; ReleaseRoot=$releaseRoot; ExpectedComputerName='SON-IIS2'; HealthTimeoutSeconds=300 }
        $qualityModule = Join-Path $source 'deployment\QualityAssuranceProductionConfiguration.psm1'
        Import-Module $qualityModule -Force
        $validatedQuality = Read-QualityProductionConfiguration -Path $before.QualityAssurance.Config
        $qualityStorage = $null
        if (Test-QualityProductionConfigurationUsesServerLocalSqlite -Configuration $validatedQuality) {
            $qualityStorage = Assert-QualityServerLocalSqliteStorage -PoolName 'QualityAssurance'
            $qualityCreated = (Get-Item -LiteralPath $qualityStorage.DataFile).CreationTimeUtc.Ticks
        }
        AssertBaseline $before
        InvokeHub $deployArgs -Preview
        Write-Host 'PREVIEW_PASSED: immutable five-app update plus separate sixth-module installation.'
        if ($PrepareOnly) { Write-Host "PREPARE_ONLY_COMPLETE $work"; return }
        Write-Host 'Before continuing, verify current restorable backups of ALL Arda SQL databases, the Quality SQLite store (including consistent WAL handling), and document shares, with restore evidence.'
        Write-Host 'This script does not create those backups. IIS path rollback does not undo application migrations.'
        $backupEvidence = Read-Host 'Enter the existing backup/restore evidence location or reference (not a password)'
        if ([string]::IsNullOrWhiteSpace($backupEvidence)) { throw 'Backup/restore evidence was not provided. No apply started.' }
        $attestation = Read-Host 'Type BACKUPS_VERIFIED only after those backups and restore evidence are verified'
        if ($attestation.Trim() -cne 'BACKUPS_VERIFIED') { throw 'Backup attestation was not provided. No apply started.' }
        AssertBaseline $before
        foreach ($origin in $origins) { Health $origin }
        if (-not (Test-Path -LiteralPath $stateRoot)) { ProtectDirectory $stateRoot }
        $record = [ordered]@{
            Commit=$commit; ReleaseId=$releaseId; Computer=$env:COMPUTERNAME; WindowsAccount=$account
            SourceVerification=$sourceVerification
            StartedUtc=[DateTime]::UtcNow.ToString('o'); Previous=$before; PackageRoot=$package
            ModuleRelease=$modulePath; ModuleUrl=$moduleUrl; ModuleDatabase='SmallBusinessSubcontracting'
            Documents=$documentRoot; BackupRestoreEvidence=$backupEvidence
            DeploymentScriptHash=(Get-FileHash -LiteralPath $deployScript -Algorithm SHA256).Hash
            CatalogTemplateHash=(Get-FileHash -LiteralPath $templatePath -Algorithm SHA256).Hash
        }
        # Exclusive journal precedes every SQL/document/IIS apply. Never overwrite a marker.
        WriteNewJson $startMarker $record
        $started = $true
        WriteNewJson (Join-Path $work 'ARDA_CHANGE_STARTED.json') $record
        [void](SqlScalar $masterConnection @'
IF DB_ID(N'SmallBusinessSubcontracting') IS NOT NULL
    THROW 51000, 'Database appeared after preflight. Refusing to reuse it.', 1;
CREATE DATABASE [SmallBusinessSubcontracting];
SELECT 1;
'@)
        $sqlBuilder['Initial Catalog'] = 'SmallBusinessSubcontracting'
        $moduleConnection = $sqlBuilder.ConnectionString
        [void](SqlScalar $moduleConnection @'
CREATE USER [SON4L\SON-IIS2$] FOR LOGIN [SON4L\SON-IIS2$];
ALTER ROLE [db_datareader] ADD MEMBER [SON4L\SON-IIS2$];
ALTER ROLE [db_datawriter] ADD MEMBER [SON4L\SON-IIS2$];
ALTER ROLE [db_ddladmin] ADD MEMBER [SON4L\SON-IIS2$];
SELECT 1;
'@)
        if (Test-Path -LiteralPath $documentRoot) { throw 'Document destination appeared after preflight; refusing to reuse it.' }
        ProtectDirectory $documentRoot
        Native 'icacls.exe' @($documentRoot, '/grant', "${computerAccount}:(OI)(CI)M") | ForEach-Object { Write-Host $_ }
        [void](New-Item -ItemType Directory -Path $moduleRelease)
        [void](New-Item -ItemType Directory -Path $modulePath)
        foreach ($file in Get-ChildItem -LiteralPath $modulePackage -Recurse -File) {
            if ($file.Name -like 'appsettings.Development*.json') { continue }
            $relative = $file.FullName.Substring($modulePackage.Length).TrimStart('\')
            $destination = Join-Path $modulePath $relative
            $parent = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $parent)) { [void](New-Item -ItemType Directory -Path $parent -Force) }
            [IO.File]::Copy($file.FullName, $destination, $false)
        }
        $newConfig = [ordered]@{
            Authentication=@{Mode='Windows'}; Database=@{Provider='SqlServer'}
            SubcontractingDatabase=@{Provider='SqlServer'}
            ConnectionStrings=@{RoleStore=$roleConnection; SubcontractingStore=$moduleConnection}
            VendorDocumentStorage=@{RootPath=$documentRoot; RequireUncPath=$true; MaximumFileBytes=20971520}
            AllowedHosts='hub.son4l.local'; Fulcrum=@{BaseUrl='https://api.fulcrumpro.us/'}
        }
        WriteNewJson (Join-Path $modulePath 'appsettings.Production.json') $newConfig
        [xml]$web = Get-Content -LiteralPath (Join-Path $modulePath 'web.config') -Raw
        $asp = @($web.SelectNodes('//aspNetCore'))
        if ($asp.Count -ne 1 -or $asp[0].processPath -ine 'dotnet' -or
            $asp[0].arguments -cne '.\SmallBusinessSubcontracting.Api.dll' -or $asp[0].hostingModel -ine 'inprocess') { throw 'New-module web.config is not the expected in-process publication.' }
        if (@($asp[0].SelectNodes('environmentVariables/environmentVariable')).Count) { throw 'Unexpected new-module web.config environment overrides.' }
        $envs = $web.CreateElement('environmentVariables')
        $envNode = $web.CreateElement('environmentVariable')
        $envNode.SetAttribute('name', 'ASPNETCORE_ENVIRONMENT'); $envNode.SetAttribute('value', 'Production')
        [void]$envs.AppendChild($envNode); [void]$asp[0].AppendChild($envs)
        $web.Save((Join-Path $modulePath 'web.config'))
        # Save a recovery snapshot; never restore the entire IIS server automatically.
        Native (Join-Path $env:windir 'System32\inetsrv\appcmd.exe') @('add', 'backup', "Arda-$releaseId") | ForEach-Object { Write-Host $_ }
        $manager = New-Object Microsoft.Web.Administration.ServerManager
        try {
            if ($manager.Sites[$siteName] -or $manager.ApplicationPools[$siteName]) { throw 'Module IIS resources appeared after preflight.' }
            $pool = $manager.ApplicationPools.Add($siteName)
            $pool.ManagedRuntimeVersion = ''
            $pool.ProcessModel.IdentityType = [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity
            $pool.ProcessModel.LoadUserProfile = $true
            $pool.ProcessModel.IdleTimeout = [TimeSpan]::Zero
            $pool.ProcessModel.MaxProcesses = 1
            $pool.Recycling.DisallowOverlappingRotation = $true
            $pool.AutoStart = $false
            $site = $manager.Sites.Add($siteName, 'https', '*:6180:hub.son4l.local', $modulePath)
            $site.ServerAutoStart = $false
            $site.Applications['/'].ApplicationPoolName = $siteName
            $binding = $site.Bindings[0]
            $binding.CertificateHash = $certificateHash
            $binding.CertificateStoreName = $certificateStore
            $binding.SslFlags = [Microsoft.Web.Administration.SslFlags]::Sni
            $hostConfig = $manager.GetApplicationHostConfiguration()
            $hostConfig.GetSection('system.webServer/security/authentication/anonymousAuthentication', $siteName).SetAttributeValue('enabled', $false)
            $hostConfig.GetSection('system.webServer/security/authentication/windowsAuthentication', $siteName).SetAttributeValue('enabled', $true)
            $moduleIisAttempted = $true
            $manager.CommitChanges()
        }
        finally { $manager.Dispose() }
        Native 'icacls.exe' @($modulePath, '/grant', "IIS AppPool\${siteName}:(OI)(CI)RX", '/t') | ForEach-Object { Write-Host $_ }
        $manager = New-Object Microsoft.Web.Administration.ServerManager
        try {
            $newAsp = $manager.GetWebConfiguration($siteName).GetSection('system.webServer/aspNetCore')
            AssertEnvironmentValues @($newAsp.GetCollection('environmentVariables') | ForEach-Object { [pscustomobject]@{Name=$_.GetAttributeValue('name');Value=$_.GetAttributeValue('value')} }) -NewModule
            AssertEnvironmentValues @($manager.ApplicationPools[$siteName].GetCollection('environmentVariables') | ForEach-Object { [pscustomobject]@{Name=$_.GetAttributeValue('name');Value=$_.GetAttributeValue('value')} }) -NewModule
        }
        finally { $manager.Dispose() }
        New-NetFirewallRule -Name $firewallName -DisplayName 'Arda Small Business Subcontracting HTTPS' -Direction Inbound -Protocol TCP -LocalPort 6180 -Profile Domain -Action Allow | Out-Null
        Start-WebAppPool -Name $siteName
        Start-Website -Name $siteName
        WaitModuleHealth
        # Read actual tables, not only /health, before updating any existing application.
        $tableCount = SqlScalar $moduleConnection "SELECT COUNT(*) FROM sys.tables WHERE name IN (N'Vendors',N'BusinessSizeTags',N'VendorBusinessSizeTags',N'VendorDocuments',N'VendorAuditEvents');"
        if ([int]$tableCount -ne 5) { throw 'The dedicated new-module schema did not initialize correctly.' }
        AssertBaseline $before
        InvokeHub $deployArgs
        $hubVerified = $true
        $after = Snapshot
        $manager = New-Object Microsoft.Web.Administration.ServerManager
        try {
            $gateway = FullPath $manager.Sites['SonAeroPortal'].Applications['/project-tracker-api'].VirtualDirectories['/'].PhysicalPath
            if ($gateway -ine $after.ProjectTracker.Path) { throw 'Portal Project Tracker gateway is not on the verified release.' }
            if ((FullPath $manager.Sites[$siteName].Applications['/'].VirtualDirectories['/'].PhysicalPath) -ine (FullPath $modulePath)) { throw 'New-module IIS path does not match its candidate.' }
        }
        finally { $manager.Dispose() }
        foreach ($name in $folders.Keys) {
            $expected = FullPath (Join-Path (Join-Path $releaseRoot $releaseId) $folders[$name])
            if ($after[$name].Path -ine $expected) { throw "New release is not active for $name" }
            if ((Get-FileHash -LiteralPath $before[$name].Config -Algorithm SHA256).Hash -cne $before[$name].Hash) { throw "Prior configuration changed: $name" }
            if ($name -ne 'SonAeroPortal' -and $after[$name].Hash -cne $before[$name].Hash) { throw "Production configuration was not preserved: $name" }
            if (($after[$name].Bindings -join ';') -cne ($before[$name].Bindings -join ';')) { throw "Bindings changed: $name" }
        }
        $actualPortal = ReadJson $after.SonAeroPortal.Config
        if (($actualPortal | ConvertTo-Json -Depth 100 -Compress) -cne ($expectedPortal | ConvertTo-Json -Depth 100 -Compress)) { throw 'Portal candidate does not match the prevalidated catalog/configuration.' }
        if ($qualityStorage) {
            [void](Assert-QualityServerLocalSqliteStorage -PoolName 'QualityAssurance')
            if ((Get-Item -LiteralPath $qualityStorage.DataFile).CreationTimeUtc.Ticks -ne $qualityCreated) { throw 'Quality SQLite file identity changed.' }
        }
        foreach ($origin in $origins) {
            Health $origin
            $allowed = if ($identityStatuses[$origin] -eq 200) { @(200) } else { @(200,403) }
            [void](Identity $origin $allowed)
        }
        $moduleMe = Identity $moduleUrl
        foreach ($permission in @('module.view','vendors.view','dashboard.view','export','compliance.manage','documents.manage','fulcrum.sync')) {
            if (@($moduleMe.permissions) -notcontains "small-business-subcontracting.$permission") { throw "New-module administrator permission missing: $permission" }
        }
        [void](Request "$moduleUrl/api/vendors")
        [void](Request "$moduleUrl/api/dashboard")
        [void](Request "$moduleUrl/")
        [void](Request "$moduleUrl/brand/arda-lockup.png")
        $appsResponse = Request 'https://hub.son4l.local/api/apps'
        $parsedApps = $appsResponse.Content | ConvertFrom-Json
        $cards = @($parsedApps | Where-Object id -eq 'small-business-subcontracting')
        if ($cards.Count -ne 1 -or $cards[0].url -cne $moduleUrl) { throw 'New module is not visible to the verified administrator in Portal.' }
        $cors = Invoke-WebRequest -UseBasicParsing -Method Options -Uri 'https://projects.hub.son4l.local/api/me' -TimeoutSec 30 -Headers @{
            Origin='https://hub.son4l.local'; 'Access-Control-Request-Method'='GET'
        }
        if ([string]$cors.Headers['Access-Control-Allow-Origin'] -cne 'https://hub.son4l.local') { throw 'Project Tracker CORS postcheck failed.' }
        $manager = New-Object Microsoft.Web.Administration.ServerManager
        try {
            $manager.Sites[$siteName].ServerAutoStart = $true
            $manager.ApplicationPools[$siteName].AutoStart = $true
            $manager.ApplicationPools[$siteName].StartMode = [Microsoft.Web.Administration.StartMode]::AlwaysRunning
            $manager.CommitChanges()
        }
        finally { $manager.Dispose() }
        Health $moduleUrl
        $completion = [ordered]@{ Commit=$commit; ReleaseId=$releaseId; CompletedUtc=[DateTime]::UtcNow.ToString('o'); WindowsAccount=$account; Active=$after; ModulePath=$modulePath; ModuleUrl=$moduleUrl; Status='ARDA_SIX_APPLICATIONS_VERIFIED' }
        WriteNewJson (Join-Path $work 'ARDA_CHECKS_PASSED.json') $completion
        WriteNewJson $completeMarker $completion
        Write-Host "ARDA_SIX_APPLICATIONS_VERIFIED $moduleUrl" -ForegroundColor Green
        Write-Host "Evidence: $work"
    }
    catch {
        $detail = $_.Exception.Message
        if ($hubVerified) {
            Write-Host 'POSTCHECK_STOP_AFTER_VERIFIED_APPLY: Hub apply succeeded. Preserve evidence; DO NOT rerun.' -ForegroundColor Yellow
        }
        elseif ($started) {
            # Stop only this newly created module. The tested Hub transaction owns its rollback.
            if ($moduleIisAttempted) {
                try {
                    if ((Get-WebsiteState -Name $siteName).Value -eq 'Started') { Stop-Website -Name $siteName }
                    if ((Get-WebAppPoolState -Name $siteName).Value -eq 'Started') { Stop-WebAppPool -Name $siteName }
                }
                catch { Write-Warning "New-module stop needs review: $($_.Exception.Message)" }
            }
            Write-Host 'APPLY_NOT_VERIFIED: SQL, files or IIS may have changed. All data and release evidence are retained. DO NOT RERUN; reconcile first.' -ForegroundColor Red
        }
        else { Write-Host 'STOPPED_BEFORE_APPLY: No SQL, active configuration or release apply started.' -ForegroundColor Yellow }
        Write-Host "FAILURE_DETAIL: $detail" -ForegroundColor Red
        throw
    }
    finally {
        if ($transcriptStarted) { try { Stop-Transcript | Out-Null } catch {} }
        if ($lockHeld) { $mutex.ReleaseMutex() }
        if ($mutex) { $mutex.Dispose() }
    }
}
