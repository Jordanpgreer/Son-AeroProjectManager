[CmdletBinding()]
param(
    [string]$WarmStartScriptPath = '',
    [string]$UserAccessScriptPath = '',
    [string]$ShortcutScriptPath = '',
    [string]$PackageScriptPath = '',
    [string]$BootstrapScriptPath = '',
    [string]$WebPushScriptPath = '',
    [string]$PublishScriptPath = '',
    [string]$CorsAuthenticationScriptPath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($WarmStartScriptPath)) {
    $WarmStartScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Configure-IisWarmStart.ps1'
}
if ([string]::IsNullOrWhiteSpace($UserAccessScriptPath)) {
    $UserAccessScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Test-HubUserAccess.ps1'
}
if ([string]::IsNullOrWhiteSpace($ShortcutScriptPath)) {
    $ShortcutScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Install-EmployeeHubShortcut.ps1'
}
if ([string]::IsNullOrWhiteSpace($PackageScriptPath)) {
    $PackageScriptPath = Join-Path $PSScriptRoot '..\..\deployment\New-EmployeeHubInstallerPackage.ps1'
}
if ([string]::IsNullOrWhiteSpace($BootstrapScriptPath)) {
    $BootstrapScriptPath = Join-Path $PSScriptRoot '..\..\deployment\employee-installer\Install-SonAeroHub.ps1'
}
if ([string]::IsNullOrWhiteSpace($WebPushScriptPath)) {
    $WebPushScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Configure-ProjectTrackerWebPush.ps1'
}
if ([string]::IsNullOrWhiteSpace($PublishScriptPath)) {
    $PublishScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Publish-Hub.ps1'
}
if ([string]::IsNullOrWhiteSpace($CorsAuthenticationScriptPath)) {
    $CorsAuthenticationScriptPath = Join-Path $PSScriptRoot '..\..\deployment\Configure-ProjectTrackerCorsAuthentication.ps1'
}
if ($PSVersionTable.PSVersion.Major -ne 5) {
    throw "These compatibility tests must run under Windows PowerShell 5.1; current version is $($PSVersionTable.PSVersion)."
}

function Assert-True {
    param([Parameter(Mandatory = $true)][bool]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if ($Actual -cne $Expected) {
        throw "$Message Expected '$Expected', received '$Actual'."
    }
}

function Get-ScriptAst {
    param([Parameter(Mandatory = $true)][string]$Path)

    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Resolve-Path $Path), [ref]$tokens, [ref]$errors
    )
    if ($errors.Count -gt 0) { throw "$Path has syntax errors: $($errors.Message -join '; ')" }
    return $ast
}

function Get-TestableFunctions {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    $ast = Get-ScriptAst $Path
    $definitions = @(
        $ast.FindAll({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $Names
        }, $true) | ForEach-Object { $_.Extent.Text }
    )
    if ($definitions.Count -ne $Names.Count) {
        throw "Expected $($Names.Count) testable functions in $Path but found $($definitions.Count)."
    }
    return [scriptblock]::Create(($definitions -join [Environment]::NewLine))
}

function Get-ParameterDefault {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ParameterName
    )

    $ast = Get-ScriptAst $Path
    $parameter = @($ast.ParamBlock.Parameters | Where-Object {
        $_.Name.VariablePath.UserPath -eq $ParameterName
    })
    if ($parameter.Count -ne 1 -or $null -eq $parameter[0].DefaultValue) {
        throw "Parameter $ParameterName in $Path does not have exactly one inspectable default."
    }
    return $parameter[0].DefaultValue.SafeGetValue()
}

function Read-ZipConfiguration {
    param([Parameter(Mandatory = $true)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entryNames = @($archive.Entries | ForEach-Object FullName)
        $configurationEntry = @($archive.Entries | Where-Object {
            $_.FullName -eq 'SonAeroHubInstaller.json'
        })
        Assert-True ($configurationEntry.Count -eq 1) 'The employee ZIP must contain exactly one installer configuration.'
        $stream = $configurationEntry[0].Open()
        $reader = New-Object IO.StreamReader($stream)
        try { $configuration = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        return [pscustomobject]@{ EntryNames = $entryNames; Configuration = $configuration }
    }
    finally { $archive.Dispose() }
}

# Parse every touched script under Windows PowerShell 5.1 before running behavior checks.
$null = Get-ScriptAst $WarmStartScriptPath
$null = Get-ScriptAst $UserAccessScriptPath
$null = Get-ScriptAst $ShortcutScriptPath
$null = Get-ScriptAst $PackageScriptPath
$null = Get-ScriptAst $BootstrapScriptPath
$null = Get-ScriptAst $WebPushScriptPath
$null = Get-ScriptAst $PublishScriptPath
$corsAuthenticationAst = Get-ScriptAst $CorsAuthenticationScriptPath

$corsAuthenticationSource = Get-Content -LiteralPath $CorsAuthenticationScriptPath -Raw
Assert-True ($corsAuthenticationSource -match '-Method Options' -and
    $corsAuthenticationSource -match "'Access-Control-Request-Method'\s*=\s*'POST'" -and
    $corsAuthenticationSource -match 'Anonymous Project Tracker /api/me must return HTTP 401' -and
    $corsAuthenticationSource -match '-UseDefaultCredentials' -and
    $corsAuthenticationSource -match '\$identity\s*=\s*\[Security\.Principal\.WindowsIdentity\]::GetCurrent\(\)' -and
    $corsAuthenticationSource -match '\$payload\.accountName -ine \$ExpectedAccountName' -and
    $corsAuthenticationSource -match 'Wait-DirectSiteBoundary' -and
    $corsAuthenticationSource -match 'Start-Sleep -Milliseconds 750' -and
    $corsAuthenticationSource -match '-AnonymousEnabled \$false -WindowsEnabled \$true' -and
    $corsAuthenticationSource -match 'foreach \(\$probe in \$CorsProbes\)[\s\S]*Assert-CorsPreflight -Uri \$probe\.Uri -Origin \$probe\.Origin') `
    'The direct Project Tracker CORS authentication script does not prove preflight, API denial, identity, and gateway isolation.'
$setAuthenticationCalls = @($corsAuthenticationAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Set-AuthenticationState'
}, $true))
Assert-True ($setAuthenticationCalls.Count -eq 2 -and @($setAuthenticationCalls | Where-Object {
    $_.Extent.Text -notmatch '-Location\s+\$ProjectTrackerSiteName'
}).Count -eq 0) 'IIS authentication mutation is not limited to the direct ProjectTracker site.'
Assert-True ($corsAuthenticationSource -match '-AnonymousEnabled \$prior\.AnonymousEnabled -WindowsEnabled \$prior\.WindowsEnabled' -and
    $corsAuthenticationSource -match '\$rollbackVerifyManager' -and
    $corsAuthenticationSource -match 'prior IIS authentication state was restored and verified') `
    'The direct Project Tracker CORS authentication script does not restore and independently verify prior IIS state.'
Assert-True ($corsAuthenticationSource.Contains('$identity.IsSystem') -and
    $corsAuthenticationSource.Contains("'NT AUTHORITY\SYSTEM'")) `
    'The direct Project Tracker repair does not explicitly reject Local System identity verification.'
# The authentication bootstrap runs before the production application-config transaction. It must
# verify exactly the approved origins already active in Project Tracker instead of requiring the
# permanent origin before the transaction that installs it.
. (Get-TestableFunctions -Path $CorsAuthenticationScriptPath -Names @(
    'ConvertTo-ApprovedTrackerCorsProbe',
    'Get-ConfiguredTrackerCorsProbes'
))
$script:ExpectedComputerName = 'SON-IIS2'
$corsConfigurationRoot = Join-Path $env:TEMP ('sonaero-cors-auth-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $corsConfigurationRoot | Out-Null
try {
    $httpOnlyPath = Join-Path $corsConfigurationRoot 'http-only.json'
    Set-Content -LiteralPath $httpOnlyPath -Encoding UTF8 -Value `
        '{"Cors":{"HubOrigins":["http://SON-IIS2:5140"]}}'
    $httpOnly = @(Get-ConfiguredTrackerCorsProbes -ConfigurationPath $httpOnlyPath)
    Assert-True ($httpOnly.Count -eq 1 -and
        $httpOnly[0].Origin -ceq 'http://son-iis2:5140' -and
        $httpOnly[0].Uri -ceq 'http://SON-IIS2:5135/api/me') `
        'The CORS authentication bootstrap cannot validate the retained HTTP-only configuration.'
    $productionPath = Join-Path $corsConfigurationRoot 'production.json'
    Set-Content -LiteralPath $productionPath -Encoding UTF8 -Value `
        '{"Cors":{"HubOrigins":["https://hub.son4l.local","https://SON-IIS2:6140/","http://SON-IIS2:5140"]}}'
    $production = @(Get-ConfiguredTrackerCorsProbes -ConfigurationPath $productionPath)
    $productionMappings = @($production | ForEach-Object { "$($_.Origin)=>$($_.Uri)" }) -join '|'
    Assert-Equal $productionMappings 'https://hub.son4l.local=>https://projects.hub.son4l.local/api/me|https://son-iis2:6140=>https://SON-IIS2:6135/api/me|http://son-iis2:5140=>http://SON-IIS2:5135/api/me' `
        'The CORS authentication bootstrap returned incorrect approved production mappings.'
    $invalidConfigurations = [ordered]@{
        '{}' = 'must contain one Cors object'
        '{"Cors":{}}' = 'must be a JSON array'
        '{"Cors":{"HubOrigins":null}}' = 'must be a JSON array'
        '{"Cors":{"HubOrigins":[]}}' = 'must contain at least one approved origin'
        '{"Cors":{"HubOrigins":"http://SON-IIS2:5140"}}' = 'must be a JSON array'
        '{"Cors":{"HubOrigins":[""]}}' = 'must be a non-empty string'
        '{"Cors":{"HubOrigins":["   "]}}' = 'must be a non-empty string'
        '{"Cors":{"HubOrigins":[42]}}' = 'must be a non-empty string'
        '{"Cors":{"HubOrigins":["*"]}}' = 'contains unapproved origin'
        '{"Cors":{"HubOrigins":["https://*.hub.son4l.local"]}}' = 'contains unapproved origin'
        '{"Cors":{"HubOrigins":["https://unapproved.example.com"]}}' = 'contains unapproved origin'
        '{"Cors":{"HubOrigins":["http://SON-IIS2:5140","http://son-iis2:5140/"]}}' = 'contains duplicate origin'
    }
    $index = 0
    foreach ($invalid in $invalidConfigurations.GetEnumerator()) {
        $invalidPath = Join-Path $corsConfigurationRoot ("invalid-$index.json")
        Set-Content -LiteralPath $invalidPath -Encoding UTF8 -Value $invalid.Key
        $rejection = ''
        try { @(Get-ConfiguredTrackerCorsProbes -ConfigurationPath $invalidPath) | Out-Null }
        catch { $rejection = $_.Exception.Message }
        Assert-True ($rejection -like "*$($invalid.Value)*") `
            "Unsafe CORS bootstrap configuration $index was accepted or failed unexpectedly: $rejection"
        $index++
    }
}
finally {
    if (Test-Path -LiteralPath $corsConfigurationRoot) {
        Remove-Item -LiteralPath $corsConfigurationRoot -Recurse -Force
    }
}
$iisSetupSource = Get-Content -LiteralPath (
    Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'deployment\Configure-IisServer.ps1'
) -Raw
Assert-True ($iisSetupSource -match '-Name enabled -Value \(\$site\.Name -eq ''ProjectTracker''\)') `
    'Fresh IIS setup does not preserve the Project Tracker anonymous-preflight boundary.'

. (Get-TestableFunctions -Path $WebPushScriptPath -Names @(
    'Assert-VerificationEndpoint', 'Assert-VapidP256KeyPair'
))
$vapidKeyOne = New-Object Security.Cryptography.ECDsaCng 256
$vapidKeyTwo = New-Object Security.Cryptography.ECDsaCng 256
try {
    $vapidOne = $vapidKeyOne.ExportParameters($true)
    $vapidTwo = $vapidKeyTwo.ExportParameters($true)
    $vapidPublicOne = New-Object byte[] 65
    $vapidPublicOne[0] = 4
    [Array]::Copy($vapidOne.Q.X, 0, $vapidPublicOne, 1, 32)
    [Array]::Copy($vapidOne.Q.Y, 0, $vapidPublicOne, 33, 32)
    Assert-VapidP256KeyPair -PublicKeyBytes $vapidPublicOne -PrivateKeyBytes $vapidOne.D
    $mismatchedVapidPairRejected = $false
    try { Assert-VapidP256KeyPair -PublicKeyBytes $vapidPublicOne -PrivateKeyBytes $vapidTwo.D }
    catch { $mismatchedVapidPairRejected = $true }
    Assert-True $mismatchedVapidPairRejected `
        'Web Push accepted a public key and private scalar from different P-256 key pairs.'
}
finally {
    if ($null -ne $vapidOne.D) { [Array]::Clear($vapidOne.D, 0, $vapidOne.D.Length) }
    if ($null -ne $vapidTwo.D) { [Array]::Clear($vapidTwo.D, 0, $vapidTwo.D.Length) }
    if ($null -ne $vapidPublicOne) { [Array]::Clear($vapidPublicOne, 0, $vapidPublicOne.Length) }
    $vapidKeyOne.Dispose()
    $vapidKeyTwo.Dispose()
}
Assert-VerificationEndpoint -Uri ([Uri]'https://projects.hub.son4l.local/api/push/public-key') -ComputerName SON-IIS2
Assert-VerificationEndpoint -Uri ([Uri]'https://SON-IIS2:6135/api/push/public-key') -ComputerName SON-IIS2
Assert-VerificationEndpoint -Uri ([Uri]'http://SON-IIS2:5135/api/push/public-key') -ComputerName SON-IIS2
$externalVerificationRejected = $false
try {
    Assert-VerificationEndpoint -Uri ([Uri]'https://unapproved.example.com/api/push/public-key') -ComputerName SON-IIS2
}
catch { $externalVerificationRejected = $true }
Assert-True $externalVerificationRejected 'Web Push accepted an external verification credential destination.'
foreach ($unsafeUri in @(
    'http://projects.hub.son4l.local/api/push/public-key',
    'https://projects.hub.son4l.local:9999/api/push/public-key',
    'http://SON-IIS2:6135/api/push/public-key',
    'https://SON-IIS2:5135/api/push/public-key'
)) {
    $unsafeVerificationRejected = $false
    try { Assert-VerificationEndpoint -Uri ([Uri]$unsafeUri) -ComputerName SON-IIS2 }
    catch { $unsafeVerificationRejected = $true }
    Assert-True $unsafeVerificationRejected "Web Push accepted unsafe verification endpoint '$unsafeUri'."
}

. (Get-TestableFunctions -Path $PublishScriptPath -Names @('ConvertTo-ApprovedProjectTrackerUrl'))
$publishAst = Get-ScriptAst $PublishScriptPath
$publishParameterNames = @($publishAst.ParamBlock.Parameters | ForEach-Object {
    $_.Name.VariablePath.UserPath
})
foreach ($removedOverride in @(
    'HubUrl', 'EngineeringHubUrl', 'EstimatingDashboardUrl', 'QualityAssuranceUrl'
)) {
    Assert-True ($removedOverride -notin $publishParameterNames) `
        "Publish-Hub still exposes topology-specific override $removedOverride."
}
Assert-Equal (ConvertTo-ApprovedProjectTrackerUrl '/project-tracker-api/') '/project-tracker-api' `
    'Publish-Hub did not normalize the same-origin Project Tracker gateway path.'
Assert-Equal (ConvertTo-ApprovedProjectTrackerUrl 'https://projects.hub.son4l.local') `
    'https://projects.hub.son4l.local' 'Publish-Hub rejected the permanent Project Tracker origin.'
Assert-Equal (ConvertTo-ApprovedProjectTrackerUrl 'https://SON-IIS2:6135') `
    'https://son-iis2:6135' 'Publish-Hub rejected the retained HTTPS pilot Project Tracker origin.'
$externalTrackerUrlRejected = $false
try { ConvertTo-ApprovedProjectTrackerUrl 'https://unapproved.example.com' | Out-Null }
catch { $externalTrackerUrlRejected = $true }
Assert-True $externalTrackerUrlRejected 'Publish-Hub accepted an external credential-bearing Project Tracker URL.'
foreach ($unsafeTrackerUrl in @(
    'https://user:password@projects.hub.son4l.local',
    'http://projects.hub.son4l.local',
    '//unapproved.example.com/project-tracker-api',
    '/unapproved-gateway'
)) {
    $unsafeTrackerUrlRejected = $false
    try { ConvertTo-ApprovedProjectTrackerUrl $unsafeTrackerUrl | Out-Null }
    catch { $unsafeTrackerUrlRejected = $true }
    Assert-True $unsafeTrackerUrlRejected "Publish-Hub accepted unsafe ProjectTrackerUrl '$unsafeTrackerUrl'."
}
. (Get-TestableFunctions -Path $WarmStartScriptPath -Names @(
    'Resolve-WarmStartEndpoint', 'New-HubEndpointUri', 'New-StartupRecoveryArguments',
    'New-WarmStartFileSystemSecurity'
))

$warmStartDirectoryAcl = New-WarmStartFileSystemSecurity -Directory
Assert-True $warmStartDirectoryAcl.AreAccessRulesProtected `
    'The warm-start operations directory ACL still inherits permissions.'
$warmStartRules = @($warmStartDirectoryAcl.GetAccessRules(
    $true, $true, [Security.Principal.SecurityIdentifier]
))
$warmStartRuleSids = @($warmStartRules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
Assert-True (($warmStartRuleSids -join '|') -ceq 'S-1-5-18|S-1-5-32-544') `
    'The warm-start ACL grants access to identities other than SYSTEM and Administrators.'
Assert-True (@($warmStartRules | Where-Object {
    ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne
        [Security.AccessControl.FileSystemRights]::FullControl
}).Count -eq 0) 'The warm-start ACL does not grant full control to both protected identities.'
$brandingAcl = New-WarmStartFileSystemSecurity -Directory -BrandingRoot
$brandingRules = @($brandingAcl.GetAccessRules(
    $true, $true, [Security.Principal.SecurityIdentifier]
))
$brandingUsersRules = @($brandingRules | Where-Object {
    $_.IdentityReference.Value -eq 'S-1-5-32-545'
})
$brandingReadMask = [Security.AccessControl.FileSystemRights]::ReadAndExecute
$brandingWriteMask = [Security.AccessControl.FileSystemRights]::Write -bor
    [Security.AccessControl.FileSystemRights]::Delete -bor
    [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
    [Security.AccessControl.FileSystemRights]::TakeOwnership
Assert-True ($brandingUsersRules.Count -eq 1 -and
    ($brandingUsersRules[0].FileSystemRights -band $brandingReadMask) -eq $brandingReadMask -and
    ($brandingUsersRules[0].FileSystemRights -band $brandingWriteMask) -eq 0) `
    'The SonAero branding directory does not grant Builtin Users read-only icon access.'
$operationsUsersRules = @($warmStartRules | Where-Object {
    $_.IdentityReference.Value -eq 'S-1-5-32-545'
})
Assert-True ($operationsUsersRules.Count -eq 0) `
    'Builtin Users can access the protected warm-start Operations directory.'
$warmStartSource = Get-Content -LiteralPath $WarmStartScriptPath -Raw
Assert-True ($warmStartSource -match 'New-WarmStartFileSystemSecurity -Directory -BrandingRoot' -and
    $warmStartSource -match 'Assert-ProtectedWarmStartPath -Path \$sonAeroDirectory -Directory -BrandingRoot') `
    'The SonAero branding parent is not assigned its distinct read-only employee ACL.'
$protectCallIndex = $warmStartSource.IndexOf(
    'Install-ProtectedWarmStartScript -SourcePath $PSCommandPath -DestinationPath $installedScriptPath'
)
$registerTaskIndex = $warmStartSource.IndexOf('Register-ScheduledTask -TaskName $taskName')
Assert-True ($warmStartSource -match '\[IO\.FileAttributes\]::ReparsePoint' -and
    $protectCallIndex -ge 0 -and $registerTaskIndex -gt $protectCallIndex) `
    'The SYSTEM startup task can be registered before reparse/ACL/hash protection of its script.'

$warmStartMappings = [ordered]@{
    ProjectTracker = 'projects.hub.son4l.local'
    SonAeroPortal = 'hub.son4l.local'
    EngineeringHub = 'engineering.hub.son4l.local'
    EstimatingDashboard = 'estimating.hub.son4l.local'
    QualityAssurance = 'quality.hub.son4l.local'
}
foreach ($entry in $warmStartMappings.GetEnumerator()) {
    $endpoint = Resolve-WarmStartEndpoint -Site $entry.Key -SelectedScheme https `
        -DefaultHostName SON-IIS2 -HttpPort 5000 -HttpsPort 6000 -UsePermanentHttps
    Assert-Equal $endpoint.Scheme 'https' "Permanent warm-start scheme is wrong for $($entry.Key)."
    Assert-Equal $endpoint.HostName $entry.Value "Permanent warm-start host is wrong for $($entry.Key)."
    Assert-True ($endpoint.Port -eq 443) "Permanent warm-start port is wrong for $($entry.Key)."
    $healthUri = New-HubEndpointUri -SelectedScheme $endpoint.Scheme `
        -HostName $endpoint.HostName -Port $endpoint.Port -Path '/api/health'
    Assert-Equal $healthUri ("https://{0}/api/health" -f $entry.Value) `
        "Permanent warm-start URI is wrong for $($entry.Key)."
}

$legacyWarmStart = Resolve-WarmStartEndpoint -Site ProjectTracker -SelectedScheme https `
    -DefaultHostName SON-IIS2 -HttpPort 5135 -HttpsPort 6135
Assert-Equal $legacyWarmStart.HostName 'SON-IIS2' 'The HTTPS pilot host changed.'
Assert-True ($legacyWarmStart.Port -eq 6135) 'The HTTPS pilot port changed.'
$permanentRecoveryArguments = New-StartupRecoveryArguments `
    -InstalledScriptPath 'C:\ProgramData\SonAero\Operations\Configure-IisWarmStart.ps1' `
    -ComputerName SON-IIS2 -SelectedScheme https -UsePermanentHttps `
    -ProjectTrackerPort 6135 -PortalPort 6140 -EngineeringPort 6150 `
    -EstimatingPort 6160 -QualityAssurancePort 6170
Assert-True ($permanentRecoveryArguments -match '(?:^| )-PermanentHttps(?: |$)') `
    'The permanent startup task does not preserve the permanent HTTPS profile.'
Assert-True ($permanentRecoveryArguments -notmatch 'HttpsPort') `
    'The permanent startup task unexpectedly persisted pilot port parameters.'
$legacyRecoveryArguments = New-StartupRecoveryArguments `
    -InstalledScriptPath 'C:\ProgramData\SonAero\Operations\Configure-IisWarmStart.ps1' `
    -ComputerName SON-IIS2 -SelectedScheme https `
    -ProjectTrackerPort 6135 -PortalPort 6140 -EngineeringPort 6150 `
    -EstimatingPort 6160 -QualityAssurancePort 6170
Assert-True ($legacyRecoveryArguments -match '-ProjectTrackerHttpsPort 6135') `
    'The retained HTTPS pilot port was not persisted in the startup task.'
Assert-True ($legacyRecoveryArguments -notmatch '(?:^| )-PermanentHttps(?: |$)') `
    'The retained HTTPS pilot startup task unexpectedly selected permanent mode.'

$userAccessSource = Get-Content -LiteralPath $UserAccessScriptPath -Raw
$accessEndpointKeys = @(
    [regex]::Matches($userAccessSource, "\[pscustomobject\]@\{\s*Key\s*=\s*'([^']+)'") |
        ForEach-Object { $_.Groups[1].Value }
)
Assert-True ($accessEndpointKeys.Count -eq 6) `
    "The user-access test must contain exactly six endpoints; found $($accessEndpointKeys.Count)."
Assert-True (@($accessEndpointKeys | Sort-Object -Unique).Count -eq 6) `
    'The user-access test contains duplicate endpoint keys.'
$accessMappings = [ordered]@{
    portal = 'hub.son4l.local'
    'project-tracker' = 'projects.hub.son4l.local'
    'project-tracker-gateway' = 'hub.son4l.local'
    engineering = 'engineering.hub.son4l.local'
    estimating = 'estimating.hub.son4l.local'
    'quality-assurance' = 'quality.hub.son4l.local'
}
foreach ($entry in $accessMappings.GetEnumerator()) {
    $escapedKey = [regex]::Escape([string]$entry.Key)
    $escapedOrigin = [regex]::Escape(('https://{0}' -f $entry.Value))
    Assert-True ($userAccessSource -match (
        "Key\s*=\s*'$escapedKey'.*PermanentHttpsOrigin\s*=\s*'$escapedOrigin'"
    )) "Permanent access-test origin is wrong or missing for $($entry.Key)."
}
Assert-True ($userAccessSource -match '\$baseUri\s*=\s*if\s*\(\$PermanentHttps\)\s*\{\s*\$module\.PermanentHttpsOrigin') `
    'The access test does not select the permanent origins in permanent mode.'
Assert-True ($userAccessSource -match "Key\s*=\s*'project-tracker-gateway'.*BasePath\s*=\s*'/project-tracker-api'.*ExpectationKey\s*=\s*'project-tracker'" -or
    $userAccessSource -match "Key\s*=\s*'project-tracker-gateway'.*ExpectationKey\s*=\s*'project-tracker'.*BasePath\s*=\s*'/project-tracker-api'") `
    'The access test does not validate the same-origin Project Tracker gateway with Project Tracker expectations.'
Assert-True ($userAccessSource -match 'PermanentHttps uses the production hostnames on port 443; do not also supply') `
    'The access test does not reject permanent-hostname mode combined with pilot HTTPS ports.'
Assert-True ($userAccessSource -match [regex]::Escape(
    "'{0}://{1}:{2}' -f `$Scheme, `$ServerName, `$selectedPort"
)) 'The access test no longer retains its SON-IIS2 port-based HTTP/HTTPS profiles.'

Assert-Equal (Get-ParameterDefault -Path $ShortcutScriptPath -ParameterName HubUri) `
    'https://hub.son4l.local' 'The shared shortcut does not default to the permanent Portal origin.'
Assert-Equal (Get-ParameterDefault -Path $PackageScriptPath -ParameterName HubUri) `
    'https://hub.son4l.local' 'The package builder does not default to the permanent Portal origin.'
$shortcutSource = Get-Content -LiteralPath $ShortcutScriptPath -Raw
foreach ($approvedOrigin in @(
    'https://hub.son4l.local/', 'http://son-iis2:5140/', 'https://son-iis2:6140/'
)) {
    Assert-True ($shortcutSource -match [regex]::Escape($approvedOrigin)) `
        "The direct shortcut installer does not allow approved origin '$approvedOrigin'."
}
$unsafeShortcutOriginRejected = $false
try {
    & $ShortcutScriptPath -HubUri 'https://unapproved.example.com' -WhatIf | Out-Null
}
catch { $unsafeShortcutOriginRejected = $true }
Assert-True $unsafeShortcutOriginRejected `
    'The direct shortcut installer accepted an unapproved destination.'
$bootstrapSource = Get-Content -LiteralPath $BootstrapScriptPath -Raw
Assert-True ($bootstrapSource -match "SonAeroHubInstaller\.json") `
    'The employee bootstrap does not consume the packaged Hub address.'
Assert-True ($bootstrapSource -match 'SchemaVersion\s*-ne\s*1') `
    'The employee bootstrap does not reject unsupported package configuration schemas.'
foreach ($approvedOrigin in @(
    'https://hub.son4l.local/', 'http://son-iis2:5140/', 'https://son-iis2:6140/'
)) {
    Assert-True ($bootstrapSource -match [regex]::Escape($approvedOrigin)) `
        "The employee bootstrap does not allow approved origin '$approvedOrigin'."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('sonaero-permanent-https-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $productionZip = Join-Path $testRoot 'production.zip'
    & $PackageScriptPath -OutputPath $productionZip -Confirm:$false | Out-Null
    $productionPackage = Read-ZipConfiguration $productionZip
    Assert-Equal ([string]$productionPackage.Configuration.HubUri) 'https://hub.son4l.local/' `
        'The production employee package contains the wrong Portal origin.'
    Assert-True ($productionPackage.EntryNames.Count -eq 6) 'The production employee package has an unexpected file count.'

    $pilotZip = Join-Path $testRoot 'pilot.zip'
    & $PackageScriptPath -OutputPath $pilotZip -HubUri 'http://SON-IIS2:5140' -Confirm:$false | Out-Null
    $pilotPackage = Read-ZipConfiguration $pilotZip
    Assert-Equal ([string]$pilotPackage.Configuration.HubUri) 'http://son-iis2:5140/' `
        'The employee package no longer accepts the retained HTTP rollout origin.'

    $unsafeOriginRejected = $false
    try {
        & $PackageScriptPath -OutputPath (Join-Path $testRoot 'unsafe.zip') `
            -HubUri 'https://hub.son4l.local/unapproved-path' -Confirm:$false | Out-Null
    }
    catch { $unsafeOriginRejected = $true }
    Assert-True $unsafeOriginRejected 'The package builder accepted a Hub URI with an unapproved path.'

    $externalOriginRejected = $false
    try {
        & $PackageScriptPath -OutputPath (Join-Path $testRoot 'external.zip') `
            -HubUri 'https://unapproved.example.com' -Confirm:$false | Out-Null
    }
    catch { $externalOriginRejected = $true }
    Assert-True $externalOriginRejected 'The package builder accepted an unapproved credential destination.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}

Write-Output 'PERMANENT_HTTPS_OPERATIONS_TESTS_PASSED'
