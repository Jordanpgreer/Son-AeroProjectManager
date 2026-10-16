using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ProjectTracker.Api.Data;

public static class SqliteRuntimeConfiguration
{
    public static async Task EnableConcurrentReadsAsync(
        ProjectTrackerDbContext db, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlite()) return;
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var settings = new SqliteConnectionStringBuilder(connection.ConnectionString);
        if (settings.Mode == SqliteOpenMode.Memory || settings.DataSource == ":memory:") return;

        // Tracker owns the role database shared by all local modules. WAL lets their inbox and
        // authorization readers continue while another module commits an edit or import.
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection) await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 30;
            command.CommandText = "SELECT sqlite_version();";
            var nativeVersion = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
            if (!HasWalResetFix(nativeVersion))
                throw new InvalidOperationException($"SQLite {nativeVersion} is not patched for concurrent WAL access. Rebuild all local modules with the current SQLite bundle before starting Arda.");
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The shared SQLite database must support WAL mode for concurrent local modules.");
        }
        finally
        {
            if (closeConnection) await db.Database.CloseConnectionAsync();
        }
    }

    private static bool HasWalResetFix(string? nativeVersion)
    {
        // https://sqlite.org/wal.html#the_wal_reset_bug (includes the two maintained backports).
        if (!Version.TryParse(nativeVersion, out var version)) return false;
        return version >= new Version(3, 51, 3)
            || (version.Major == 3 && version.Minor == 50 && version.Build >= 7)
            || (version.Major == 3 && version.Minor == 44 && version.Build >= 6);
    }
}
