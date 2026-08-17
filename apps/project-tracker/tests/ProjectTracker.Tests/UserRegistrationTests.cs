using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Endpoints;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

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

    [Fact]
    public async Task RegisterUserAsync_TrimsAndPersistsConfiguredDisplayName()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await UserEndpoints.RegisterUserAsync(
            new RegisteredUserUpsertDto(
                @"SON4L\preferred.name",
                "  Preferred Name  ",
                true,
                []),
            db,
            CancellationToken.None);

        Assert.Equal("Preferred Name", (await db.Users.SingleAsync()).DisplayName);
    }

    [Fact]
    public async Task RegisterUserAsync_RejectsDisplayNameOver160Characters()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var result = await UserEndpoints.RegisterUserAsync(
            new RegisteredUserUpsertDto(
                @"SON4L\preferred.name",
                new string('x', 161),
                true,
                []),
            db,
            CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>(result);
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task TouchLastSeenAsync_PreservesAdministratorConfiguredDisplayName()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var user = new AppUser
        {
            AccountName = @"SON4L\preferred.name",
            DisplayName = "Administrator Choice",
            IsActive = true,
            LastSeenAt = DateTimeOffset.UnixEpoch
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var seenAt = DateTimeOffset.UtcNow;

        await UserEndpoints.TouchLastSeenAsync(db, user.Id, seenAt, CancellationToken.None);

        var persisted = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal("Administrator Choice", persisted.DisplayName);
        Assert.Equal(seenAt, persisted.LastSeenAt);
    }

    [Fact]
    public async Task UpdateUserAsync_ChangesDisplayNameWithoutChangingAccountOrGroups()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var accessManagers = new AppGroup
        {
            Name = ApplicationGroups.Administrators,
            Permissions =
            [
                new AppGroupPermission { PermissionKey = ApplicationPermissions.AccessManageUsers },
                new AppGroupPermission { PermissionKey = ApplicationPermissions.AccessManageGroups }
            ]
        };
        var user = new AppUser
        {
            AccountName = @"SON4L\preferred.name",
            DisplayName = "Original Name",
            IsActive = true,
            GroupMemberships = [new AppUserGroupMembership { Group = accessManagers }]
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await UserEndpoints.UpdateUserAsync(
            user.Id,
            new RegisteredUserUpsertDto(
                user.AccountName,
                "  Preferred Application Name  ",
                true,
                [accessManagers.Id]),
            db,
            new ModuleAccessService(),
            CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<RegisteredUserDto>>(result);
        var persisted = await db.Users
            .AsNoTracking()
            .Include(candidate => candidate.GroupMemberships)
            .SingleAsync();
        Assert.Equal(@"SON4L\preferred.name", persisted.AccountName);
        Assert.Equal("Preferred Application Name", persisted.DisplayName);
        Assert.Equal(
            new[] { accessManagers.Id },
            persisted.GroupMemberships.Select(membership => membership.AppGroupId).ToArray());
    }
}
