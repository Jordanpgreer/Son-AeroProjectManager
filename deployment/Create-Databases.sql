:setvar AppServiceAccount "SON4L\SON-IIS2$"
:On Error exit
:setvar IncludeSmallBusinessSubcontracting "0"

-- Fresh-server inventory only. For the existing company SQL server use the scoped
-- Initialize-SmallBusinessSubcontractingDatabase.sql script instead of rerunning setup.
-- Set IncludeSmallBusinessSubcontracting to 1 in this reviewed SQLCMD script to opt in.
IF N'$(IncludeSmallBusinessSubcontracting)' NOT IN (N'0', N'1')
    THROW 51000, 'IncludeSmallBusinessSubcontracting must be 0 or 1.', 1;

IF DB_ID(N'ProjectTracker') IS NULL CREATE DATABASE [ProjectTracker];
GO
IF DB_ID(N'EngineeringHub') IS NULL CREATE DATABASE [EngineeringHub];
GO
IF DB_ID(N'QualityAssurance') IS NULL CREATE DATABASE [QualityAssurance];
GO
IF N'$(IncludeSmallBusinessSubcontracting)' = N'1' AND DB_ID(N'SmallBusinessSubcontracting') IS NULL
    EXEC(N'CREATE DATABASE [SmallBusinessSubcontracting]');
GO
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE [name] = N'$(AppServiceAccount)')
    CREATE LOGIN [$(AppServiceAccount)] FROM WINDOWS;
GO

USE [ProjectTracker];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'$(AppServiceAccount)')
    CREATE USER [$(AppServiceAccount)] FOR LOGIN [$(AppServiceAccount)];
ALTER ROLE [db_datareader] ADD MEMBER [$(AppServiceAccount)];
ALTER ROLE [db_datawriter] ADD MEMBER [$(AppServiceAccount)];
ALTER ROLE [db_ddladmin] ADD MEMBER [$(AppServiceAccount)];
GO

USE [EngineeringHub];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'$(AppServiceAccount)')
    CREATE USER [$(AppServiceAccount)] FOR LOGIN [$(AppServiceAccount)];
ALTER ROLE [db_datareader] ADD MEMBER [$(AppServiceAccount)];
ALTER ROLE [db_datawriter] ADD MEMBER [$(AppServiceAccount)];
ALTER ROLE [db_ddladmin] ADD MEMBER [$(AppServiceAccount)];
GO

USE [QualityAssurance];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'$(AppServiceAccount)')
    CREATE USER [$(AppServiceAccount)] FOR LOGIN [$(AppServiceAccount)];
ALTER ROLE [db_datareader] ADD MEMBER [$(AppServiceAccount)];
ALTER ROLE [db_datawriter] ADD MEMBER [$(AppServiceAccount)];
ALTER ROLE [db_ddladmin] ADD MEMBER [$(AppServiceAccount)];
GO

IF N'$(IncludeSmallBusinessSubcontracting)' = N'1'
    EXEC(N'USE [SmallBusinessSubcontracting];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N''$(AppServiceAccount)'')
    CREATE USER [$(AppServiceAccount)] FOR LOGIN [$(AppServiceAccount)];
IF ISNULL(IS_ROLEMEMBER(N''db_datareader'', N''$(AppServiceAccount)''), 0) <> 1
    ALTER ROLE [db_datareader] ADD MEMBER [$(AppServiceAccount)];
IF ISNULL(IS_ROLEMEMBER(N''db_datawriter'', N''$(AppServiceAccount)''), 0) <> 1
    ALTER ROLE [db_datawriter] ADD MEMBER [$(AppServiceAccount)];
IF ISNULL(IS_ROLEMEMBER(N''db_ddladmin'', N''$(AppServiceAccount)''), 0) <> 1
    ALTER ROLE [db_ddladmin] ADD MEMBER [$(AppServiceAccount)];');
GO
