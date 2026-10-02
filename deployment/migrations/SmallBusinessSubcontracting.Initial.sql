-- Initial Small Business Subcontracting EF Core schema, generated from the checked-in migration.
-- Run AFTER Initialize-SmallBusinessSubcontractingDatabase.sql as the authorized SQL administrator.
-- SSMS SQLCMD Mode is required. Select SmallBusinessSubcontracting on SON-SQL2 before executing.
-- This script never changes the selected database and stops if either target is wrong.
-- Regenerate with dotnet ef migrations script --idempotent --context SubcontractingDbContext.
:on error exit
IF ISNULL(CONVERT(nvarchar(128), SERVERPROPERTY(N'MachineName')), N'') <> N'SON-SQL2'
    THROW 51010, 'Select SON-SQL2 before applying the Subcontracting schema.', 1;
IF DB_NAME() <> N'SmallBusinessSubcontracting'
    THROW 51011, 'Select the SmallBusinessSubcontracting database before applying its schema.', 1;
GO
-- BEGIN GENERATED EF CORE SCRIPT
IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001192605_InitialSubcontracting'
)
BEGIN
    CREATE TABLE [BusinessSizeTags] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(80) NOT NULL,
        [NormalizedName] nvarchar(80) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [CreatedBy] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_BusinessSizeTags] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001192605_InitialSubcontracting'
)
BEGIN
    CREATE TABLE [Vendors] (
        [Id] int NOT NULL IDENTITY,
        [FulcrumId] nvarchar(80) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [VendorCode] nvarchar(120) NULL,
        [Active] bit NOT NULL,
        [Website] nvarchar(500) NULL,
        [ContactsJson] nvarchar(max) NOT NULL,
        [LastSyncedAt] datetimeoffset NOT NULL,
        [LastCertificationDate] date NULL,
        [Version] int NOT NULL,
        CONSTRAINT [PK_Vendors] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001192605_InitialSubcontracting'
)
BEGIN
    CREATE TABLE [VendorAuditEvents] (
        [Id] bigint NOT NULL IDENTITY,
        [VendorId] int NOT NULL,
        [Kind] nvarchar(80) NOT NULL,
        [Summary] nvarchar(500) NOT NULL,
        [Actor] nvarchar(160) NOT NULL,
        [OccurredAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_VendorAuditEvents] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_VendorAuditEvents_Vendors_VendorId] FOREIGN KEY ([VendorId]) REFERENCES [Vendors] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001192605_InitialSubcontracting'
)
BEGIN
    CREATE TABLE [VendorBusinessSizeTags] (
        [VendorId] int NOT NULL,
        [BusinessSizeTagId] int NOT NULL,
        CONSTRAINT [PK_VendorBusinessSizeTags] PRIMARY KEY ([VendorId], [BusinessSizeTagId]),
        CONSTRAINT [FK_VendorBusinessSizeTags_BusinessSizeTags_BusinessSizeTagId] FOREIGN KEY ([BusinessSizeTagId]) REFERENCES [BusinessSizeTags] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_VendorBusinessSizeTags_Vendors_VendorId] FOREIGN KEY ([VendorId]) REFERENCES [Vendors] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001192605_InitialSubcontracting'
)
BEGIN
    CREATE TABLE [VendorDocuments] (
        [Id] uniqueidentifier NOT NULL,
        [VendorId] int NOT NULL,
        [OriginalFileName] nvarchar(255) NOT NULL,
        [RelativePath] nvarchar(1000) NOT NULL,
        [ContentType] nvarchar(160) NOT NULL,
        [FileSize] bigint NOT NULL,
        [FileHash] nvarchar(64) NOT NULL,
        [DocumentType] nvarchar(100) NOT NULL,
        [DocumentDate] date NOT NULL,
        [Notes] nvarchar(1000) NULL,
        [UploadedBy] nvarchar(160) NOT NULL,
        [UploadedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_VendorDocuments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_VendorDocuments_Vendors_VendorId] FOREIGN KEY ([VendorId]) REFERENCES [Vendors] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001192605_InitialSubcontracting'
)
BEGIN
    CREATE UNIQUE INDEX [IX_BusinessSizeTags_NormalizedName] ON [BusinessSizeTags] ([NormalizedName]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001192605_InitialSubcontracting'
)
BEGIN
    CREATE INDEX [IX_VendorAuditEvents_VendorId] ON [VendorAuditEvents] ([VendorId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001192605_InitialSubcontracting'
)
BEGIN
    CREATE INDEX [IX_VendorBusinessSizeTags_BusinessSizeTagId] ON [VendorBusinessSizeTags] ([BusinessSizeTagId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001192605_InitialSubcontracting'
)
BEGIN
    CREATE INDEX [IX_VendorDocuments_VendorId] ON [VendorDocuments] ([VendorId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001192605_InitialSubcontracting'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Vendors_FulcrumId] ON [Vendors] ([FulcrumId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001192605_InitialSubcontracting'
)
BEGIN
    CREATE INDEX [IX_Vendors_Name] ON [Vendors] ([Name]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261001192605_InitialSubcontracting'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261001192605_InitialSubcontracting', N'8.0.31');
END;
GO

COMMIT;
GO
