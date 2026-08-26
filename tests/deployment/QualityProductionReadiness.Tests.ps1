$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

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
if ($qualityTemplate.ConnectionStrings.QualityStore -notmatch 'Database=QualityAssurance(?:;|$)') {
    throw 'Quality production configuration does not target the QualityAssurance database.'
}

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
