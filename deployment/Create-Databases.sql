:setvar AppServiceAccount "SON4L\SON-IIS2$"

IF DB_ID(N'ProjectTracker') IS NULL CREATE DATABASE [ProjectTracker];
GO
IF DB_ID(N'EngineeringHub') IS NULL CREATE DATABASE [EngineeringHub];
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
