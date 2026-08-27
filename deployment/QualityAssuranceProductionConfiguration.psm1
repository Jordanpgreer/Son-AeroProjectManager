$ErrorActionPreference = 'Stop'

$script:ExpectedSqlServer = 'tcp:SON-SQL2,1433'
$script:ExpectedModuleAccessDatabase = 'ProjectTracker'
$script:ExpectedQualityDatabase = 'QualityAssurance'
$script:ServerLocalSqliteStorageMode = 'ServerLocalSqlite'
$script:ServerLocalSqliteDataDirectory = `
    'C:\ProgramData\SonAero\deployment-state\quality-assurance-data'
$script:ServerLocalSqliteDataFile = Join-Path `
    $script:ServerLocalSqliteDataDirectory 'quality-assurance.db'
$script:ServerLocalSqliteConnectionString =
    "Data Source=$($script:ServerLocalSqliteDataFile);Mode=ReadWrite;Default Timeout=30;Foreign Keys=True;Pooling=True"

function Read-JsonObject {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    try {
        $content = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($content)) { throw 'The file is empty.' }
        $value = $content | ConvertFrom-Json -ErrorAction Stop
    }
    catch { throw "Invalid $Label JSON at '$Path': $($_.Exception.Message)" }
    if ($null -eq $value -or
        $value.GetType() -ne [System.Management.Automation.PSCustomObject]) {
        throw "$Label JSON must contain one object: $Path"
    }
    return $value
}

function Assert-ApprovedSqlConnectionString {
    param(
        [Parameter(Mandatory = $true)][string]$ConnectionString,
        [Parameter(Mandatory = $true)][string]$ExpectedDatabase,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw "$Label connection string is missing."
    }

    $rawBuilder = New-Object System.Data.Common.DbConnectionStringBuilder
    try {
        $rawBuilder.set_ConnectionString($ConnectionString)
        $sqlBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    }
    catch { throw "$Label connection string is invalid: $($_.Exception.Message)" }

    $allowedKeys = @(
        'server', 'datasource', 'database', 'initialcatalog', 'trustedconnection',
        'integratedsecurity', 'encrypt', 'trustservercertificate', 'multipleactiveresultsets'
    )
    $normalizedKeys = @()
    foreach ($key in @($rawBuilder.Keys)) {
        $normalizedKey = ([string]$key -replace '[ _]', '').ToLowerInvariant()
        if ($normalizedKey -notin $allowedKeys) {
            throw "$Label connection string contains unsupported key '$key'."
        }
        $normalizedKeys += $normalizedKey
    }
    $requiredKeyGroups = @(
        @('server', 'datasource'), @('database', 'initialcatalog'),
        @('trustedconnection', 'integratedsecurity'), @('encrypt'),
        @('trustservercertificate'), @('multipleactiveresultsets')
    )
    if ($normalizedKeys.Count -ne $requiredKeyGroups.Count) {
        throw "$Label connection string must contain exactly the six approved settings."
    }
    foreach ($group in $requiredKeyGroups) {
        if (@($normalizedKeys | Where-Object { $_ -in $group }).Count -ne 1) {
            throw "$Label connection string must contain exactly one '$($group -join '/')' setting."
        }
    }

    if ([string]$sqlBuilder.DataSource -ine $script:ExpectedSqlServer) {
        throw "$Label must target '$($script:ExpectedSqlServer)', not '$($sqlBuilder.DataSource)'."
    }
    if ([string]$sqlBuilder.InitialCatalog -ine $ExpectedDatabase) {
        throw "$Label must target database '$ExpectedDatabase', not '$($sqlBuilder.InitialCatalog)'."
    }
    if (-not $sqlBuilder.IntegratedSecurity -or
        -not [string]::IsNullOrWhiteSpace([string]$sqlBuilder.UserID) -or
        -not [string]::IsNullOrWhiteSpace([string]$sqlBuilder.Password)) {
        throw "$Label must use integrated security without a user ID or password."
    }
    if (-not $sqlBuilder.Encrypt -or -not $sqlBuilder.TrustServerCertificate -or
        -not $sqlBuilder.MultipleActiveResultSets -or
        -not [string]::IsNullOrWhiteSpace([string]$sqlBuilder.AttachDBFilename)) {
        throw "$Label must enable Encrypt, TrustServerCertificate, and MultipleActiveResultSets and must not attach a file."
    }
}

function Assert-QualityProductionConfigurationObject {
    param(
        [Parameter(Mandatory = $true)]$Configuration,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ([string]$Configuration.Authentication.Mode -cne 'Windows') {
        throw "$Label must use Windows authentication."
    }
    if ([string]$Configuration.Database.Provider -cne 'SqlServer') {
        throw "$Label must use SQL Server for the shared access database provider."
    }
    if ($null -eq $Configuration.QualityDatabase -or
        $Configuration.QualityDatabase.GetType() -ne
            [System.Management.Automation.PSCustomObject]) {
        throw "$Label must contain one QualityDatabase object."
    }
    $qualityDatabaseProperties = @($Configuration.QualityDatabase.PSObject.Properties)
    $unexpectedQualityDatabaseProperties = @($qualityDatabaseProperties | Where-Object {
        $_.Name -notin @('Provider', 'StorageMode')
    })
    if ($unexpectedQualityDatabaseProperties.Count -gt 0) {
        throw "$Label QualityDatabase contains unsupported setting '$($unexpectedQualityDatabaseProperties[0].Name)'."
    }
    if ($null -eq $Configuration.ConnectionStrings -or
        $Configuration.ConnectionStrings.GetType() -ne
            [System.Management.Automation.PSCustomObject]) {
        throw "$Label must contain a ConnectionStrings object."
    }
    Assert-ApprovedSqlConnectionString `
        -ConnectionString ([string]$Configuration.ConnectionStrings.ModuleAccessStore) `
        -ExpectedDatabase $script:ExpectedModuleAccessDatabase -Label "$Label ModuleAccessStore"
    $storageModeProperty = $Configuration.QualityDatabase.PSObject.Properties['StorageMode']
    if ($null -eq $storageModeProperty) {
        if ([string]$Configuration.QualityDatabase.Provider -cne 'SqlServer') {
            throw "$Label must use SQL Server for Quality data unless the reviewed server-local SQLite storage mode is explicit."
        }
        Assert-ApprovedSqlConnectionString `
            -ConnectionString ([string]$Configuration.ConnectionStrings.QualityStore) `
            -ExpectedDatabase $script:ExpectedQualityDatabase -Label "$Label QualityStore"
        return
    }

    if ([string]$storageModeProperty.Value -cne $script:ServerLocalSqliteStorageMode -or
        [string]$Configuration.QualityDatabase.Provider -cne 'Sqlite') {
        throw "$Label QualityDatabase.StorageMode and Provider do not select the reviewed server-local SQLite mode."
    }
    if ([string]$Configuration.ConnectionStrings.QualityStore -cne
        $script:ServerLocalSqliteConnectionString) {
        throw "$Label server-local SQLite QualityStore must use the exact approved persistent ProgramData path and options."
    }
}

function Read-QualityProductionConfiguration {
    param([Parameter(Mandatory = $true)][string]$Path)

    $configuration = Read-JsonObject -Path $Path -Label 'Quality Production'
    Assert-QualityProductionConfigurationObject -Configuration $configuration `
        -Label "Quality Production configuration '$Path'"
    return $configuration
}

function Assert-RepairableQualityProductionConfiguration {
    param(
        [Parameter(Mandatory = $true)]$Configuration,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $label = "Active Quality Production configuration '$Path'"
    if ([string]$Configuration.Authentication.Mode -cne 'Windows') {
        throw "$label must use Windows authentication."
    }
    if ([string]$Configuration.Database.Provider -cne 'SqlServer') {
        throw "$label must use SQL Server for the shared database provider."
    }
    if ($null -eq $Configuration.ConnectionStrings -or
        $Configuration.ConnectionStrings.GetType() -ne
            [System.Management.Automation.PSCustomObject]) {
        throw "$label must contain a ConnectionStrings object."
    }
    Assert-ApprovedSqlConnectionString `
        -ConnectionString ([string]$Configuration.ConnectionStrings.ModuleAccessStore) `
        -ExpectedDatabase $script:ExpectedModuleAccessDatabase -Label "$label ModuleAccessStore"

    $qualityDatabaseProperty = $Configuration.PSObject.Properties['QualityDatabase']
    if ($null -ne $qualityDatabaseProperty) {
        if ($null -eq $qualityDatabaseProperty.Value -or
            $qualityDatabaseProperty.Value.GetType() -ne
                [System.Management.Automation.PSCustomObject]) {
            throw "$label has an invalid QualityDatabase value; repair requires an absent or empty object."
        }
        $children = @($qualityDatabaseProperty.Value.PSObject.Properties)
        if ($children.Count -ne 0) {
            throw "$label contains QualityDatabase settings; repair requires Provider and all other children to be absent."
        }
    }

    if ($null -ne $Configuration.ConnectionStrings.PSObject.Properties['QualityStore']) {
        throw "$label contains ConnectionStrings.QualityStore; repair requires that leaf to be genuinely absent."
    }
}

function New-QualityProductionDatabaseConfigurationRepair {
    param(
        [Parameter(Mandatory = $true)][string]$ActivePath,
        [Parameter(Mandatory = $true)][string]$TemplatePath
    )

    $active = Read-JsonObject -Path $ActivePath -Label 'active Quality Production'
    Assert-RepairableQualityProductionConfiguration -Configuration $active -Path $ActivePath
    $template = Read-QualityProductionConfiguration -Path $TemplatePath

    $clone = ($active | ConvertTo-Json -Depth 100) | ConvertFrom-Json -ErrorAction Stop
    if ($null -eq $clone.PSObject.Properties['QualityDatabase']) {
        $clone | Add-Member -NotePropertyName QualityDatabase -NotePropertyValue ([pscustomobject]@{})
    }
    $clone.QualityDatabase | Add-Member -NotePropertyName Provider `
        -NotePropertyValue ([string]$template.QualityDatabase.Provider)
    $clone.ConnectionStrings | Add-Member -NotePropertyName QualityStore `
        -NotePropertyValue ([string]$template.ConnectionStrings.QualityStore)

    Assert-QualityProductionConfigurationObject -Configuration $clone `
        -Label 'Repaired Quality Production configuration'
    $json = ($clone | ConvertTo-Json -Depth 100) + [Environment]::NewLine
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json)
    return [pscustomobject]@{
        Configuration = $clone
        Utf8Bytes = $bytes
        AddedPaths = @('QualityDatabase.Provider', 'ConnectionStrings.QualityStore')
    }
}

function New-QualityServerLocalSqliteConfiguration {
    param([Parameter(Mandatory = $true)][string]$ActivePath)

    $active = Read-JsonObject -Path $ActivePath -Label 'active Quality Production'
    Assert-QualityProductionConfigurationObject -Configuration $active `
        -Label "Active Quality Production configuration '$ActivePath'"
    if ($null -ne $active.QualityDatabase.PSObject.Properties['StorageMode']) {
        throw "Active Quality Production configuration '$ActivePath' already uses server-local SQLite. Omit the transition switch for ordinary deployments."
    }

    $clone = ($active | ConvertTo-Json -Depth 100) | ConvertFrom-Json -ErrorAction Stop
    $clone.QualityDatabase.Provider = 'Sqlite'
    $clone.QualityDatabase | Add-Member -NotePropertyName StorageMode `
        -NotePropertyValue $script:ServerLocalSqliteStorageMode
    $clone.ConnectionStrings.QualityStore = $script:ServerLocalSqliteConnectionString

    Assert-QualityProductionConfigurationObject -Configuration $clone `
        -Label 'Server-local SQLite Quality Production configuration'
    $json = ($clone | ConvertTo-Json -Depth 100) + [Environment]::NewLine
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json)
    return [pscustomobject]@{
        Configuration = $clone
        Utf8Bytes = $bytes
        DataDirectory = $script:ServerLocalSqliteDataDirectory
        DataFile = $script:ServerLocalSqliteDataFile
        ChangedPaths = @(
            'QualityDatabase.Provider',
            'QualityDatabase.StorageMode',
            'ConnectionStrings.QualityStore'
        )
    }
}

function Test-QualityProductionConfigurationUsesServerLocalSqlite {
    param([Parameter(Mandatory = $true)]$Configuration)

    return $null -ne $Configuration.QualityDatabase -and
        [string]$Configuration.QualityDatabase.Provider -ceq 'Sqlite' -and
        $null -ne $Configuration.QualityDatabase.PSObject.Properties['StorageMode'] -and
        [string]$Configuration.QualityDatabase.StorageMode -ceq
            $script:ServerLocalSqliteStorageMode
}

function Get-QualityServerLocalSqliteAclExpectation {
    param([Parameter(Mandatory = $true)][string]$PoolName)

    $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    try {
        $poolIdentity = (New-Object Security.Principal.NTAccount(
            'IIS AppPool', $PoolName)).Translate(
                [Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "Unable to resolve the Quality IIS application-pool identity: $($_.Exception.Message)"
    }
    $rights = @{}
    $rights[$administrators.Value] = [Security.AccessControl.FileSystemRights]::FullControl
    $rights[$system.Value] = [Security.AccessControl.FileSystemRights]::FullControl
    $rights[$poolIdentity.Value] = [Security.AccessControl.FileSystemRights]::Modify
    return [pscustomobject]@{
        Administrators = $administrators
        Rights = $rights
    }
}

function Assert-QualityServerLocalSqliteExactAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Expectation,
        [Parameter(Mandatory = $true)][bool]$Directory
    )

    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    if (-not $acl.AreAccessRulesProtected -or
        $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne
            $Expectation.Administrators.Value) {
        throw "Quality SQLite path ownership or inheritance protection is incorrect: $Path"
    }
    $rules = @($acl.GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne $Expectation.Rights.Count) {
        throw "Quality SQLite path must contain exactly three protected access rules: $Path"
    }
    $expectedInheritance = if ($Directory) {
        [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    }
    else { [Security.AccessControl.InheritanceFlags]::None }
    foreach ($rule in $rules) {
        $sid = $rule.IdentityReference.Value
        if (-not $Expectation.Rights.ContainsKey($sid) -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.IsInherited -or
            [long]$rule.FileSystemRights -ne [long]$Expectation.Rights[$sid] -or
            $rule.InheritanceFlags -ne $expectedInheritance -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
            throw "Quality SQLite path contains an unexpected access rule for '$sid': $Path"
        }
    }
}

function Assert-QualityServerLocalSqliteStorage {
    param([string]$PoolName = 'QualityAssurance')

    $protectedRoot = 'C:\ProgramData\SonAero\deployment-state'
    $protectedRootItem = Get-Item -LiteralPath $protectedRoot -Force -ErrorAction Stop
    if (-not $protectedRootItem.PSIsContainer -or
        ($protectedRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Protected deployment-state root is missing, is not a directory, or is a reparse point: $protectedRoot"
    }
    $protectedRootAcl = Get-Acl -LiteralPath $protectedRoot -ErrorAction Stop
    $protectedRootAllowedSids = @('S-1-5-18', 'S-1-5-32-544')
    $protectedRootOwner = $protectedRootAcl.GetOwner(
        [Security.Principal.SecurityIdentifier]).Value
    $protectedRootRules = @($protectedRootAcl.GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier]))
    $protectedRootInheritance =
        [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    if (-not $protectedRootAcl.AreAccessRulesProtected -or
        $protectedRootOwner -notin $protectedRootAllowedSids -or
        $protectedRootRules.Count -ne 2) {
        throw "Protected deployment-state root has an unsafe owner, inheritance state, or rule count: $protectedRoot"
    }
    foreach ($rule in $protectedRootRules) {
        if ($rule.IdentityReference.Value -notin $protectedRootAllowedSids -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.IsInherited -or
            [long]$rule.FileSystemRights -ne
                [long][Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne $protectedRootInheritance -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
            throw "Protected deployment-state root contains an unsafe access rule: $protectedRoot"
        }
    }

    $dataDirectory = [IO.Path]::GetFullPath(
        $script:ServerLocalSqliteDataDirectory).TrimEnd('\')
    $currentPath = [IO.Path]::GetPathRoot($dataDirectory)
    $relativePath = $dataDirectory.Substring($currentPath.Length)
    foreach ($segment in @($relativePath.Split('\') | Where-Object { $_.Length -gt 0 })) {
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            throw "Quality SQLite data path is missing: $currentPath"
        }
        $item = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not $item.PSIsContainer) {
            throw "Quality SQLite data path component is not a regular directory: $currentPath"
        }
    }

    $allowedNames = @(
        'quality-assurance.db',
        'quality-assurance.db-journal',
        'quality-assurance.db-shm',
        'quality-assurance.db-wal'
    )
    $items = @(Get-ChildItem -LiteralPath $dataDirectory -Force -ErrorAction Stop)
    foreach ($item in $items) {
        if ($item.Name -cnotin $allowedNames -or $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Quality SQLite data directory contains an unapproved item: $($item.FullName)"
        }
    }
    $dataFileItem = @($items | Where-Object Name -CEQ 'quality-assurance.db')
    if ($dataFileItem.Count -ne 1 -or $dataFileItem[0].Length -le 0) {
        throw "Quality SQLite database is missing or empty: $($script:ServerLocalSqliteDataFile)"
    }

    $expectation = Get-QualityServerLocalSqliteAclExpectation -PoolName $PoolName
    Assert-QualityServerLocalSqliteExactAcl -Path $dataDirectory `
        -Expectation $expectation -Directory $true
    Assert-QualityServerLocalSqliteExactAcl -Path $script:ServerLocalSqliteDataFile `
        -Expectation $expectation -Directory $false
    return [pscustomobject]@{
        DataDirectory = $dataDirectory
        DataFile = $script:ServerLocalSqliteDataFile
        DataFileLength = [long]$dataFileItem[0].Length
    }
}

function Get-QualitySanitizedApplicationManifest {
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) {
        throw "Application root does not exist: $rootPath"
    }
    $rootItem = Get-Item -LiteralPath $rootPath -Force -ErrorAction Stop
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Application root must not be a reparse point: $rootPath"
    }

    $seenPaths = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $directories = New-Object 'Collections.Generic.Queue[IO.DirectoryInfo]'
    $directories.Enqueue([IO.DirectoryInfo]$rootItem)
    $records = New-Object 'Collections.Generic.SortedDictionary[string,object]' ([StringComparer]::Ordinal)
    while ($directories.Count -gt 0) {
        $directory = $directories.Dequeue()
        foreach ($item in $directory.GetFileSystemInfos()) {
            $relativePath = $item.FullName.Substring($rootPath.Length).TrimStart('\')
            if (-not $seenPaths.Add($relativePath)) {
                throw "Application tree contains case-colliding path '$relativePath'."
            }
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Application tree contains reparse point '$relativePath'."
            }
            if ($item -is [IO.DirectoryInfo]) {
                $directories.Enqueue($item)
                continue
            }
            if ($item.Name -ieq 'appsettings.Production.json' -or
                $item.Name -like 'appsettings.Development*.json') { continue }
            $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
            $records.Add($relativePath, [pscustomobject]@{
                RelativePath = $relativePath
                Length = [long]$item.Length
                Sha256 = $hash
            })
        }
    }
    return @($records.Values)
}

function Assert-QualitySanitizedApplicationManifestEqual {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$CandidateRoot
    )

    $source = @(Get-QualitySanitizedApplicationManifest -Root $SourceRoot)
    $candidate = @(Get-QualitySanitizedApplicationManifest -Root $CandidateRoot)
    if ($source.Count -ne $candidate.Count) {
        throw "Sanitized application manifest count differs: source=$($source.Count), candidate=$($candidate.Count)."
    }
    for ($index = 0; $index -lt $source.Count; $index++) {
        if ([string]$source[$index].RelativePath -cne [string]$candidate[$index].RelativePath -or
            [long]$source[$index].Length -ne [long]$candidate[$index].Length -or
            [string]$source[$index].Sha256 -cne [string]$candidate[$index].Sha256) {
            throw "Sanitized application manifest differs at '$($source[$index].RelativePath)' / '$($candidate[$index].RelativePath)'."
        }
    }
}

Export-ModuleMember -Function @(
    'Read-QualityProductionConfiguration',
    'New-QualityProductionDatabaseConfigurationRepair',
    'New-QualityServerLocalSqliteConfiguration',
    'Test-QualityProductionConfigurationUsesServerLocalSqlite',
    'Assert-QualityServerLocalSqliteStorage',
    'Get-QualitySanitizedApplicationManifest',
    'Assert-QualitySanitizedApplicationManifestEqual'
)
