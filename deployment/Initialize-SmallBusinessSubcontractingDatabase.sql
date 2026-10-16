-- Run on SON-SQL2 as a SQL administrator in SSMS with SQLCMD Mode enabled.
-- Default matches IIS ApplicationPoolIdentity on SON-IIS2 (network machine account).
-- For a dedicated domain service account, change this ONE value before execution.
-- This provisions permissions and the shared read cache; the app applies module EF migrations.
-- It does not change Portal visibility, module membership, or other module databases.
:setvar AppServiceAccount "SON4L\SON-IIS2$"
:on error exit

USE [master];
GO
SET NOCOUNT ON;
IF ISNULL(CONVERT(nvarchar(128), SERVERPROPERTY(N'MachineName')), N'') <> N'SON-SQL2'
    THROW 51003, 'Run this provisioning script only on SON-SQL2. No database or login changes were made.', 1;
IF DB_ID(N'ProjectTracker') IS NULL
    THROW 51000, 'ProjectTracker must already exist and contain the shared authorization schema.', 1;
IF OBJECT_ID(N'ProjectTracker.dbo.Users', N'U') IS NULL
   OR OBJECT_ID(N'ProjectTracker.dbo.UserModuleAccess', N'U') IS NULL
   OR OBJECT_ID(N'ProjectTracker.dbo.Groups', N'U') IS NULL
   OR OBJECT_ID(N'ProjectTracker.dbo.UserGroupMemberships', N'U') IS NULL
   OR OBJECT_ID(N'ProjectTracker.dbo.GroupPermissions', N'U') IS NULL
   OR OBJECT_ID(N'ProjectTracker.dbo.IntegrationCredentials', N'U') IS NULL
   OR OBJECT_ID(N'ProjectTracker.dbo.EnterpriseIntegrationSettings', N'U') IS NULL
    THROW 51001, 'The shared RoleStore schema is incomplete. Upgrade ProjectTracker before installation.', 1;
IF DB_ID(N'SmallBusinessSubcontracting') IS NULL
    CREATE DATABASE [SmallBusinessSubcontracting];
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE [name] = N'$(AppServiceAccount)')
    CREATE LOGIN [$(AppServiceAccount)] FROM WINDOWS;
GO

USE [SmallBusinessSubcontracting];
GO
-- Refuse an untracked or partially initialized production schema. Do not stamp
-- migration history or remove any tables here; preserve data for DBA reconciliation.
IF EXISTS (SELECT 1 FROM sys.tables WHERE is_ms_shipped = 0 AND name <> N'__EFMigrationsHistory')
    AND OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 51002, 'Existing untracked Subcontracting tables require DBA reconciliation before deployment.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'$(AppServiceAccount)')
    CREATE USER [$(AppServiceAccount)] FOR LOGIN [$(AppServiceAccount)];
-- db_ddladmin is confined to this new module database for startup EF migrations.
-- No db_owner or server-level creation rights are granted.
IF IS_ROLEMEMBER(N'db_datareader', N'$(AppServiceAccount)') <> 1
    ALTER ROLE [db_datareader] ADD MEMBER [$(AppServiceAccount)];
IF IS_ROLEMEMBER(N'db_datawriter', N'$(AppServiceAccount)') <> 1
    ALTER ROLE [db_datawriter] ADD MEMBER [$(AppServiceAccount)];
IF IS_ROLEMEMBER(N'db_ddladmin', N'$(AppServiceAccount)') <> 1
    ALTER ROLE [db_ddladmin] ADD MEMBER [$(AppServiceAccount)];
GO

USE [ProjectTracker];
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'$(AppServiceAccount)')
    CREATE USER [$(AppServiceAccount)] FOR LOGIN [$(AppServiceAccount)];
-- Shared authorization/settings remain read-only; only the read-cache table gets CRUD.
-- Existing rights used by other modules sharing this machine account are not revoked.
GRANT SELECT ON OBJECT::dbo.Users TO [$(AppServiceAccount)];
GRANT SELECT ON OBJECT::dbo.UserModuleAccess TO [$(AppServiceAccount)];
GRANT SELECT ON OBJECT::dbo.Groups TO [$(AppServiceAccount)];
GRANT SELECT ON OBJECT::dbo.UserGroupMemberships TO [$(AppServiceAccount)];
GRANT SELECT ON OBJECT::dbo.GroupPermissions TO [$(AppServiceAccount)];
GRANT SELECT ON OBJECT::dbo.IntegrationCredentials TO [$(AppServiceAccount)];
GRANT SELECT ON OBJECT::dbo.EnterpriseIntegrationSettings TO [$(AppServiceAccount)];
GO

-- Match FulcrumSnapshotStore.SqlServerSchema without granting shared-database DDL rights.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @lockResult int;
    EXEC @lockResult = sys.sp_getapplock @Resource = N'Arda.FulcrumReadSnapshots.Schema',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
    IF @lockResult < 0 THROW 51006, 'Could not obtain the shared snapshot schema lock.', 1;
    IF OBJECT_ID(N'dbo.FulcrumReadSnapshots', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FulcrumReadSnapshots (
            CacheKey varchar(64) NOT NULL CONSTRAINT PK_FulcrumReadSnapshots PRIMARY KEY,
            Body varbinary(max) NULL, ContentType nvarchar(160) NULL,
            FetchedTicks bigint NOT NULL, ExpiresTicks bigint NOT NULL, RetryTicks bigint NOT NULL,
            LeaseOwner varchar(32) NULL, LeaseTicks bigint NOT NULL
        );
        CREATE INDEX IX_FulcrumReadSnapshots_Expiry ON dbo.FulcrumReadSnapshots (ExpiresTicks);
    END;
    GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.FulcrumReadSnapshots TO [$(AppServiceAccount)];
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

USE [SmallBusinessSubcontracting];
GO
DECLARE @CanRead int = IS_ROLEMEMBER(N'db_datareader', N'$(AppServiceAccount)');
DECLARE @CanWrite int = IS_ROLEMEMBER(N'db_datawriter', N'$(AppServiceAccount)');
DECLARE @CanApplyMigrations int = IS_ROLEMEMBER(N'db_ddladmin', N'$(AppServiceAccount)');
IF DB_NAME() <> N'SmallBusinessSubcontracting'
    OR ISNULL(@CanRead, 0) <> 1 OR ISNULL(@CanWrite, 0) <> 1 OR ISNULL(@CanApplyMigrations, 0) <> 1
    THROW 51004, 'Subcontracting database identity or role verification failed.', 1;
IF (SELECT COUNT(DISTINCT object.name)
    FROM ProjectTracker.sys.database_permissions AS permission
    JOIN ProjectTracker.sys.database_principals AS principal ON principal.principal_id = permission.grantee_principal_id
    JOIN ProjectTracker.sys.objects AS object ON object.object_id = permission.major_id
    JOIN ProjectTracker.sys.schemas AS schemaName ON schemaName.schema_id = object.schema_id
    WHERE principal.name = N'$(AppServiceAccount)' AND permission.class = 1
      AND permission.permission_name = N'SELECT' AND permission.state IN (N'G', N'W')
      AND schemaName.name = N'dbo'
      AND object.name IN (N'Users', N'UserModuleAccess', N'Groups', N'UserGroupMemberships', N'GroupPermissions', N'IntegrationCredentials', N'EnterpriseIntegrationSettings')) <> 7
    THROW 51005, 'Shared RoleStore SELECT grants could not be verified.', 1;
IF (SELECT COUNT(DISTINCT permission.permission_name)
    FROM ProjectTracker.sys.database_permissions permission
    JOIN ProjectTracker.sys.database_principals principal ON principal.principal_id = permission.grantee_principal_id
    JOIN ProjectTracker.sys.objects object ON object.object_id = permission.major_id
    JOIN ProjectTracker.sys.schemas schemaName ON schemaName.schema_id = object.schema_id
    WHERE principal.name = N'$(AppServiceAccount)' AND permission.class = 1
      AND permission.permission_name IN (N'SELECT', N'INSERT', N'UPDATE', N'DELETE') AND permission.state IN (N'G', N'W')
      AND schemaName.name = N'dbo' AND object.name = N'FulcrumReadSnapshots') <> 4
    THROW 51007, 'Shared Fulcrum snapshot CRUD grants could not be verified.', 1;
SELECT DB_NAME() AS ModuleDatabase, N'$(AppServiceAccount)' AS AppServiceAccount,
    @CanRead AS CanRead, @CanWrite AS CanWrite, @CanApplyMigrations AS CanApplyMigrations;
PRINT 'SMALL_BUSINESS_SUBCONTRACTING_DATABASE_READY';
GO
