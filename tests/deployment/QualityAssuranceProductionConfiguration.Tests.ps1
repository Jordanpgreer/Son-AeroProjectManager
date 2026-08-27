[CmdletBinding()]
param(
    [string]$ModulePath = ''
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) {
    throw "These compatibility tests must run under Windows PowerShell 5.1; current version is $($PSVersionTable.PSVersion)."
}
if ([string]::IsNullOrWhiteSpace($ModulePath)) {
    $ModulePath = Join-Path $PSScriptRoot `
        '..\..\deployment\QualityAssuranceProductionConfiguration.psm1'
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Operation,
        [Parameter(Mandatory = $true)][string]$Message
    )
    $failed = $false
    try { & $Operation }
    catch { $failed = $true }
    if (-not $failed) { throw $Message }
}

function Write-TestJson {
    param([string]$Path, $Value)
    $json = $Value | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText(
        $Path,
        $json + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
}

function Copy-TestObject {
    param($Value)
    return $Value | ConvertTo-Json -Depth 30 | ConvertFrom-Json
}

function New-ValidProductionConfiguration {
    [pscustomobject]@{
        Authentication = [pscustomobject]@{ Mode = 'Windows' }
        Database = [pscustomobject]@{ Provider = 'SqlServer' }
        QualityDatabase = [pscustomobject]@{ Provider = 'SqlServer' }
        ConnectionStrings = [pscustomobject]@{
            ModuleAccessStore = 'Server=tcp:SON-SQL2,1433;Database=ProjectTracker;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=true'
            QualityStore = 'Server=tcp:SON-SQL2,1433;Database=QualityAssurance;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=true'
        }
        Portal = [pscustomobject]@{ Url = 'https://hub.son4l.local' }
        Unrelated = [pscustomobject]@{
            Keep = 'exactly'
            Nested = @('one', 'two')
        }
    }
}

$resolvedModule = (Resolve-Path $ModulePath).Path
$tokens = $null
$parseErrors = $null
$moduleAst = [Management.Automation.Language.Parser]::ParseFile(
    $resolvedModule, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Quality production configuration module has syntax errors: $($parseErrors.Message -join '; ')"
}
$moduleSource = Get-Content -LiteralPath $resolvedModule -Raw
foreach ($required in @(
    'Read-QualityProductionConfiguration',
    'New-QualityProductionDatabaseConfigurationRepair',
    'New-QualityServerLocalSqliteConfiguration',
    'Test-QualityProductionConfigurationUsesServerLocalSqlite',
    'Get-QualityServerLocalSqliteModifyRights',
    'Assert-QualityServerLocalSqliteExactAcl',
    'Assert-QualityServerLocalSqliteStorage',
    'Get-QualitySanitizedApplicationManifest',
    'Assert-QualitySanitizedApplicationManifestEqual',
    'QualityDatabase.Provider',
    'ConnectionStrings.QualityStore',
    'ServerLocalSqlite',
    'C:\ProgramData\SonAero\deployment-state\quality-assurance-data',
    'appsettings.Production.json',
    'appsettings.Development*.json',
    'SHA256',
    'ReparsePoint',
    'Modify, Synchronize'
)) {
    Assert-True $moduleSource.Contains($required) `
        "Quality production configuration module is missing fail-closed contract '$required'."
}
Assert-True ($moduleSource -match 'OrdinalIgnoreCase') `
    'Artifact manifest comparison must detect case-colliding Windows paths.'

Import-Module $resolvedModule -Force -ErrorAction Stop
foreach ($commandName in @(
    'Read-QualityProductionConfiguration',
    'New-QualityProductionDatabaseConfigurationRepair',
    'New-QualityServerLocalSqliteConfiguration',
    'Test-QualityProductionConfigurationUsesServerLocalSqlite',
    'Get-QualityServerLocalSqliteModifyRights',
    'Assert-QualityServerLocalSqliteStorage',
    'Get-QualitySanitizedApplicationManifest',
    'Assert-QualitySanitizedApplicationManifestEqual'
)) {
    Assert-True ($null -ne (Get-Command $commandName -ErrorAction SilentlyContinue)) `
        "Quality production configuration module did not export '$commandName'."
}

$expectedModifyRights = Get-QualityServerLocalSqliteModifyRights
$canonicalModifyRule = New-Object Security.AccessControl.FileSystemAccessRule(
    (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')),
    [Security.AccessControl.FileSystemRights]::Modify,
    [Security.AccessControl.AccessControlType]::Allow)
Assert-True ([long]$expectedModifyRights -eq 1245631) `
    'The approved app-pool ACL mask is not the exact Windows Modify, Synchronize value.'
Assert-True ([long]$canonicalModifyRule.FileSystemRights -eq
    [long]$expectedModifyRights) `
    'Windows canonicalized an allowed Modify ACE differently from the validated app-pool ACL mask.'
Assert-True (($expectedModifyRights -band
    [Security.AccessControl.FileSystemRights]::ChangePermissions) -eq 0 -and
    ($expectedModifyRights -band
        [Security.AccessControl.FileSystemRights]::TakeOwnership) -eq 0) `
    'The approved app-pool ACL mask admits ACL-management or ownership rights.'

$testRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ('quality-production-config-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
try {
    $aclDirectory = Join-Path $testRoot 'canonical-acl'
    New-Item -ItemType Directory -Path $aclDirectory -Force | Out-Null
    $currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $thirdSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-19')
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($currentUserSid)
    foreach ($grant in @(
        [pscustomobject]@{ Identity = $currentUserSid; Rights = [Security.AccessControl.FileSystemRights]::FullControl },
        [pscustomobject]@{ Identity = $systemSid; Rights = [Security.AccessControl.FileSystemRights]::FullControl },
        [pscustomobject]@{ Identity = $thirdSid; Rights = [Security.AccessControl.FileSystemRights]::Modify }
    )) {
        $acl.SetAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $grant.Identity, $grant.Rights, $inheritance, $propagation, $allow)))
    }
    Set-Acl -LiteralPath $aclDirectory -AclObject $acl
    $exactRights = @{}
    $exactRights[$currentUserSid.Value] = [Security.AccessControl.FileSystemRights]::FullControl
    $exactRights[$systemSid.Value] = [Security.AccessControl.FileSystemRights]::FullControl
    $exactRights[$thirdSid.Value] = Get-QualityServerLocalSqliteModifyRights
    $exactExpectation = [pscustomobject]@{
        Administrators = $currentUserSid
        Rights = $exactRights
    }
    $configurationModule = Get-Module QualityAssuranceProductionConfiguration
    & $configurationModule {
        param($Path, $Expectation)
        Assert-QualityServerLocalSqliteExactAcl `
            -Path $Path -Expectation $Expectation -Directory $true
    } $aclDirectory $exactExpectation

    $rawModifyRights = @{}
    foreach ($key in $exactRights.Keys) { $rawModifyRights[$key] = $exactRights[$key] }
    $rawModifyRights[$thirdSid.Value] = [Security.AccessControl.FileSystemRights]::Modify
    $rawModifyExpectation = [pscustomobject]@{
        Administrators = $currentUserSid
        Rights = $rawModifyRights
    }
    Assert-Throws {
        & $configurationModule {
            param($Path, $Expectation)
            Assert-QualityServerLocalSqliteExactAcl `
                -Path $Path -Expectation $Expectation -Directory $true
        } $aclDirectory $rawModifyExpectation
    } 'Exact ACL validation accepted the noncanonical raw Modify mask.'

    $broadRights = @{}
    foreach ($key in $exactRights.Keys) { $broadRights[$key] = $exactRights[$key] }
    $broadRights[$thirdSid.Value] = [Security.AccessControl.FileSystemRights]::FullControl
    $broadExpectation = [pscustomobject]@{
        Administrators = $currentUserSid
        Rights = $broadRights
    }
    Assert-Throws {
        & $configurationModule {
            param($Path, $Expectation)
            Assert-QualityServerLocalSqliteExactAcl `
                -Path $Path -Expectation $Expectation -Directory $true
        } $aclDirectory $broadExpectation
    } 'Exact ACL validation accepted broader app-pool rights.'

    $valid = New-ValidProductionConfiguration
    $validPath = Join-Path $testRoot 'valid.json'
    Write-TestJson -Path $validPath -Value $valid
    $readValid = Read-QualityProductionConfiguration -Path $validPath
    Assert-True ($readValid.QualityDatabase.Provider -ceq 'SqlServer') `
        'A valid explicit Quality SQL Server provider was not returned.'
    Assert-True (-not (Test-QualityProductionConfigurationUsesServerLocalSqlite `
        -Configuration $readValid)) `
        'A dedicated SQL Server configuration was mistaken for server-local SQLite.'

    $validBefore = [IO.File]::ReadAllBytes($validPath)
    $sqlitePlan = New-QualityServerLocalSqliteConfiguration -ActivePath $validPath
    Assert-True ((@($sqlitePlan.ChangedPaths) -join '|') -ceq
        'QualityDatabase.Provider|QualityDatabase.StorageMode|ConnectionStrings.QualityStore') `
        'Server-local SQLite transition did not report exactly the three reviewed leaves.'
    Assert-True ($sqlitePlan.Configuration.Database.Provider -ceq 'SqlServer' -and
        $sqlitePlan.Configuration.QualityDatabase.Provider -ceq 'Sqlite' -and
        $sqlitePlan.Configuration.QualityDatabase.StorageMode -ceq 'ServerLocalSqlite') `
        'Server-local SQLite transition changed the shared provider or did not select the explicit Quality storage mode.'
    $approvedSqliteStore = 'Data Source=C:\ProgramData\SonAero\deployment-state\quality-assurance-data\quality-assurance.db;Mode=ReadWrite;Default Timeout=30;Foreign Keys=True;Pooling=True'
    Assert-True ($sqlitePlan.Configuration.ConnectionStrings.QualityStore -ceq
        $approvedSqliteStore -and
        $sqlitePlan.DataDirectory -ceq 'C:\ProgramData\SonAero\deployment-state\quality-assurance-data' -and
        $sqlitePlan.DataFile -ceq
            'C:\ProgramData\SonAero\deployment-state\quality-assurance-data\quality-assurance.db') `
        'Server-local SQLite transition did not use the exact approved persistent path and options.'
    $sqliteOutsideApprovedLeaves = Copy-TestObject $sqlitePlan.Configuration
    $sqliteOutsideApprovedLeaves.QualityDatabase.Provider = 'SqlServer'
    $sqliteOutsideApprovedLeaves.QualityDatabase.PSObject.Properties.Remove('StorageMode')
    $sqliteOutsideApprovedLeaves.ConnectionStrings.QualityStore =
        $valid.ConnectionStrings.QualityStore
    Assert-True (($sqliteOutsideApprovedLeaves | ConvertTo-Json -Depth 30 -Compress) -ceq
        ($valid | ConvertTo-Json -Depth 30 -Compress)) `
        'Server-local SQLite transition changed configuration outside the three reviewed leaves.'
    Assert-True ($sqlitePlan.Utf8Bytes -is [byte[]] -and
        $sqlitePlan.Utf8Bytes.Length -gt 0 -and
        -not ($sqlitePlan.Utf8Bytes.Length -ge 3 -and
            $sqlitePlan.Utf8Bytes[0] -eq 0xEF -and
            $sqlitePlan.Utf8Bytes[1] -eq 0xBB -and
            $sqlitePlan.Utf8Bytes[2] -eq 0xBF)) `
        'Server-local SQLite transition did not return BOM-free UTF-8 candidate bytes.'
    $secondSqlitePlan = New-QualityServerLocalSqliteConfiguration -ActivePath $validPath
    Assert-True ([Convert]::ToBase64String($sqlitePlan.Utf8Bytes) -ceq
        [Convert]::ToBase64String($secondSqlitePlan.Utf8Bytes)) `
        'Server-local SQLite transition bytes are not deterministic for identical input.'
    Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($validPath)) -ceq
        [Convert]::ToBase64String($validBefore)) `
        'Server-local SQLite planning modified the active Production configuration.'
    $sqliteCandidatePath = Join-Path $testRoot 'sqlite-candidate.json'
    [IO.File]::WriteAllBytes($sqliteCandidatePath, [byte[]]$sqlitePlan.Utf8Bytes)
    $readSqlite = Read-QualityProductionConfiguration -Path $sqliteCandidatePath
    Assert-True ($readSqlite.QualityDatabase.Provider -ceq 'Sqlite' -and
        $readSqlite.QualityDatabase.StorageMode -ceq 'ServerLocalSqlite' -and
        $readSqlite.ConnectionStrings.QualityStore -ceq $approvedSqliteStore) `
        'The validated server-local SQLite candidate could not be read back safely.'
    Assert-True (Test-QualityProductionConfigurationUsesServerLocalSqlite `
        -Configuration $readSqlite) `
        'The explicit server-local SQLite configuration was not detected.'
    Assert-Throws {
        [void](New-QualityServerLocalSqliteConfiguration `
            -ActivePath $sqliteCandidatePath)
    } 'An already-transitioned SQLite configuration accepted a second transition.'

    $templatePath = Join-Path $testRoot 'template.json'
    Write-TestJson -Path $templatePath -Value $valid

    $legacy = Copy-TestObject $valid
    $legacy.PSObject.Properties.Remove('QualityDatabase')
    $legacy.ConnectionStrings.PSObject.Properties.Remove('QualityStore')
    $legacyPath = Join-Path $testRoot 'legacy.json'
    Write-TestJson -Path $legacyPath -Value $legacy
    $legacyBefore = [IO.File]::ReadAllBytes($legacyPath)

    $repair = New-QualityProductionDatabaseConfigurationRepair `
        -ActivePath $legacyPath `
        -TemplatePath $templatePath
    Assert-True ((@($repair.AddedPaths) -join '|') -ceq
        'QualityDatabase.Provider|ConnectionStrings.QualityStore') `
        'Candidate-only repair did not report exactly the two approved missing leaves.'
    Assert-True ($repair.Configuration.QualityDatabase.Provider -ceq 'SqlServer') `
        'Candidate-only repair did not add the explicit Quality SQL Server provider.'
    Assert-True ($repair.Configuration.ConnectionStrings.QualityStore -ceq
        $valid.ConnectionStrings.QualityStore) `
        'Candidate-only repair did not use the approved QualityStore template value.'
    Assert-True ($repair.Configuration.Database.Provider -ceq 'SqlServer' -and
        $repair.Configuration.ConnectionStrings.ModuleAccessStore -ceq
            $legacy.ConnectionStrings.ModuleAccessStore -and
        $repair.Configuration.Portal.Url -ceq $legacy.Portal.Url -and
        $repair.Configuration.Unrelated.Keep -ceq 'exactly' -and
        (@($repair.Configuration.Unrelated.Nested) -join '|') -ceq 'one|two') `
        'Candidate-only repair changed configuration outside the two approved leaves.'
    $repairedWithoutApprovedLeaves = Copy-TestObject $repair.Configuration
    $repairedWithoutApprovedLeaves.PSObject.Properties.Remove('QualityDatabase')
    $repairedWithoutApprovedLeaves.ConnectionStrings.PSObject.Properties.Remove('QualityStore')
    Assert-True (($repairedWithoutApprovedLeaves | ConvertTo-Json -Depth 30 -Compress) -ceq
        ($legacy | ConvertTo-Json -Depth 30 -Compress)) `
        'Candidate-only repair did not preserve every property outside the two approved leaves.'
    Assert-True ($repair.Utf8Bytes -is [byte[]] -and $repair.Utf8Bytes.Length -gt 0) `
        'Candidate-only repair did not return deterministic UTF-8 candidate bytes.'
    Assert-True (-not ($repair.Utf8Bytes.Length -ge 3 -and
        $repair.Utf8Bytes[0] -eq 0xEF -and $repair.Utf8Bytes[1] -eq 0xBB -and
        $repair.Utf8Bytes[2] -eq 0xBF)) `
        'Candidate-only repair returned a UTF-8 BOM that could change deployment behavior.'
    $secondRepair = New-QualityProductionDatabaseConfigurationRepair `
        -ActivePath $legacyPath `
        -TemplatePath $templatePath
    Assert-True ([Convert]::ToBase64String($repair.Utf8Bytes) -ceq
        [Convert]::ToBase64String($secondRepair.Utf8Bytes)) `
        'Candidate-only repair bytes are not deterministic for identical inputs.'
    $repairedFromBytes = [Text.Encoding]::UTF8.GetString($repair.Utf8Bytes) | ConvertFrom-Json
    Assert-True ($repairedFromBytes.QualityDatabase.Provider -ceq 'SqlServer' -and
        $repairedFromBytes.ConnectionStrings.QualityStore -ceq
            $valid.ConnectionStrings.QualityStore) `
        'Candidate-only repair bytes do not contain the validated repaired configuration.'
    Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($legacyPath)) -ceq
        [Convert]::ToBase64String($legacyBefore)) `
        'Repair planning modified the active production configuration file.'

    $unsafeConfigurations = New-Object System.Collections.Generic.List[object]
    foreach ($mutation in @(
        { param($c) $c.Authentication.Mode = 'Development' },
        { param($c) $c.Database.Provider = 'Sqlite' },
        { param($c) $c.QualityDatabase.Provider = 'Sqlite' },
        { param($c) $c.QualityDatabase | Add-Member -NotePropertyName StorageMode -NotePropertyValue 'ServerLocalSqlite' },
        { param($c) $c.QualityDatabase.Provider = 'Sqlite'; $c.QualityDatabase | Add-Member -NotePropertyName StorageMode -NotePropertyValue 'Unknown'; $c.ConnectionStrings.QualityStore = $approvedSqliteStore },
        { param($c) $c.QualityDatabase.Provider = 'Sqlite'; $c.QualityDatabase | Add-Member -NotePropertyName StorageMode -NotePropertyValue 'ServerLocalSqlite'; $c.ConnectionStrings.QualityStore = 'Data Source=quality-assurance.db;Mode=ReadWrite;Default Timeout=30;Foreign Keys=True;Pooling=True' },
        { param($c) $c.QualityDatabase.Provider = 'Sqlite'; $c.QualityDatabase | Add-Member -NotePropertyName StorageMode -NotePropertyValue 'ServerLocalSqlite'; $c.ConnectionStrings.QualityStore = $approvedSqliteStore + ';Cache=Shared' },
        { param($c) $c.QualityDatabase.Provider = 'Sqlite'; $c.QualityDatabase | Add-Member -NotePropertyName StorageMode -NotePropertyValue 'ServerLocalSqlite'; $c.QualityDatabase | Add-Member -NotePropertyName Unexpected -NotePropertyValue 'unsafe'; $c.ConnectionStrings.QualityStore = $approvedSqliteStore },
        { param($c) $c.QualityDatabase.Provider = '' },
        { param($c) $c.ConnectionStrings.ModuleAccessStore = 'Data Source=project-tracker-dev.db' },
        { param($c) $c.ConnectionStrings.ModuleAccessStore = 'Server=tcp:OTHER,1433;Database=ProjectTracker;Integrated Security=True;Encrypt=True;TrustServerCertificate=True' },
        { param($c) $c.ConnectionStrings.QualityStore = 'Data Source=quality-assurance-dev.db' },
        { param($c) $c.ConnectionStrings.QualityStore = 'Server=tcp:SON-SQL2,1433;Database=Wrong;Integrated Security=True;Encrypt=True;TrustServerCertificate=True' },
        { param($c) $c.ConnectionStrings.QualityStore = 'Server=tcp:SON-SQL2,1433;Database=QualityAssurance;User ID=user;Password=secret;Encrypt=True;TrustServerCertificate=True' },
        { param($c) $c.ConnectionStrings.QualityStore = 'Server=tcp:SON-SQL2,1433;Database=QualityAssurance;Integrated Security=True;Encrypt=False;TrustServerCertificate=True' },
        { param($c) $c.ConnectionStrings.QualityStore = 'Server=tcp:SON-SQL2,1433;Database=QualityAssurance;Integrated Security=True;Encrypt=True;TrustServerCertificate=False' },
        { param($c) $c.ConnectionStrings.QualityStore = 'Server=tcp:SON-SQL2,1433;Database=QualityAssurance;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;AttachDbFilename=C:\unsafe.mdf' }
    )) {
        $candidate = Copy-TestObject $valid
        & $mutation $candidate
        $unsafeConfigurations.Add($candidate)
    }
    $unsafeIndex = 0
    foreach ($unsafe in $unsafeConfigurations) {
        $unsafeIndex++
        $unsafePath = Join-Path $testRoot "unsafe-$unsafeIndex.json"
        Write-TestJson -Path $unsafePath -Value $unsafe
        Assert-Throws { [void](Read-QualityProductionConfiguration -Path $unsafePath) } `
            "Unsafe Quality production configuration $unsafeIndex was accepted."
    }

    # Repair is deliberately all-or-nothing. Partial, blank, conflicting, or already-valid
    # states must not be silently normalized by the emergency candidate-only mode.
    $invalidRepairInputs = New-Object System.Collections.Generic.List[object]
    $missingProviderOnly = Copy-TestObject $valid
    $missingProviderOnly.PSObject.Properties.Remove('QualityDatabase')
    $invalidRepairInputs.Add($missingProviderOnly)
    $missingStoreOnly = Copy-TestObject $valid
    $missingStoreOnly.ConnectionStrings.PSObject.Properties.Remove('QualityStore')
    $invalidRepairInputs.Add($missingStoreOnly)
    $blankProvider = Copy-TestObject $legacy
    $blankProvider | Add-Member -NotePropertyName QualityDatabase `
        -NotePropertyValue ([pscustomobject]@{ Provider = '' })
    $invalidRepairInputs.Add($blankProvider)
    $blankStore = Copy-TestObject $legacy
    $blankStore.ConnectionStrings | Add-Member -NotePropertyName QualityStore `
        -NotePropertyValue ''
    $invalidRepairInputs.Add($blankStore)
    $conflictingQualityObject = Copy-TestObject $legacy
    $conflictingQualityObject | Add-Member -NotePropertyName QualityDatabase `
        -NotePropertyValue ([pscustomobject]@{ Unexpected = 'must-not-be-dropped' })
    $invalidRepairInputs.Add($conflictingQualityObject)
    $invalidRepairInputs.Add((Copy-TestObject $valid))

    $repairIndex = 0
    foreach ($invalidRepair in $invalidRepairInputs) {
        $repairIndex++
        $invalidRepairPath = Join-Path $testRoot "invalid-repair-$repairIndex.json"
        Write-TestJson -Path $invalidRepairPath -Value $invalidRepair
        Assert-Throws {
            [void](New-QualityProductionDatabaseConfigurationRepair `
                -ActivePath $invalidRepairPath `
                -TemplatePath $templatePath)
        } "Partial, blank, conflicting, or unnecessary repair state $repairIndex was accepted."
    }

    $invalidTemplate = Copy-TestObject $valid
    $invalidTemplate.ConnectionStrings.QualityStore = 'Data Source=quality-assurance-dev.db'
    $invalidTemplatePath = Join-Path $testRoot 'invalid-template.json'
    Write-TestJson -Path $invalidTemplatePath -Value $invalidTemplate
    Assert-Throws {
        [void](New-QualityProductionDatabaseConfigurationRepair `
            -ActivePath $legacyPath `
            -TemplatePath $invalidTemplatePath)
    } 'Candidate-only repair accepted an unsafe production template.'

    $sourceRoot = Join-Path $testRoot 'source'
    $candidateRoot = Join-Path $testRoot 'candidate'
    New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'nested') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $candidateRoot 'nested') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $sourceRoot 'QualityAssurance.Api.dll'), 'binary-one')
    [IO.File]::WriteAllText((Join-Path $candidateRoot 'QualityAssurance.Api.dll'), 'binary-one')
    [IO.File]::WriteAllText((Join-Path $sourceRoot 'nested\asset.js'), 'asset-one')
    [IO.File]::WriteAllText((Join-Path $candidateRoot 'nested\asset.js'), 'asset-one')
    [IO.File]::WriteAllText((Join-Path $sourceRoot 'appsettings.Production.json'), 'source production')
    [IO.File]::WriteAllText((Join-Path $candidateRoot 'appsettings.Production.json'), 'repaired production')
    [IO.File]::WriteAllText((Join-Path $sourceRoot 'appsettings.Development.json'), 'excluded')

    $sourceManifest = @(Get-QualitySanitizedApplicationManifest -Root $sourceRoot)
    Assert-True ($sourceManifest.Count -eq 2) `
        'Sanitized manifest did not exclude only the Production and Development settings.'
    Assert-True (@($sourceManifest | Where-Object {
        $_.RelativePath -match '(?i)appsettings\.(?:Production|Development)'
    }).Count -eq 0) 'Sanitized manifest included excluded environment settings.'
    Assert-True (@($sourceManifest | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.RelativePath) -or
        [string]::IsNullOrWhiteSpace([string]$_.Sha256) -or
        $null -eq $_.Length
    }).Count -eq 0) 'Sanitized manifest omitted relative path, length, or SHA-256 data.'
    [string[]]$manifestPaths = @($sourceManifest | ForEach-Object { [string]$_.RelativePath })
    [string[]]$sortedManifestPaths = @($manifestPaths)
    [Array]::Sort($sortedManifestPaths, [StringComparer]::Ordinal)
    Assert-True (($manifestPaths -join '|') -ceq ($sortedManifestPaths -join '|')) `
        'Sanitized manifest records are not returned in deterministic ordinal path order.'
    $assemblyRecord = @($sourceManifest | Where-Object {
        $_.RelativePath -ceq 'QualityAssurance.Api.dll'
    })[0]
    $assemblyPath = Join-Path $sourceRoot 'QualityAssurance.Api.dll'
    Assert-True ($assemblyRecord.Length -eq (Get-Item -LiteralPath $assemblyPath).Length -and
        $assemblyRecord.Sha256 -ceq
            (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash) `
        'Sanitized manifest did not bind each path to its exact byte length and SHA-256 hash.'
    Assert-QualitySanitizedApplicationManifestEqual `
        -SourceRoot $sourceRoot `
        -CandidateRoot $candidateRoot

    [IO.File]::WriteAllText((Join-Path $candidateRoot 'nested\asset.js'), 'asset-two')
    Assert-Throws {
        Assert-QualitySanitizedApplicationManifestEqual `
            -SourceRoot $sourceRoot `
            -CandidateRoot $candidateRoot
    } 'Artifact manifest accepted same-length content drift.'
    [IO.File]::WriteAllText((Join-Path $candidateRoot 'nested\asset.js'), 'asset-one')
    [IO.File]::WriteAllText((Join-Path $candidateRoot 'unexpected.bin'), 'extra')
    Assert-Throws {
        Assert-QualitySanitizedApplicationManifestEqual `
            -SourceRoot $sourceRoot `
            -CandidateRoot $candidateRoot
    } 'Artifact manifest accepted an extra candidate file.'
    Remove-Item -LiteralPath (Join-Path $candidateRoot 'unexpected.bin') -Force
    Remove-Item -LiteralPath (Join-Path $candidateRoot 'nested\asset.js') -Force
    Assert-Throws {
        Assert-QualitySanitizedApplicationManifestEqual `
            -SourceRoot $sourceRoot `
            -CandidateRoot $candidateRoot
    } 'Artifact manifest accepted a missing candidate file.'

    [IO.File]::WriteAllText((Join-Path $candidateRoot 'nested\asset.js'), 'asset-one')
    [IO.File]::WriteAllText((Join-Path $sourceRoot 'appsettings.Staging.json'), 'source')
    [IO.File]::WriteAllText((Join-Path $candidateRoot 'appsettings.Staging.json'), 'changed')
    Assert-Throws {
        Assert-QualitySanitizedApplicationManifestEqual `
            -SourceRoot $sourceRoot `
            -CandidateRoot $candidateRoot
    } 'Artifact manifest excluded an unapproved appsettings.Staging.json file.'
}
finally {
    Remove-Module QualityAssuranceProductionConfiguration -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Output 'QUALITY_ASSURANCE_PRODUCTION_CONFIGURATION_TESTS_PASSED'
