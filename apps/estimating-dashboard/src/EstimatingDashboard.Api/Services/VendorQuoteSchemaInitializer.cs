using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public sealed class VendorQuoteSchemaInitializer(EstimatingAccessDbContext db)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsSqlite() && !db.Database.IsSqlServer()) return;
        await db.Database.ExecuteSqlRawAsync(CreateSql(db.Database.IsSqlite()), cancellationToken);
    }

    // Identifiers and definitions below are code constants; no request data enters this DDL.
    public static string CreateSql(bool sqlite)
    {
        string Text(int length) => sqlite ? "TEXT" : $"nvarchar({length})";
        var integer = sqlite ? "INTEGER" : "int";
        var big = sqlite ? "INTEGER" : "bigint";
        var timestamp = sqlite ? "TEXT" : "datetimeoffset";
        var date = sqlite ? "TEXT" : "datetime2";
        var unlimited = sqlite ? "TEXT" : "nvarchar(max)";
        string Id(bool longId = false) => sqlite ? "[Id] INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT" : $"[Id] {(longId ? big : integer)} IDENTITY(1,1) NOT NULL PRIMARY KEY";
        string Fk(string column, string table, string target = "Id") => $"FOREIGN KEY ([{column}]) REFERENCES [{table}] ([{target}]) ON DELETE NO ACTION";
        string Table(string name, string fields) => sqlite
            ? $"CREATE TABLE IF NOT EXISTS [{name}] ({fields});\n"
            : $"IF OBJECT_ID(N'[{name}]', N'U') IS NULL BEGIN CREATE TABLE [{name}] ({fields}); END;\n";
        string Index(string name, string table, string columns, bool unique = false) => sqlite
            ? $"CREATE {(unique ? "UNIQUE " : "")}INDEX IF NOT EXISTS [{name}] ON [{table}] ({columns});\n"
            : $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'{name}' AND object_id=OBJECT_ID(N'[{table}]')) CREATE {(unique ? "UNIQUE " : "")}INDEX [{name}] ON [{table}] ({columns});\n";
        string ActivityFields() => $"[Kind] {Text(40)} NOT NULL, [Text] {Text(4000)} NOT NULL, [OldValue] {Text(1000)} NULL, [NewValue] {Text(1000)} NULL, [OccurredAt] {timestamp} NOT NULL, [AccountName] {Text(160)} NOT NULL, [DisplayName] {Text(160)} NOT NULL";
        return Table("EstimatingVendorRequests", $"""
            {Id()}, [QuoteHistoryId] {integer} NOT NULL, [VendorName] {Text(200)} NOT NULL,
            [VendorEmail] {Text(254)} NOT NULL, [ThreadKey] {Text(64)} NOT NULL, [PartNumber] {Text(160)} NULL,
            [Title] {Text(240)} NOT NULL, [Status] {Text(40)} NOT NULL,
            [StatusChangedAt] {timestamp} NOT NULL, [StatusChangedBy] {Text(160)} NOT NULL,
            [FollowUpDate] {date} NULL, [CreatedAt] {timestamp} NOT NULL, [UpdatedAt] {timestamp} NOT NULL,
            [LastMessageAt] {timestamp} NULL, [Version] {integer} NOT NULL,
            {Fk("QuoteHistoryId", "EstimatingQuoteHistory")}
            """) + Index("IX_EstimatingVendorRequests_QuoteHistoryId_ThreadKey", "EstimatingVendorRequests", "[QuoteHistoryId], [ThreadKey]", true)
        + Table("EstimatingVendorMessages", $"""
            {Id(true)}, [RequestId] {integer} NULL, [QuoteHistoryId] {integer} NOT NULL,
            [VendorEmail] {Text(254)} NOT NULL, [ConversationId] {Text(512)} NULL,
            [DeduplicationKey] {Text(64)} NOT NULL, [SourceMessageId] {Text(1024)} NOT NULL,
            [Mailbox] {Text(254)} NOT NULL, [Direction] {Text(16)} NOT NULL, [Subject] {Text(998)} NOT NULL,
            [FromAddress] {Text(254)} NOT NULL, [FromName] {Text(200)} NULL, [ToAddressesJson] {unlimited} NOT NULL,
            [SentAt] {timestamp} NOT NULL, [ReceivedAt] {timestamp} NULL, [ImportedAt] {timestamp} NOT NULL,
            [ImportedBy] {Text(160)} NOT NULL, [BodyText] {unlimited} NOT NULL,
            {Fk("RequestId", "EstimatingVendorRequests")}, {Fk("QuoteHistoryId", "EstimatingQuoteHistory")}
            """) + Index("IX_EstimatingVendorMessages_DeduplicationKey", "EstimatingVendorMessages", "[DeduplicationKey]", true)
        + Index("IX_EstimatingVendorMessages_RequestId", "EstimatingVendorMessages", "[RequestId]")
        + Index("IX_EstimatingVendorMessages_QuoteHistoryId", "EstimatingVendorMessages", "[QuoteHistoryId]")
        + Table("EstimatingVendorAttachments", $"""
            {Id(true)}, [MessageId] {big} NOT NULL, [FileName] {Text(180)} NOT NULL,
            [ContentType] {Text(100)} NOT NULL, [SizeBytes] {integer} NOT NULL,
            [Content] {(sqlite ? "BLOB" : "varbinary(max)")} NOT NULL, {Fk("MessageId", "EstimatingVendorMessages")}
            """) + Index("IX_EstimatingVendorAttachments_MessageId", "EstimatingVendorAttachments", "[MessageId]")
        + Table("EstimatingVendorActivities", $"{Id(true)}, [RequestId] {integer} NOT NULL, {ActivityFields()}, {Fk("RequestId", "EstimatingVendorRequests")}")
        + Index("IX_EstimatingVendorActivities_RequestId", "EstimatingVendorActivities", "[RequestId]")
        + Table("EstimatingVendorSyncStates", $"""
            {Id()}, [AccountName] {Text(160)} NOT NULL, [Mailbox] {Text(254)} NOT NULL, [ClientName] {Text(64)} NOT NULL,
            [LastCheckedAt] {timestamp} NOT NULL, [LastSuccessAt] {timestamp} NULL,
            [ImportedCount] {integer} NOT NULL, [DuplicateCount] {integer} NOT NULL,
            [DeferredCount] {integer} NOT NULL, [Error] {Text(500)} NULL
            """) + Index("IX_EstimatingVendorSyncStates_AccountName_Mailbox_ClientName", "EstimatingVendorSyncStates", "[AccountName], [Mailbox], [ClientName]", true)
        + Table("EstimatingQuoteStatusMetadata", $"[QuoteHistoryId] {integer} NOT NULL PRIMARY KEY, [FollowUpDate] {date} NULL, {Fk("QuoteHistoryId", "EstimatingQuoteHistory")}")
        + Table("EstimatingQuoteStatusActivities", $"{Id(true)}, [QuoteHistoryId] {integer} NOT NULL, {ActivityFields()}, {Fk("QuoteHistoryId", "EstimatingQuoteHistory")}")
        + Index("IX_EstimatingQuoteStatusActivities_QuoteHistoryId", "EstimatingQuoteStatusActivities", "[QuoteHistoryId]");
    }
}
