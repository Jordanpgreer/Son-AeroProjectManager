using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Endpoints;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Services.Import;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class ArchivedProjectEndpointTests
{
    [Fact]
    public void PermanentDeleteRoute_RequiresTheAdministratorPolicy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<ProjectTrackerDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        builder.Services.AddScoped<ProjectTracker.Api.Services.ProjectAuditService>();
        builder.Services.AddSingleton<ControlledImportReviewStore>();
        var app = builder.Build();
        app.MapGroup("/api").MapArchivedProjectEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == "/api/archived-projects/{id:int}"
                && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("DELETE") == true);

        Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            authorization => authorization.Policy == ArchivedProjectEndpoints.PermanentDeletePolicyName);
    }

    [Fact]
    public void PermanentDeletePolicy_RequiresAdministratorGroupAndDeletePermission()
    {
        var builder = new AuthorizationPolicyBuilder();

        ArchivedProjectEndpoints.ConfigurePermanentDeletePolicy(builder);

        var claims = builder.Build().Requirements.OfType<ClaimsAuthorizationRequirement>().ToList();
        Assert.Contains(claims, requirement =>
            requirement.ClaimType == ApplicationClaimTypes.Group
            && requirement.AllowedValues!.Contains(ApplicationGroups.Administrators));
        Assert.Contains(claims, requirement =>
            requirement.ClaimType == ApplicationClaimTypes.Permission
            && requirement.AllowedValues!.Contains(ProjectTrackerPermissions.ArchivedDelete));
    }

    [Fact]
    public async Task PermanentlyDelete_RejectsActiveProjects()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var project = new Project { ProgramName = "ACTIVE-PROJECT", Version = 4 };
        fixture.Db.Projects.Add(project);
        await fixture.Db.SaveChangesAsync();

        var result = await ArchivedProjectEndpoints.PermanentlyDeleteAsync(
            project.Id,
            new ArchivedProjectPermanentDeleteDto(project.Version, project.ProgramName),
            fixture.Db,
            new ControlledImportReviewStore(),
            CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
        Assert.True(await fixture.Db.Projects.AnyAsync(candidate => candidate.Id == project.Id));
    }

    [Fact]
    public async Task PermanentlyDelete_RequiresCurrentVersionAndExactProjectName()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var project = new Project
        {
            ProgramName = "ARCHIVED-PROJECT",
            DeletedAt = DateTimeOffset.UtcNow,
            Version = 8
        };
        fixture.Db.Projects.Add(project);
        await fixture.Db.SaveChangesAsync();

        var staleResult = await ArchivedProjectEndpoints.PermanentlyDeleteAsync(
            project.Id,
            new ArchivedProjectPermanentDeleteDto(7, project.ProgramName),
            fixture.Db,
            new ControlledImportReviewStore(),
            CancellationToken.None);
        var wrongNameResult = await ArchivedProjectEndpoints.PermanentlyDeleteAsync(
            project.Id,
            new ArchivedProjectPermanentDeleteDto(project.Version, "archived-project"),
            fixture.Db,
            new ControlledImportReviewStore(),
            CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Conflict<ConcurrencyConflictDto>>(staleResult);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>(wrongNameResult);
        Assert.True(await fixture.Db.Projects.IgnoreQueryFilters().AnyAsync(candidate => candidate.Id == project.Id));
    }

    [Fact]
    public async Task PermanentlyDelete_RemovesTheEntireArchivedProjectGraphOnly()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var recipient = new AppUser
        {
            AccountName = "SON4L\\Recipient",
            DisplayName = "Recipient"
        };
        var archived = new Project
        {
            ProgramName = "ARCHIVED-WITH-GRAPH",
            DeletedAt = DateTimeOffset.UtcNow,
            Version = 12
        };
        var firstTask = new ProjectTask { Sequence = 1, Title = "First operation" };
        var dependentTask = new ProjectTask
        {
            Sequence = 2,
            Title = "Dependent operation",
            DependencyTask = firstTask,
            OvertimeDays = [new TaskOvertimeDay { Date = new DateOnly(2026, 8, 22), Note = "Saturday" }]
        };
        archived.Tasks.AddRange([firstTask, dependentTask]);
        var message = new ProjectMessage
        {
            AuthorAccountName = "SON4L\\Author",
            AuthorDisplayName = "Author",
            Body = "Archived project message"
        };
        archived.Messages.Add(message);
        archived.AuditEntries.Add(new ProjectAuditEntry
        {
            Action = "ProjectArchived",
            Summary = "Archived project",
            ChangedByAccountName = "SON4L\\Admin",
            ChangedByDisplayName = "Admin"
        });
        archived.Notifications.Add(new UserNotification
        {
            RecipientUser = recipient,
            ProjectTask = dependentTask,
            ProjectMessage = message,
            Kind = NotificationKind.ProjectChatMention,
            ActorAccountName = "SON4L\\Author",
            ActorDisplayName = "Author",
            Title = "Mention",
            BodyPreview = "Archived project mention"
        });
        var statusHistory = new StatusHistory
        {
            Project = archived,
            ProjectTask = dependentTask,
            EntityName = "Dependent operation",
            OldStatus = "NotStarted",
            NewStatus = "InProgress",
            ChangedBy = "SON4L\\Author"
        };
        var retained = new Project
        {
            ProgramName = "RETAINED-PROJECT",
            Tasks = [new ProjectTask { Sequence = 1, Title = "Retained operation" }]
        };
        fixture.Db.AddRange(recipient, archived, statusHistory, retained);
        await fixture.Db.SaveChangesAsync();
        var archivedId = archived.Id;
        var retainedId = retained.Id;
        fixture.Db.ChangeTracker.Clear();
        var importReviews = new ControlledImportReviewStore();
        var pendingReview = ControlledImportReviewStore.Create(
            "SON4L\\Admin",
            "pending-import.xlsx",
            [],
            new ControlledImportPayload([], []),
            [],
            [],
            new Dictionary<int, long> { [archivedId] = archived.Version },
            new Dictionary<int, long>());
        importReviews.Save(pendingReview);

        var result = await ArchivedProjectEndpoints.PermanentlyDeleteAsync(
            archivedId,
            new ArchivedProjectPermanentDeleteDto(12, "ARCHIVED-WITH-GRAPH"),
            fixture.Db,
            importReviews,
            CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NoContent>(result);
        Assert.False(await fixture.Db.Projects.IgnoreQueryFilters().AnyAsync(project => project.Id == archivedId));
        Assert.False(await fixture.Db.Tasks.IgnoreQueryFilters().AnyAsync(task => task.ProjectId == archivedId));
        Assert.False(await fixture.Db.TaskOvertimeDays.IgnoreQueryFilters().AnyAsync(day => day.ProjectTask.ProjectId == archivedId));
        Assert.False(await fixture.Db.ProjectMessages.IgnoreQueryFilters().AnyAsync(item => item.ProjectId == archivedId));
        Assert.False(await fixture.Db.ProjectAuditEntries.IgnoreQueryFilters().AnyAsync(item => item.ProjectId == archivedId));
        Assert.False(await fixture.Db.UserNotifications.IgnoreQueryFilters().AnyAsync(item => item.ProjectId == archivedId));
        Assert.False(await fixture.Db.StatusHistory.IgnoreQueryFilters().AnyAsync(item => item.ProjectId == archivedId));
        Assert.True(await fixture.Db.Projects.AnyAsync(project => project.Id == retainedId));
        Assert.True(await fixture.Db.Tasks.AnyAsync(task => task.ProjectId == retainedId));
        Assert.True(await fixture.Db.Users.AnyAsync(user => user.Id == recipient.Id));
        Assert.Null(importReviews.Find(pendingReview.Id, pendingReview.AccountName));
    }

    [Fact]
    public async Task PermanentlyDelete_RollsBackAllCleanupWhenTheProjectChangesDuringDeletion()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var archived = new Project
        {
            ProgramName = "ARCHIVED-CONCURRENT-CHANGE",
            DeletedAt = DateTimeOffset.UtcNow,
            Version = 5,
            Tasks = [new ProjectTask { Sequence = 1, Title = "Operation retained by rollback" }],
            Messages =
            [
                new ProjectMessage
                {
                    AuthorAccountName = "SON4L\\Author",
                    AuthorDisplayName = "Author",
                    Body = "Message retained by rollback"
                }
            ]
        };
        fixture.Db.Projects.Add(archived);
        await fixture.Db.SaveChangesAsync();
        var archivedId = archived.Id;
        fixture.Db.ChangeTracker.Clear();

        await fixture.Db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER "BumpProjectVersionAfterTaskDelete"
            AFTER DELETE ON "Tasks"
            BEGIN
                UPDATE "Projects"
                SET "Version" = "Version" + 1
                WHERE "Id" = OLD."ProjectId";
            END;
            """);

        var result = await ArchivedProjectEndpoints.PermanentlyDeleteAsync(
            archivedId,
            new ArchivedProjectPermanentDeleteDto(5, "ARCHIVED-CONCURRENT-CHANGE"),
            fixture.Db,
            new ControlledImportReviewStore(),
            CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Conflict<ConcurrencyConflictDto>>(result);
        fixture.Db.ChangeTracker.Clear();
        var retainedProject = await fixture.Db.Projects
            .IgnoreQueryFilters()
            .SingleAsync(project => project.Id == archivedId);
        Assert.Equal(5, retainedProject.Version);
        Assert.True(await fixture.Db.Tasks.IgnoreQueryFilters().AnyAsync(task => task.ProjectId == archivedId));
        Assert.True(await fixture.Db.ProjectMessages.IgnoreQueryFilters().AnyAsync(message => message.ProjectId == archivedId));
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private DatabaseFixture(SqliteConnection connection, ProjectTrackerDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public ProjectTrackerDbContext Db { get; }

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new ProjectTrackerDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new DatabaseFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
