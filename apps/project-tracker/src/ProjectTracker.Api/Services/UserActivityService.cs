using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ProjectTracker.Api.Data;

namespace ProjectTracker.Api.Services;

public static class UserActivityService
{
    private static readonly TimeSpan ActivityInterval = TimeSpan.FromMinutes(5);

    public static async Task TouchLastSeenAsync(
        ProjectTrackerDbContext db, int userId, DateTimeOffset seenAt, CancellationToken cancellationToken)
    {
        var originalTimeout = db.Database.GetCommandTimeout();
        if (db.Database.IsSqlite()) db.Database.SetCommandTimeout(1);
        try
        {
            var previous = await db.Users.AsNoTracking().Where(user => user.Id == userId)
                .Select(user => (DateTimeOffset?)user.LastSeenAt).SingleOrDefaultAsync(cancellationToken);
            if (previous is null || seenAt - previous.Value < ActivityInterval) return;

            // Only one concurrent identity request advances this timestamp. Do not save unrelated
            // tracked account changes or use UPDATE RETURNING, whose reader holds a write lock.
            try
            {
                await db.Users.Where(user => user.Id == userId && user.LastSeenAt == previous.Value)
                    .ExecuteUpdateAsync(update => update.SetProperty(user => user.LastSeenAt, seenAt), cancellationToken);
            }
            catch (DbException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The optional write can be retried without saving unrelated tracked changes.
                var errorCode = exception switch
                {
                    SqlException sql => sql.Number,
                    SqliteException sqlite => sqlite.SqliteErrorCode,
                    _ => exception.ErrorCode
                };
                db.GetService<ILoggerFactory>().CreateLogger(typeof(UserActivityService).FullName!)
                    .LogWarning("Optional last-seen update failed ({DatabaseErrorType}, code {DatabaseErrorCode}); a later request can retry.",
                        exception.GetType().Name, errorCode);
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Preserve local contention tolerance for this telemetry-only timestamp read.
            // Required account and permission reads happen separately in the caller.
        }
        finally
        {
            if (db.Database.IsSqlite()) db.Database.SetCommandTimeout(originalTimeout);
        }
    }
}
