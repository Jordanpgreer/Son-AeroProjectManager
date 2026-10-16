using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace SonAero.Platform.Integrations;

public sealed record FulcrumReadSnapshot(byte[]? Body, string? ContentType, DateTimeOffset FetchedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset RetryAt);

// Durable rows and compare-and-swap leases coordinate independent IIS processes and local modules.
// No credentials, URLs, request bodies, or connection strings are persisted in this table.
public sealed class FulcrumSnapshotStore(IServiceScopeFactory scopes,
    FulcrumSnapshotConnectionFactory connectionFactory)
{
    private readonly SemaphoreSlim schemaGate = new(1, 1);
    private bool initialized;

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public Task<bool> HasUsableCredentialAsync(DateTimeOffset now, CancellationToken ct) => UseAsync(async connection =>
    {
        if (!string.Equals(await EnterpriseIntegrationStore.ReadActiveProviderAsync(connection, ct),
                EnterpriseProviderNames.Fulcrum, StringComparison.OrdinalIgnoreCase)) return false;
        await using var command = Command(connection,
            "SELECT ExpiresAt FROM IntegrationCredentials WHERE CredentialKey = @key AND EncryptedSecret <> ''",
            ("@key", Security.IntegrationCredentialNames.FulcrumPublicApi));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return false;
        return reader.IsDBNull(0) || DateTimeOffset.TryParse(Convert.ToString(reader.GetValue(0)), out var expires)
            && expires > now;
    }, ct);

    public Task<FulcrumReadSnapshot?> ReadAsync(string key, CancellationToken ct) => UseAsync(async connection =>
    {
        await using var command = Command(connection,
            "SELECT Body, ContentType, FetchedTicks, ExpiresTicks, RetryTicks FROM FulcrumReadSnapshots WHERE CacheKey = @key",
            ("@key", key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new FulcrumReadSnapshot(reader.IsDBNull(0) ? null : (byte[])reader.GetValue(0),
                reader.IsDBNull(1) ? null : reader.GetString(1), Time(reader.GetInt64(2)),
                Time(reader.GetInt64(3)), Time(reader.GetInt64(4)))
            : null;
    }, ct);

    public Task<bool> TryAcquireAsync(string key, string owner, DateTimeOffset now, TimeSpan duration,
        CancellationToken ct, bool requireExpired = true) => UseAsync(async connection =>
    {
        await EnsureRowAsync(connection, key, ct);
        await using var command = Command(connection,
            "UPDATE FulcrumReadSnapshots SET LeaseOwner = @owner, LeaseTicks = @until "
            + "WHERE CacheKey = @key AND LeaseTicks <= @now AND RetryTicks <= @now"
            + (requireExpired ? " AND ExpiresTicks <= @now" : ""),
            ("@key", key), ("@owner", owner), ("@now", now.UtcTicks), ("@until", (now + duration).UtcTicks));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }, ct);

    public Task PublishAsync(string key, string owner, byte[] body, string contentType,
        DateTimeOffset fetchedAt, DateTimeOffset expiresAt, CancellationToken ct) => ExecuteAsync(
        "UPDATE FulcrumReadSnapshots SET Body = @body, ContentType = @type, FetchedTicks = @fetched, "
        + "ExpiresTicks = @expires, RetryTicks = 0, LeaseOwner = NULL, LeaseTicks = 0 "
        + "WHERE CacheKey = @key AND LeaseOwner = @owner", ct,
        ("@key", key), ("@owner", owner), ("@body", body), ("@type", contentType),
        ("@fetched", fetchedAt.UtcTicks), ("@expires", expiresAt.UtcTicks));

    public Task ReleaseAsync(string key, string owner, CancellationToken ct) => ExecuteAsync(
        "UPDATE FulcrumReadSnapshots SET LeaseOwner = NULL, LeaseTicks = 0 WHERE CacheKey = @key AND LeaseOwner = @owner",
        ct, ("@key", key), ("@owner", owner));

    public Task DeferAsync(string key, DateTimeOffset retryAt, CancellationToken ct) => UseAsync(async connection =>
    {
        await EnsureRowAsync(connection, key, ct);
        await using var command = Command(connection,
            "UPDATE FulcrumReadSnapshots SET RetryTicks = CASE WHEN RetryTicks > @retry THEN RetryTicks ELSE @retry END "
            + "WHERE CacheKey = @key", ("@key", key), ("@retry", retryAt.UtcTicks));
        return await command.ExecuteNonQueryAsync(ct);
    }, ct);

    // Bounded retention: old successes, expired leases and obsolete credential scopes are removed.
    public Task PruneAsync(DateTimeOffset now, CancellationToken ct) => ExecuteAsync(
        "DELETE FROM FulcrumReadSnapshots WHERE ExpiresTicks < @old AND RetryTicks < @now AND LeaseTicks < @now",
        ct, ("@old", now.AddDays(-8).UtcTicks), ("@now", now.UtcTicks));

    private Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] values) =>
        UseAsync(async connection =>
        {
            await using var command = Command(connection, sql, values);
            return await command.ExecuteNonQueryAsync(ct);
        }, ct);

    private async Task<T> UseAsync<T>(Func<DbConnection, Task<T>> action, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var connection = connectionFactory(scope.ServiceProvider);
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        return await action(connection);
    }

    private async Task EnsureSchemaAsync(DbConnection connection, CancellationToken ct)
    {
        if (Volatile.Read(ref initialized)) return;
        await schemaGate.WaitAsync(ct);
        try
        {
            if (initialized) return;
            var sqlite = IsSqlite(connection);
            await using var command = Command(connection, sqlite ? SqliteSchema : SqlServerSchema);
            await command.ExecuteNonQueryAsync(ct);
            Volatile.Write(ref initialized, true);
        }
        finally { schemaGate.Release(); }
    }

    private static async Task EnsureRowAsync(DbConnection connection, string key, CancellationToken ct)
    {
        await using var command = Command(connection, IsSqlite(connection)
            ? "INSERT OR IGNORE INTO FulcrumReadSnapshots (CacheKey, FetchedTicks, ExpiresTicks, RetryTicks, LeaseTicks) VALUES (@key, 0, 0, 0, 0)"
            : "INSERT INTO FulcrumReadSnapshots (CacheKey, FetchedTicks, ExpiresTicks, RetryTicks, LeaseTicks) "
              + "SELECT @key, 0, 0, 0, 0 WHERE NOT EXISTS "
              + "(SELECT 1 FROM FulcrumReadSnapshots WITH (UPDLOCK, HOLDLOCK) WHERE CacheKey = @key)", ("@key", key));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static DbCommand Command(DbConnection connection, string sql, params (string Name, object Value)[] values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            if (value is byte[]) parameter.DbType = DbType.Binary;
            if (name is "@key" or "@owner") parameter.DbType = DbType.AnsiString;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static bool IsSqlite(DbConnection connection) => connection.GetType().Name == "SqliteConnection";
    private static DateTimeOffset Time(long ticks) => new(ticks, TimeSpan.Zero);

    private const string SqliteSchema = """
        CREATE TABLE IF NOT EXISTS FulcrumReadSnapshots (
            CacheKey TEXT NOT NULL PRIMARY KEY, Body BLOB NULL, ContentType TEXT NULL,
            FetchedTicks INTEGER NOT NULL, ExpiresTicks INTEGER NOT NULL, RetryTicks INTEGER NOT NULL,
            LeaseOwner TEXT NULL, LeaseTicks INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_FulcrumReadSnapshots_Expiry ON FulcrumReadSnapshots (ExpiresTicks);
        """;

    private const string SqlServerSchema = """
        DECLARE @lockResult int;
        EXEC @lockResult = sys.sp_getapplock @Resource = N'Arda.FulcrumReadSnapshots.Schema',
            @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 30000;
        IF @lockResult < 0 THROW 51000, 'Could not initialize the shared Fulcrum snapshot store.', 1;
        BEGIN TRY
            IF OBJECT_ID(N'FulcrumReadSnapshots', N'U') IS NULL
            BEGIN
                CREATE TABLE FulcrumReadSnapshots (
                    CacheKey varchar(64) NOT NULL CONSTRAINT PK_FulcrumReadSnapshots PRIMARY KEY,
                    Body varbinary(max) NULL, ContentType nvarchar(160) NULL,
                    FetchedTicks bigint NOT NULL, ExpiresTicks bigint NOT NULL, RetryTicks bigint NOT NULL,
                    LeaseOwner varchar(32) NULL, LeaseTicks bigint NOT NULL
                );
                CREATE INDEX IX_FulcrumReadSnapshots_Expiry ON FulcrumReadSnapshots (ExpiresTicks);
            END;
            EXEC sys.sp_releaseapplock @Resource = N'Arda.FulcrumReadSnapshots.Schema', @LockOwner = 'Session';
        END TRY
        BEGIN CATCH
            EXEC sys.sp_releaseapplock @Resource = N'Arda.FulcrumReadSnapshots.Schema', @LockOwner = 'Session';
            THROW;
        END CATCH;
        """;
}
