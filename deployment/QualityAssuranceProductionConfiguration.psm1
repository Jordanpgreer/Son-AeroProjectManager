$ErrorActionPreference = 'Stop'

$script:ExpectedSqlServer = 'tcp:SON-SQL2,1433'
$script:ExpectedModuleAccessDatabase = 'ProjectTracker'
$script:ExpectedQualityDatabase = 'QualityAssurance'

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
    if ([string]$Configuration.Database.Provider -cne 'SqlServer' -or
        [string]$Configuration.QualityDatabase.Provider -cne 'SqlServer') {
        throw "$Label must use SQL Server for both database providers."
    }
    if ($null -eq $Configuration.ConnectionStrings -or
        $Configuration.ConnectionStrings.GetType() -ne
            [System.Management.Automation.PSCustomObject]) {
        throw "$Label must contain a ConnectionStrings object."
    }
    Assert-ApprovedSqlConnectionString `
        -ConnectionString ([string]$Configuration.ConnectionStrings.ModuleAccessStore) `
        -ExpectedDatabase $script:ExpectedModuleAccessDatabase -Label "$Label ModuleAccessStore"
    Assert-ApprovedSqlConnectionString `
        -ConnectionString ([string]$Configuration.ConnectionStrings.QualityStore) `
        -ExpectedDatabase $script:ExpectedQualityDatabase -Label "$Label QualityStore"
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
    'Get-QualitySanitizedApplicationManifest',
    'Assert-QualitySanitizedApplicationManifestEqual'
)
