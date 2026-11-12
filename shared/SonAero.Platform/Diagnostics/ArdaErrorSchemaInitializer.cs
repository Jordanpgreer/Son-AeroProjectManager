using System.Data;
using Microsoft.EntityFrameworkCore;

namespace SonAero.Platform.Diagnostics;

public sealed class ArdaErrorSchemaInitializer<TContext>(TContext db)
    where TContext : DbContext
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        var sql = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
            ? SqliteSchema
            : provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)
                ? SqlServerSchema
                : null;

        if (sql is null) return;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private const string SqliteSchema = """
        CREATE TABLE IF NOT EXISTS ArdaErrorEvents (
            Id INTEGER NOT NULL CONSTRAINT PK_ArdaErrorEvents PRIMARY KEY AUTOINCREMENT,
            ApplicationId TEXT NOT NULL,
            ApplicationName TEXT NOT NULL,
            ErrorCode TEXT NOT NULL,
            Explanation TEXT NOT NULL,
            AffectedArea TEXT NOT NULL,
            OccurredAtUtc TEXT NOT NULL,
            HttpStatusCode INTEGER NOT NULL,
            RequestMethod TEXT NOT NULL,
            RequestPath TEXT NOT NULL,
            TraceIdentifier TEXT NOT NULL,
            AccountName TEXT NULL,
            ExceptionType TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_ArdaErrorEvents_OccurredAtUtc
            ON ArdaErrorEvents (OccurredAtUtc);
        CREATE INDEX IF NOT EXISTS IX_ArdaErrorEvents_ApplicationId_OccurredAtUtc
            ON ArdaErrorEvents (ApplicationId, OccurredAtUtc);
        CREATE INDEX IF NOT EXISTS IX_ArdaErrorEvents_ErrorCode_OccurredAtUtc
            ON ArdaErrorEvents (ErrorCode, OccurredAtUtc);
        """;

    private const string SqlServerSchema = """
        IF OBJECT_ID(N'[dbo].[ArdaErrorEvents]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[ArdaErrorEvents] (
                [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ArdaErrorEvents] PRIMARY KEY,
                [ApplicationId] nvarchar(64) NOT NULL,
                [ApplicationName] nvarchar(120) NOT NULL,
                [ErrorCode] nvarchar(80) NOT NULL,
                [Explanation] nvarchar(500) NOT NULL,
                [AffectedArea] nvarchar(240) NOT NULL,
                [OccurredAtUtc] datetime2 NOT NULL,
                [HttpStatusCode] int NOT NULL,
                [RequestMethod] nvarchar(12) NOT NULL,
                [RequestPath] nvarchar(500) NOT NULL,
                [TraceIdentifier] nvarchar(160) NOT NULL,
                [AccountName] nvarchar(160) NULL,
                [ExceptionType] nvarchar(240) NULL
            );
        END;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ArdaErrorEvents_OccurredAtUtc' AND object_id = OBJECT_ID(N'[dbo].[ArdaErrorEvents]'))
            CREATE INDEX [IX_ArdaErrorEvents_OccurredAtUtc] ON [dbo].[ArdaErrorEvents] ([OccurredAtUtc]);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ArdaErrorEvents_ApplicationId_OccurredAtUtc' AND object_id = OBJECT_ID(N'[dbo].[ArdaErrorEvents]'))
            CREATE INDEX [IX_ArdaErrorEvents_ApplicationId_OccurredAtUtc] ON [dbo].[ArdaErrorEvents] ([ApplicationId], [OccurredAtUtc]);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ArdaErrorEvents_ErrorCode_OccurredAtUtc' AND object_id = OBJECT_ID(N'[dbo].[ArdaErrorEvents]'))
            CREATE INDEX [IX_ArdaErrorEvents_ErrorCode_OccurredAtUtc] ON [dbo].[ArdaErrorEvents] ([ErrorCode], [OccurredAtUtc]);
        """;
}
