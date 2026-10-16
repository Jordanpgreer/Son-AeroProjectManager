using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Endpoints;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Tests;

public sealed class UserActivityTests
{
    [Fact]
    public async Task TouchLastSeen_SkipsRecentActivityAndDoesNotSaveOtherTrackedChanges()
    {
        await using var database = await ActivityDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await db.Users.SingleAsync();
        var seenAt = user.LastSeenAt.AddMinutes(1);
        user.DisplayName = "Unsaved edit";

        await UserEndpoints.TouchLastSeenAsync(db, user.Id, seenAt, CancellationToken.None);

        await using var verify = database.CreateContext();
        var persisted = await verify.Users.SingleAsync();
        Assert.Equal("Administrator Choice", persisted.DisplayName);
        Assert.Equal(database.InitialSeenAt, persisted.LastSeenAt);
    }

    [Fact]
    public async Task TouchLastSeen_DoesNotFailIdentityLookupWhenAnotherConnectionHoldsWriteLock()
    {
        await using var database = await ActivityDatabase.CreateAsync();
        await using var writer = new SqliteConnection(database.ConnectionString);
        await writer.OpenAsync();
        await using var transaction = writer.BeginTransaction();
        await using var db = database.CreateContext();
        db.Database.SetCommandTimeout(1);
        var user = await db.Users.AsNoTracking().SingleAsync();

        await UserEndpoints.TouchLastSeenAsync(db, user.Id, user.LastSeenAt.AddHours(1), CancellationToken.None);

        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State == EntityState.Modified);
        await transaction.RollbackAsync();
        await using var verify = database.CreateContext();
        Assert.Equal(database.InitialSeenAt, (await verify.Users.SingleAsync()).LastSeenAt);
    }

    [Fact]
    public async Task TouchLastSeen_PropagatesErrorsOtherThanContention()
    {
        await using var database = await ActivityDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE Users;");

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            UserEndpoints.TouchLastSeenAsync(db, 1, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(1, exception.SqliteErrorCode);
    }

    private sealed class ActivityDatabase : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"arda-user-activity-{Guid.NewGuid():N}.db");
        public string ConnectionString => $"Data Source={path};Pooling=False";
        public DateTimeOffset InitialSeenAt { get; } = DateTimeOffset.UtcNow.AddHours(-1);

        public static async Task<ActivityDatabase> CreateAsync()
        {
            var database = new ActivityDatabase();
            await using var db = database.CreateContext();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE Users (Id INTEGER PRIMARY KEY, AccountName TEXT NOT NULL, DisplayName TEXT NOT NULL,
                    IsActive INTEGER NOT NULL, LastSeenAt TEXT NOT NULL, Role TEXT NULL);
                """);
            db.Users.Add(new AppUser
            {
                Id = 1,
                AccountName = @"SON4L\activity.user",
                DisplayName = "Administrator Choice",
                LastSeenAt = database.InitialSeenAt
            });
            await db.SaveChangesAsync();
            return database;
        }

        public ProjectTrackerDbContext CreateContext() => new(
            new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(ConnectionString).Options);

        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }
}
