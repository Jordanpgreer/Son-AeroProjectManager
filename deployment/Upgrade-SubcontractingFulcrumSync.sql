-- Existing installed Subcontracting only. Run on SON-SQL2 as a DBA in SSMS SQLCMD Mode.
-- Confirm current ProjectTracker backup/restore evidence before execution.
-- Review this SAME identity used by the installed module; change only this variable for
-- a dedicated account. No login/user, database, broad role, credential, or business row is created/changed.
:setvar AppServiceAccount "SON4L\SON-IIS2$"
:on error exit

USE [master];
GO
SET NOCOUNT ON;
IF ISNULL(CONVERT(nvarchar(128), SERVERPROPERTY(N'MachineName')), N'') <> N'SON-SQL2'
    THROW 51010, 'Run this upgrade only on SON-SQL2.', 1;
IF DB_ID(N'ProjectTracker') IS NULL OR DB_ID(N'SmallBusinessSubcontracting') IS NULL
    THROW 51011, 'This upgrade requires the existing ProjectTracker and installed Subcontracting databases.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(AppServiceAccount)')
    THROW 51012, 'The reviewed application login must already exist.', 1;
GO

USE [ProjectTracker];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.EnterpriseIntegrationSettings', N'U') IS NULL
    OR OBJECT_ID(N'dbo.IntegrationCredentials', N'U') IS NULL
    THROW 51013, 'Upgrade the shared ProjectTracker integration schema before this permissions upgrade.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals
    WHERE name = N'$(AppServiceAccount)' AND ISNULL(default_schema_name, N'dbo') = N'dbo')
    THROW 51014, 'The reviewed application user must already exist with the dbo default schema.', 1;

DECLARE @impersonating bit = 0;
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @lockResult int;
    EXEC @lockResult = sys.sp_getapplock @Resource = N'Arda.FulcrumReadSnapshots.Schema',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
    IF @lockResult < 0 THROW 51015, 'Could not obtain the shared snapshot schema lock.', 1;

    -- Keep this table contract aligned with FulcrumSnapshotStore.SqlServerSchema.
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
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FulcrumReadSnapshots')) <> 8
        OR EXISTS (
            SELECT expected.Name FROM (VALUES
                (N'CacheKey', N'varchar', 64, 0), (N'Body', N'varbinary', -1, 1),
                (N'ContentType', N'nvarchar', 320, 1), (N'FetchedTicks', N'bigint', 8, 0),
                (N'ExpiresTicks', N'bigint', 8, 0), (N'RetryTicks', N'bigint', 8, 0),
                (N'LeaseOwner', N'varchar', 32, 1), (N'LeaseTicks', N'bigint', 8, 0)
            ) expected(Name, TypeName, MaxLength, Nullable)
            LEFT JOIN sys.columns actual ON actual.object_id = OBJECT_ID(N'dbo.FulcrumReadSnapshots')
                AND actual.name = expected.Name
            WHERE actual.column_id IS NULL OR TYPE_NAME(actual.user_type_id) <> expected.TypeName
                OR actual.max_length <> expected.MaxLength OR actual.is_nullable <> expected.Nullable
                OR actual.is_computed <> 0 OR actual.is_identity <> 0)
        THROW 51016, 'Existing snapshot schema differs; preserve it for DBA reconciliation.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic
            ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'dbo.FulcrumReadSnapshots') AND i.is_primary_key = 1
                AND c.name = N'CacheKey' AND ic.key_ordinal = 1
                AND (SELECT COUNT(*) FROM sys.index_columns keys
                    WHERE keys.object_id = i.object_id AND keys.index_id = i.index_id AND keys.key_ordinal > 0) = 1)
        THROW 51017, 'Existing snapshot primary key differs; preserve it for DBA reconciliation.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FulcrumReadSnapshots')
        AND name = N'IX_FulcrumReadSnapshots_Expiry')
        CREATE INDEX IX_FulcrumReadSnapshots_Expiry ON dbo.FulcrumReadSnapshots (ExpiresTicks);

    GRANT SELECT ON OBJECT::dbo.EnterpriseIntegrationSettings TO [$(AppServiceAccount)];
    GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.FulcrumReadSnapshots TO [$(AppServiceAccount)];

    EXECUTE AS USER = N'$(AppServiceAccount)';
    SET @impersonating = 1;
    DECLARE @permissionsOk bit = CASE WHEN
        HAS_PERMS_BY_NAME(N'dbo.EnterpriseIntegrationSettings', N'OBJECT', N'SELECT') = 1
        AND HAS_PERMS_BY_NAME(N'dbo.IntegrationCredentials', N'OBJECT', N'SELECT') = 1
        AND HAS_PERMS_BY_NAME(N'dbo.FulcrumReadSnapshots', N'OBJECT', N'SELECT') = 1
        AND HAS_PERMS_BY_NAME(N'dbo.FulcrumReadSnapshots', N'OBJECT', N'INSERT') = 1
        AND HAS_PERMS_BY_NAME(N'dbo.FulcrumReadSnapshots', N'OBJECT', N'UPDATE') = 1
        AND HAS_PERMS_BY_NAME(N'dbo.FulcrumReadSnapshots', N'OBJECT', N'DELETE') = 1
        THEN 1 ELSE 0 END;
    REVERT;
    SET @impersonating = 0;
    IF @permissionsOk <> 1 THROW 51018, 'Effective shared sync permissions are incomplete or denied.', 1;
    COMMIT TRANSACTION;
    PRINT 'SUBCONTRACTING_FULCRUM_SYNC_DATABASE_READY';
END TRY
BEGIN CATCH
    IF @impersonating = 1 REVERT;
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
