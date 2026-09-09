using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;

namespace Portal.Api.Services.ApiCustomizer;

public sealed class CustomizerSchemaInitializer(PortalRoleDbContext db)
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        db.Database.ExecuteSqlRawAsync(db.Database.IsSqlite() ? Sqlite : SqlServer, cancellationToken);

    public const string Sqlite = """
        CREATE TABLE IF NOT EXISTS ApiCustomizerReports (
            Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, DefinitionJson TEXT NOT NULL,
            Version INTEGER NOT NULL, UpdatedBy TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS ApiCustomizerAudits (
            Id TEXT NOT NULL PRIMARY KEY, Actor TEXT NOT NULL, Action TEXT NOT NULL,
            ReportName TEXT NOT NULL, Detail TEXT NOT NULL, OccurredAt TEXT NOT NULL);
        """;
    public const string SqlServer = """
        IF OBJECT_ID(N'[dbo].[ApiCustomizerReports]', N'U') IS NULL
        CREATE TABLE [dbo].[ApiCustomizerReports] (
            Id uniqueidentifier NOT NULL PRIMARY KEY, Name nvarchar(120) NOT NULL,
            DefinitionJson nvarchar(max) NOT NULL, Version int NOT NULL,
            UpdatedBy nvarchar(160) NOT NULL, UpdatedAt datetimeoffset NOT NULL);
        IF OBJECT_ID(N'[dbo].[ApiCustomizerAudits]', N'U') IS NULL
        CREATE TABLE [dbo].[ApiCustomizerAudits] (
            Id uniqueidentifier NOT NULL PRIMARY KEY, Actor nvarchar(160) NOT NULL,
            Action nvarchar(32) NOT NULL, ReportName nvarchar(120) NOT NULL,
            Detail nvarchar(max) NOT NULL, OccurredAt datetimeoffset NOT NULL);
        """;
}
