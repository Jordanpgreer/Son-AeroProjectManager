using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
            await db.Users.Where(user => user.Id == userId && user.LastSeenAt == previous.Value)
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.LastSeenAt, seenAt), cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Last-seen is optional telemetry. A busy local database must not deny authenticated
            // access; the next identity request can retry without leaving a modified tracked user.
        }
        finally
        {
            if (db.Database.IsSqlite()) db.Database.SetCommandTimeout(originalTimeout);
        }
    }
}
