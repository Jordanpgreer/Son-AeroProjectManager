using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Portal.Api.Data;
using Portal.Api.Dtos;
using Portal.Api.Endpoints;
using Portal.Api.Services;
using System.Security.Claims;

namespace Portal.Tests;

public sealed class RaidLogEndpointTests
{
    [Fact]
    public void Raid_log_routes_all_require_authenticated_users()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<PortalUserService>();
        builder.Services.AddDbContext<PortalRoleDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        var app = builder.Build();
        app.MapGroup("/api").MapRaidLogAdminEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/admin/raid-log") == true)
            .ToList();

        Assert.Equal(10, routes.Count);
        Assert.All(routes, endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));
    }

    [Fact]
    public void Portal_context_maps_the_audited_raid_log_relationships()
    {
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var db = new PortalRoleDbContext(options);

        var group = db.Model.FindEntityType(typeof(RaidLogGroupRecord))!;
        var item = db.Model.FindEntityType(typeof(RaidLogItemRecord))!;
        var note = db.Model.FindEntityType(typeof(RaidLogNoteRecord))!;
        var activity = db.Model.FindEntityType(typeof(RaidLogActivityRecord))!;
        var workSession = db.Model.FindEntityType(typeof(RaidLogWorkSessionRecord))!;

        Assert.Equal("RaidLogGroups", group.GetTableName());
        Assert.Equal("RaidLogItems", item.GetTableName());
        Assert.Equal("RaidLogNotes", note.GetTableName());
        Assert.Equal("RaidLogActivity", activity.GetTableName());
        Assert.Equal("RaidLogWorkSessions", workSession.GetTableName());
        Assert.True(group.FindProperty(nameof(RaidLogGroupRecord.Version))!.IsConcurrencyToken);
        Assert.True(item.FindProperty(nameof(RaidLogItemRecord.Version))!.IsConcurrencyToken);
        Assert.True(group.GetIndexes().Single(index => index.Properties.Single().Name == nameof(RaidLogGroupRecord.NormalizedName)).IsUnique);
        Assert.Contains(item.GetIndexes(), index => index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(RaidLogItemRecord.GroupId), nameof(RaidLogItemRecord.CompletedAt), nameof(RaidLogItemRecord.Priority)]));
        Assert.Contains(item.GetIndexes(), index => index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(RaidLogItemRecord.ParentItemId), nameof(RaidLogItemRecord.CompletedAt)]));
        Assert.Equal(DeleteBehavior.Restrict, item.GetForeignKeys().Single(key => key.PrincipalEntityType == item).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, note.GetForeignKeys().Single().DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, activity.GetForeignKeys().Single().DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, workSession.GetForeignKeys().Single().DeleteBehavior);
        Assert.True(workSession.FindProperty(nameof(RaidLogWorkSessionRecord.LastHeartbeatAt))!.IsConcurrencyToken);
        Assert.Contains(workSession.GetIndexes(), index => index.IsUnique
            && index.Properties.Single().Name == nameof(RaidLogWorkSessionRecord.ItemId));
        Assert.Contains(workSession.GetIndexes(), index => index.IsUnique
            && index.Properties.Single().Name == nameof(RaidLogWorkSessionRecord.StartedBy));
    }

    [Fact]
    public async Task Raid_log_initializer_creates_all_tables_and_indexes_on_sqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);

        await new PortalRaidLogSchemaInitializer(db).InitializeAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'index') AND name LIKE 'RaidLog%' OR name LIKE 'IX_RaidLog%';";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        Assert.Contains("RaidLogGroups", names);
        Assert.Contains("RaidLogItems", names);
        Assert.Contains("RaidLogNotes", names);
        Assert.Contains("RaidLogActivity", names);
        Assert.Contains("RaidLogWorkSessions", names);
        Assert.Contains("IX_RaidLogGroups_NormalizedName", names);
        Assert.Contains("IX_RaidLogItems_GroupId_CompletedAt_Priority", names);
        Assert.Contains("IX_RaidLogItems_ParentItemId_CompletedAt", names);
        Assert.Contains("IX_RaidLogNotes_ItemId_CreatedAt", names);
        Assert.Contains("IX_RaidLogActivity_ItemId_OccurredAt", names);
        Assert.Contains("IX_RaidLogWorkSessions_ItemId_StartedAt", names);
        Assert.Contains("IX_RaidLogWorkSessions_ItemId_Open", names);
        Assert.Contains("IX_RaidLogWorkSessions_StartedBy_Open", names);
    }

    [Fact]
    public async Task Raid_log_initializer_adds_dependencies_to_an_existing_sqlite_schema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var oldSchema = connection.CreateCommand())
        {
            oldSchema.CommandText = """
                CREATE TABLE "RaidLogItems" (
                    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "GroupId" INTEGER NOT NULL,
                    "CompletedAt" TEXT NULL,
                    "Priority" TEXT NOT NULL,
                    "AssignedToUserId" INTEGER NULL
                );
                """;
            await oldSchema.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await new PortalRaidLogSchemaInitializer(db).InitializeAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('RaidLogItems') WHERE name = 'ParentItemId';";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Raid_log_allows_history_but_prevents_overlapping_work_sessions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTimeOffset.UtcNow;
        var group = new RaidLogGroupRecord
        {
            Name = "Operations",
            NormalizedName = "OPERATIONS",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = "admin",
            UpdatedBy = "admin",
            Version = 1,
        };
        var firstItem = NewItem(group, "First task", now);
        var secondItem = NewItem(group, "Second task", now);
        db.RaidLogGroups.Add(group);
        db.RaidLogItems.AddRange(firstItem, secondItem);
        await db.SaveChangesAsync();

        db.RaidLogWorkSessions.Add(NewSession(firstItem.Id, "SONAERO\\worker.one", now));
        await db.SaveChangesAsync();

        db.RaidLogWorkSessions.Add(NewSession(firstItem.Id, "SONAERO\\worker.two", now.AddMinutes(1)));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.RaidLogWorkSessions.Add(NewSession(secondItem.Id, "SONAERO\\worker.one", now.AddMinutes(1)));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var completedSession = await db.RaidLogWorkSessions.SingleAsync();
        completedSession.StoppedAt = now.AddMinutes(2);
        completedSession.StoppedBy = "SONAERO\\worker.one";
        completedSession.StopReason = "manual";
        completedSession.LastHeartbeatAt = completedSession.StoppedAt.Value;
        await db.SaveChangesAsync();
        db.RaidLogWorkSessions.Add(NewSession(secondItem.Id, "SONAERO\\worker.one", now.AddMinutes(3)));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.RaidLogWorkSessions.CountAsync());
        Assert.Single(await db.RaidLogWorkSessions.Where(session => session.StoppedAt == null).ToListAsync());
    }

    [Fact]
    public async Task Raid_work_sessions_record_pickup_progress_and_completion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();

        const string account = "SONAERO\\worker.one";
        var now = DateTimeOffset.UtcNow;
        var user = new PortalRoleRecord
        {
            AccountName = account,
            DisplayName = "Worker One",
            Role = "Admin",
            IsActive = true,
            LastSeenAt = now,
        };
        var group = new RaidLogGroupRecord
        {
            Name = "Operations",
            NormalizedName = "OPERATIONS",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = account,
            UpdatedBy = account,
            Version = 1,
        };
        var item = NewItem(group, "Review supplier issue", now);
        db.AddRange(user, group, item);
        await db.SaveChangesAsync();
        var portalUsers = PortalUsers(account);

        db.ChangeTracker.Clear();
        await RaidLogAdminEndpoints.StartWorkAsync(
            item.Id, new RaidLogWorkStartDto("Checking the supplier response.", 1), portalUsers, db, default);
        db.ChangeTracker.Clear();
        var started = await db.RaidLogItems.Include(record => record.WorkSessions).Include(record => record.Activity).SingleAsync();
        Assert.Equal(user.Id, started.AssignedToUserId);
        Assert.Equal(2, started.Version);
        Assert.Equal("Checking the supplier response.", started.WorkSessions.Single().StartNote);
        Assert.Contains(started.Activity, activity => activity.Action == "work-started");

        await RaidLogAdminEndpoints.StopWorkAsync(
            started.Id, new RaidLogWorkStopDto("Supplier reply verified.", started.Version), portalUsers, db, default);
        db.ChangeTracker.Clear();
        var stopped = await db.RaidLogItems.Include(record => record.WorkSessions).SingleAsync();
        var firstSession = stopped.WorkSessions.Single();
        Assert.NotNull(firstSession.StoppedAt);
        Assert.Equal(account, firstSession.StoppedBy);
        Assert.Equal("Supplier reply verified.", firstSession.StopNote);

        await RaidLogAdminEndpoints.StartWorkAsync(
            stopped.Id, new RaidLogWorkStartDto("Preparing the final disposition.", stopped.Version), portalUsers, db, default);
        db.ChangeTracker.Clear();
        var restarted = await db.RaidLogItems.Include(record => record.WorkSessions).SingleAsync();
        await RaidLogAdminEndpoints.SetCompletionAsync(
            restarted.Id, new RaidLogCompletionDto(true, restarted.Version), portalUsers, db, default);
        db.ChangeTracker.Clear();
        var completed = await db.RaidLogItems.Include(record => record.WorkSessions).Include(record => record.Activity).SingleAsync();
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal(account, completed.CompletedBy);
        Assert.Equal(2, completed.WorkSessions.Count);
        Assert.All(completed.WorkSessions, session => Assert.NotNull(session.StoppedAt));
        Assert.Contains(completed.Activity, activity => activity.Action == "completed");
    }

    [Fact]
    public async Task Raid_parent_completion_requires_every_subtask_and_reopening_preserves_the_invariant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();

        const string account = "SONAERO\\worker.one";
        var now = DateTimeOffset.UtcNow;
        var user = new PortalRoleRecord
        {
            AccountName = account,
            DisplayName = "Worker One",
            Role = "Admin",
            IsActive = true,
            LastSeenAt = now,
        };
        var group = new RaidLogGroupRecord
        {
            Name = "Operations",
            NormalizedName = "OPERATIONS",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = account,
            UpdatedBy = account,
            Version = 1,
        };
        var parent = NewItem(group, "Release package", now);
        var subtask = NewItem(group, "Verify certificates", now);
        subtask.ParentItem = parent;
        db.AddRange(user, group, parent, subtask);
        await db.SaveChangesAsync();
        var portalUsers = PortalUsers(account);

        db.ChangeTracker.Clear();
        await RaidLogAdminEndpoints.SetCompletionAsync(
            parent.Id, new RaidLogCompletionDto(true, 1), portalUsers, db, default);
        db.ChangeTracker.Clear();
        Assert.Null((await db.RaidLogItems.SingleAsync(item => item.Id == parent.Id)).CompletedAt);

        db.ChangeTracker.Clear();
        await RaidLogAdminEndpoints.SetCompletionAsync(
            subtask.Id, new RaidLogCompletionDto(true, 1), portalUsers, db, default);
        db.ChangeTracker.Clear();
        var updatedParent = await db.RaidLogItems.SingleAsync(item => item.Id == parent.Id);
        var completedSubtask = await db.RaidLogItems.SingleAsync(item => item.Id == subtask.Id);
        Assert.Equal(2, updatedParent.Version);
        Assert.NotNull(completedSubtask.CompletedAt);

        db.ChangeTracker.Clear();
        await RaidLogAdminEndpoints.SetCompletionAsync(
            parent.Id, new RaidLogCompletionDto(true, updatedParent.Version), portalUsers, db, default);
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.RaidLogItems.SingleAsync(item => item.Id == parent.Id)).CompletedAt);

        db.ChangeTracker.Clear();
        await RaidLogAdminEndpoints.SetCompletionAsync(
            subtask.Id, new RaidLogCompletionDto(false, completedSubtask.Version), portalUsers, db, default);
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.RaidLogItems.SingleAsync(item => item.Id == subtask.Id)).CompletedAt);
    }

    [Fact]
    public async Task Raid_log_versions_reject_a_stale_admin_update()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using (var setup = new PortalRoleDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.RaidLogGroups.Add(new RaidLogGroupRecord
            {
                Name = "Operations",
                NormalizedName = "OPERATIONS",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CreatedBy = "admin",
                UpdatedBy = "admin",
                Version = 1,
            });
            await setup.SaveChangesAsync();
        }

        await using var first = new PortalRoleDbContext(options);
        await using var second = new PortalRoleDbContext(options);
        var firstCopy = await first.RaidLogGroups.SingleAsync();
        var staleCopy = await second.RaidLogGroups.SingleAsync();
        firstCopy.Name = "Operations A";
        firstCopy.Version++;
        await first.SaveChangesAsync();
        staleCopy.Name = "Operations B";
        staleCopy.Version++;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private static RaidLogItemRecord NewItem(RaidLogGroupRecord group, string title, DateTimeOffset now) => new()
    {
        Group = group,
        Title = title,
        Kind = "Action",
        Priority = "Normal",
        CreatedAt = now,
        UpdatedAt = now,
        CreatedBy = "admin",
        UpdatedBy = "admin",
        Version = 1,
    };

    private static RaidLogWorkSessionRecord NewSession(int itemId, string actor, DateTimeOffset now) => new()
    {
        ItemId = itemId,
        StartedAt = now,
        StartedBy = actor,
        LastHeartbeatAt = now,
    };

    private static PortalUserService PortalUsers(string account)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, account)], "Test")),
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "Development",
            ["Portal:DevelopmentRole"] = "Admin",
        }).Build();
        return new PortalUserService(new HttpContextAccessor { HttpContext = httpContext }, configuration, new EmptyRoleStore());
    }

    private sealed class EmptyRoleStore : IPortalRoleStore
    {
        public Task<PortalAccountLookup> FindAccountAsync(string accountName, CancellationToken cancellationToken = default) =>
            Task.FromResult(PortalAccountLookup.Missing());

        public Task<PortalAccountLookup> RegisterPendingAccountAsync(string accountName, string displayName, CancellationToken cancellationToken = default) =>
            Task.FromResult(PortalAccountLookup.Missing());
    }
}
