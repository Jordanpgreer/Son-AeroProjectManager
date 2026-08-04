using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Endpoints;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Tests;

public sealed class UserRegistrationTests
{
    [Fact]
    public async Task RegisterUserAsync_DoesNotCreateModuleAssignments()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var group = new AppGroup
        {
            Name = "Approved Project Tracker Users",
            Description = "Test access group"
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        await UserEndpoints.RegisterUserAsync(
            new RegisteredUserUpsertDto(
                "SON4L/new.employee",
                "New Employee",
                true,
                [group.Id]),
            db,
            CancellationToken.None);

        var user = await db.Users
            .Include(candidate => candidate.GroupMemberships)
            .Include(candidate => candidate.ModuleAccessAssignments)
            .SingleAsync();
        Assert.Equal(@"SON4L\new.employee", user.AccountName);
        Assert.Single(user.GroupMemberships);
        Assert.Empty(user.ModuleAccessAssignments);
        Assert.Empty(await db.UserModuleAccess.ToListAsync());
    }
}
