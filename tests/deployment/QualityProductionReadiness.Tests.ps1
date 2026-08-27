$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-ApprovedSqlServerConnection {
    param(
        [Parameter(Mandatory = $true)][string]$ConnectionString,
        [Parameter(Mandatory = $true)][string]$Database,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Add-Type -AssemblyName System.Data
    try {
        $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder `
            -ArgumentList $ConnectionString
    }
    catch { throw "$Label is not a valid SQL Server connection string: $($_.Exception.Message)" }

    Assert-True ($builder.DataSource -ieq 'tcp:SON-SQL2,1433') `
        "$Label must target tcp:SON-SQL2,1433."
    Assert-True ($builder.InitialCatalog -ieq $Database) `
        "$Label must target database '$Database'."
    Assert-True $builder.IntegratedSecurity "$Label must use Integrated Security."
    Assert-True $builder.Encrypt "$Label must require encrypted transport."
    Assert-True $builder.TrustServerCertificate "$Label must use the approved internal SQL certificate trust setting."
    Assert-True ([string]::IsNullOrWhiteSpace($builder.UserID) -and
        [string]::IsNullOrWhiteSpace($builder.Password) -and
        [string]::IsNullOrWhiteSpace($builder.AttachDBFilename)) `
        "$Label must not contain SQL credentials or an attached database file."
}

$createDatabases = Get-Content -LiteralPath `
    (Join-Path $repoRoot 'deployment\Create-Databases.sql') -Raw
foreach ($required in @(
    "DB_ID(N'QualityAssurance')",
    'CREATE DATABASE [QualityAssurance]',
    'USE [QualityAssurance]'
)) {
    if (-not $createDatabases.Contains($required)) {
        throw "Create-Databases.sql does not provision Quality Assurance completely: $required"
    }
}
$qualityGrantBlock = [regex]::Match(
    $createDatabases,
    '(?ms)^USE \[QualityAssurance\];.*?^GO\s*$')
if (-not $qualityGrantBlock.Success) {
    throw 'Create-Databases.sql has no isolated QualityAssurance grant block.'
}
foreach ($requiredGrant in @(
    'ALTER ROLE [db_datareader]',
    'ALTER ROLE [db_datawriter]',
    'ALTER ROLE [db_ddladmin]'
)) {
    if (-not $qualityGrantBlock.Value.Contains($requiredGrant)) {
        throw "The QualityAssurance database grant block is missing: $requiredGrant"
    }
}

$configureSqlServer = Get-Content -LiteralPath `
    (Join-Path $repoRoot 'deployment\Configure-SqlServer.ps1') -Raw
foreach ($required in @(
    "DB_ID(N'QualityAssurance')",
    "@('ProjectTracker', 'EngineeringHub', 'QualityAssurance')",
    'ProjectTracker, EngineeringHub, and QualityAssurance databases are ready.'
)) {
    if (-not $configureSqlServer.Contains($required)) {
        throw "Configure-SqlServer.ps1 does not provision or verify Quality Assurance: $required"
    }
}

$backupReadiness = Get-Content -LiteralPath `
    (Join-Path $repoRoot 'deployment\Test-HubBackupReadiness.ps1') -Raw
foreach ($required in @(
    "N'QualityAssurance'",
    "@('ProjectTracker', 'EngineeringHub', 'QualityAssurance')",
    'ProjectTracker, EngineeringHub, and QualityAssurance'
)) {
    if (-not $backupReadiness.Contains($required)) {
        throw "Backup readiness does not include Quality Assurance: $required"
    }
}

$qualityTemplate = Get-Content -LiteralPath `
    (Join-Path $repoRoot 'deployment\templates\quality-assurance.appsettings.Production.json') -Raw |
    ConvertFrom-Json
if ($qualityTemplate.Database.Provider -cne 'SqlServer' -or
    $qualityTemplate.QualityDatabase.Provider -cne 'SqlServer') {
    throw 'Quality production configuration must select SQL Server for both shared access and Quality data.'
}
Assert-True ($null -eq $qualityTemplate.QualityDatabase.PSObject.Properties['StorageMode']) `
    'The normal Quality Production template must remain the dedicated SQL Server target, not the temporary SQLite bridge.'
if ($qualityTemplate.ConnectionStrings.QualityStore -notmatch 'Database=QualityAssurance(?:;|$)') {
    throw 'Quality production configuration does not target the QualityAssurance database.'
}
Assert-ApprovedSqlServerConnection `
    -ConnectionString ([string]$qualityTemplate.ConnectionStrings.ModuleAccessStore) `
    -Database 'ProjectTracker' `
    -Label 'Quality production ModuleAccessStore'
Assert-ApprovedSqlServerConnection `
    -ConnectionString ([string]$qualityTemplate.ConnectionStrings.QualityStore) `
    -Database 'QualityAssurance' `
    -Label 'Quality production QualityStore'

$qualityBasePath = Join-Path $repoRoot `
    'apps\quality-assurance\src\QualityAssurance.Api\appsettings.json'
$qualityBase = Get-Content -LiteralPath $qualityBasePath -Raw | ConvertFrom-Json
Assert-True ($qualityBase.QualityDatabase.Provider -ceq 'SqlServer') `
    'Quality base configuration must explicitly select the Quality database provider.'
Assert-True (-not [string]::IsNullOrWhiteSpace(
    [string]$qualityBase.ConnectionStrings.QualityStore)) `
    'Quality base configuration must explicitly define QualityStore.'
Assert-True ([string]$qualityBase.ConnectionStrings.QualityStore -notmatch '(?i)\.db(?:;|$)') `
    'Quality base configuration must never fall back to a SQLite filename while SQL Server is selected.'

$qualityConfigurationModule = Join-Path $repoRoot `
    'deployment\QualityAssuranceProductionConfiguration.psm1'
Assert-True (Test-Path -LiteralPath $qualityConfigurationModule -PathType Leaf) `
    'The shared fail-closed Quality production configuration module is missing.'
$qualityModuleSource = Get-Content -LiteralPath $qualityConfigurationModule -Raw
foreach ($requiredExport in @(
    'Read-QualityProductionConfiguration',
    'New-QualityProductionDatabaseConfigurationRepair',
    'New-QualityServerLocalSqliteConfiguration',
    'Test-QualityProductionConfigurationUsesServerLocalSqlite',
    'Get-QualityServerLocalSqliteModifyRights',
    'Assert-QualityServerLocalSqliteStorage',
    'Get-QualitySanitizedApplicationManifest',
    'Assert-QualitySanitizedApplicationManifestEqual'
)) {
    Assert-True $qualityModuleSource.Contains($requiredExport) `
        "Quality production configuration module is missing contract '$requiredExport'."
}

$qualityReleaseSource = Get-Content -LiteralPath `
    (Join-Path $repoRoot 'deployment\Deploy-QualityAssuranceRelease.ps1') -Raw
$hubReleaseSource = Get-Content -LiteralPath `
    (Join-Path $repoRoot 'deployment\Deploy-HubRelease.ps1') -Raw
Assert-True ($qualityReleaseSource -match
    'Import-Module\s+\$configurationModule') `
    'The targeted Quality release does not import the shared production configuration policy.'
foreach ($requiredBridgeContract in @(
    '[switch]$UseServerLocalSqlite',
    '[switch]$ResumeServerLocalSqlitePreparation',
    'WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_SERVER_LOCAL_SQLITE',
    'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SERVER_LOCAL_SQLITE',
    'WHATIF_READY_QUALITY_ASSURANCE_RELEASE_WITH_SERVER_LOCAL_SQLITE_RESUME',
    'QUALITY_ASSURANCE_RELEASE_DEPLOYED_AND_HEALTHY_WITH_SERVER_LOCAL_SQLITE_RESUME',
    'Modify, Synchronize',
    'C:\ProgramData\SonAero\deployment-state\quality-assurance-data'
)) {
    Assert-True ($qualityReleaseSource.Contains($requiredBridgeContract) -or
        $qualityModuleSource.Contains($requiredBridgeContract)) `
        "The explicit server-local Quality bridge is missing contract '$requiredBridgeContract'."
}
Assert-True ($hubReleaseSource -match
    'Import-Module\s+\$qualityProductionConfigurationModule') `
    'The full Hub release does not import the shared production configuration policy.'

$portalTemplate = Get-Content -LiteralPath `
    (Join-Path $repoRoot 'deployment\templates\portal.appsettings.Production.json') -Raw |
    ConvertFrom-Json
$qualityApplication = @($portalTemplate.Portal.Applications | Where-Object Id -eq 'quality-assurance')
if ($qualityApplication.Count -ne 1 -or @($qualityApplication[0].AllowedRoles).Count -ne 0) {
    throw 'Quality Assurance must be active in the production Portal template with permission-based visibility.'
}
$engineeringApplication = @($portalTemplate.Portal.Applications | Where-Object Id -eq 'engineering-hub')
if ($engineeringApplication.Count -ne 1 -or @($engineeringApplication[0].AllowedRoles).Count -ne 0) {
    throw 'Engineering Hub must be active in the production Portal template with permission-based visibility.'
}

Write-Output 'QUALITY_PRODUCTION_READINESS_TESTS_PASSED'
