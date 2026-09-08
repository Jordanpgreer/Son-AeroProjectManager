using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Portal.Api.Data;
using Portal.Api.Endpoints;
using Portal.Api.Services;

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

        Assert.Equal(7, routes.Count);
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

        Assert.Equal("RaidLogGroups", group.GetTableName());
        Assert.Equal("RaidLogItems", item.GetTableName());
        Assert.Equal("RaidLogNotes", note.GetTableName());
        Assert.Equal("RaidLogActivity", activity.GetTableName());
        Assert.True(group.FindProperty(nameof(RaidLogGroupRecord.Version))!.IsConcurrencyToken);
        Assert.True(item.FindProperty(nameof(RaidLogItemRecord.Version))!.IsConcurrencyToken);
        Assert.True(group.GetIndexes().Single(index => index.Properties.Single().Name == nameof(RaidLogGroupRecord.NormalizedName)).IsUnique);
        Assert.Contains(item.GetIndexes(), index => index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(RaidLogItemRecord.GroupId), nameof(RaidLogItemRecord.CompletedAt), nameof(RaidLogItemRecord.Priority)]));
        Assert.Equal(DeleteBehavior.Cascade, note.GetForeignKeys().Single().DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, activity.GetForeignKeys().Single().DeleteBehavior);
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
        Assert.Contains("IX_RaidLogGroups_NormalizedName", names);
        Assert.Contains("IX_RaidLogItems_GroupId_CompletedAt_Priority", names);
        Assert.Contains("IX_RaidLogNotes_ItemId_CreatedAt", names);
        Assert.Contains("IX_RaidLogActivity_ItemId_OccurredAt", names);
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
}
