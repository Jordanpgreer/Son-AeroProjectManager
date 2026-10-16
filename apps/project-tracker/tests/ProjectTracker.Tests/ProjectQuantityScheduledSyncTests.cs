using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using SonAero.Platform.Integrations;

namespace ProjectTracker.Tests;

public sealed class ProjectQuantityScheduledSyncTests
{
    [Fact]
    public async Task ScheduledApply_PreservesManualQuantityAndNamedRouting_UpdatesProgressAndResolvesReminder()
    {
        await using var fixture = await Fixture.CreateAsync();
        var project = ActiveProject("MATCH");
        project.RequiredQuantity = 12;
        project.RequiredQuantitySource = ProjectQuantitySources.Manual;
        project.JobQuantity = 5;
        project.JobQuantitySource = ProjectQuantitySources.Fulcrum;
        var originalStart = new DateOnly(2026, 10, 1);
        var task = new ProjectTask
        {
            Title = "User's operation name", Sequence = 1, ExternalSourceProvider = "Fulcrum",
            ExternalSourceOperationId = "operation-1", StartDate = originalStart,
            OriginalStartDate = originalStart, EndDate = new DateOnly(2026, 10, 20),
            PercentCompleteManual = true
        };
        project.Tasks.Add(task);
        project.Tasks.Add(new ProjectTask { Title = "Arda-only work", Sequence = 2 });
        fixture.Db.Projects.Add(project);
        var recipient = new AppUser { AccountName = @"DOMAIN\recipient", DisplayName = "Recipient" };
        fixture.Db.Users.Add(recipient);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.UserNotifications.Add(new UserNotification
        {
            Project = project, ProjectTask = task, RecipientUser = recipient,
            Kind = NotificationKind.OperationStartConfirmation, ScheduledDate = originalStart
        });
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Application.ApplyAsync(project,
            new(90, 40, [], true, [new("operation-1", 1, "Fulcrum renamed operation",
                ActualStartDate: new DateOnly(2026, 10, 10))]),
            "Fulcrum", false, ProjectRoutingSyncMode.PopulateWhenBlank, default, scheduled: true);
        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.Projects.Include(row => row.Tasks).SingleAsync();
        Assert.Equal(12, saved.RequiredQuantity);
        Assert.Equal(ProjectQuantitySources.Manual, saved.RequiredQuantitySource);
        Assert.Equal(40, saved.JobQuantity);
        Assert.Equal(2, saved.Tasks.Count);
        var savedTask = saved.Tasks.Single(row => row.Id == task.Id);
        Assert.Equal("User's operation name", savedTask.Title);
        Assert.Equal(originalStart, savedTask.OriginalStartDate);
        Assert.Equal(new DateOnly(2026, 10, 10), savedTask.ExternalActualStartDate);
        Assert.NotNull((await fixture.Db.UserNotifications.SingleAsync()).RespondedAt);
        Assert.Equal("ARDA-FULCRUM-SYNC", (await fixture.Db.ProjectAuditEntries.SingleAsync()).ChangedByAccountName);
        Assert.False(result.RoutingReset);
    }

    [Fact]
    public async Task UnchangedScheduledSnapshot_UpdatesFreshnessWithoutVersionOrAuditChurn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var project = ActiveProject("UNCHANGED");
        project.RequiredQuantity = 20;
        project.RequiredQuantitySource = "Fulcrum";
        project.JobQuantity = 30;
        project.JobQuantitySource = "Fulcrum";
        fixture.Db.Projects.Add(project);
        await fixture.Db.SaveChangesAsync();
        var version = project.Version;
        var updated = project.UpdatedAt;
        await fixture.Application.ApplyAsync(project, new(20, 30, [], true), "Fulcrum", false,
            ProjectRoutingSyncMode.PopulateWhenBlank, default, scheduled: true);

        Assert.NotNull(project.QuantityLastSyncedAt);
        Assert.Equal(version, project.Version);
        Assert.Equal(updated, project.UpdatedAt);
        Assert.Empty(await fixture.Db.ProjectAuditEntries.ToListAsync());
    }

    [Fact]
    public async Task UnmatchedAndReadOnlyProjectsCannotBeChanged_AndScheduledResetIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var project = ActiveProject("NO-MATCH");
        fixture.Db.Projects.Add(project);
        await fixture.Db.SaveChangesAsync();
        await fixture.Application.ApplyAsync(project, new(20, 30, [], false), "Fulcrum", false,
            ProjectRoutingSyncMode.PopulateWhenBlank, default, scheduled: true);
        Assert.Null(project.RequiredQuantity);
        Assert.Null(project.QuantityLastSyncedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Application.ApplyAsync(project,
            new(20, 30, [], true), "Fulcrum", false, ProjectRoutingSyncMode.ForceOverride, default, scheduled: true));
        project.Status = ProjectStatus.Complete;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Application.ApplyAsync(project,
            new(20, 30, [], true), "Fulcrum", false, ProjectRoutingSyncMode.PopulateWhenBlank, default, scheduled: true));
    }

    [Fact]
    public async Task ConcurrentUserEdit_RollsBackAutomaticSnapshotAndAudit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var project = ActiveProject("CONCURRENT");
        fixture.Db.Projects.Add(project);
        await fixture.Db.SaveChangesAsync();
        await using (var concurrentScope = fixture.Services.CreateAsyncScope())
        {
            var otherDb = concurrentScope.ServiceProvider.GetRequiredService<ProjectTrackerDbContext>();
            var changed = await otherDb.Projects.SingleAsync();
            changed.RequiredQuantity = 77;
            changed.RequiredQuantitySource = "Manual";
            changed.Version++;
            await otherDb.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => fixture.Application.ApplyAsync(project,
            new(20, 30, [], true), "Fulcrum", false, ProjectRoutingSyncMode.PopulateWhenBlank, default, scheduled: true));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(77, (await fixture.Db.Projects.SingleAsync()).RequiredQuantity);
        Assert.Empty(await fixture.Db.ProjectAuditEntries.ToListAsync());
    }

    [Fact]
    public async Task ScheduledRun_SkipsIncompleteIdentifiersAndInactiveRows_ContinuesAfterOneFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        var failed = ActiveProject("FAIL");
        var valid = ActiveProject("VALID");
        var complete = ActiveProject("COMPLETE");
        complete.Status = ProjectStatus.Complete;
        var deleted = ActiveProject("DELETED");
        deleted.DeletedAt = DateTimeOffset.UtcNow;
        var incomplete = ActiveProject("INCOMPLETE");
        incomplete.JobNumber = null;
        fixture.Db.Projects.AddRange(failed, valid, complete, deleted, incomplete);
        await fixture.Db.SaveChangesAsync();

        var scheduler = fixture.Services.GetRequiredService<ProjectQuantityScheduledSyncService>();
        await scheduler.RunAsync(default);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(new[] { "FAIL", "VALID" }, fixture.Provider.Pulled);
        Assert.Equal(10, (await fixture.Db.Projects.SingleAsync(row => row.Id == valid.Id)).RequiredQuantity);
        Assert.Null((await fixture.Db.Projects.SingleAsync(row => row.Id == failed.Id)).RequiredQuantity);
        Assert.Null((await fixture.Db.Projects.SingleAsync(row => row.Id == complete.Id)).RequiredQuantity);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task ScheduledRun_StopsAnUnavailableConnectionInsteadOfRepeatingCallsForEveryProject(int status)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Provider.FailureStatus = (HttpStatusCode)status;
        fixture.Db.Projects.AddRange(ActiveProject("FAIL"), ActiveProject("VALID"));
        await fixture.Db.SaveChangesAsync();
        await fixture.Services.GetRequiredService<ProjectQuantityScheduledSyncService>().RunAsync(default);
        Assert.Equal(new[] { "FAIL" }, fixture.Provider.Pulled);
    }

    [Theory]
    [InlineData("Acumatica", "test-token")]
    [InlineData("Fulcrum", null)]
    public async Task ScheduledRun_DoesNotPullWithoutActiveProviderAndCredential(string provider, string? token)
    {
        await using var fixture = await Fixture.CreateAsync(provider, token);
        fixture.Db.Projects.Add(ActiveProject("VALID"));
        await fixture.Db.SaveChangesAsync();
        await fixture.Services.GetRequiredService<ProjectQuantityScheduledSyncService>().RunAsync(default);
        Assert.Empty(fixture.Provider.Pulled);
    }

    private static Project ActiveProject(string part) => new()
    {
        ProgramName = part, SalesOrderNumber = "101", JobNumber = "202"
    };

    private sealed class Fixture(SqliteConnection connection, ServiceProvider services,
        AsyncServiceScope scope, StubProvider provider) : IAsyncDisposable
    {
        public ServiceProvider Services => services;
        public StubProvider Provider => provider;
        public ProjectTrackerDbContext Db => scope.ServiceProvider.GetRequiredService<ProjectTrackerDbContext>();
        public ProjectQuantitySyncApplicationService Application =>
            scope.ServiceProvider.GetRequiredService<ProjectQuantitySyncApplicationService>();

        public static async Task<Fixture> CreateAsync(string providerName = "Fulcrum", string? token = "test-token")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var provider = new StubProvider();
            var services = new ServiceCollection().AddLogging()
                .AddDbContext<ProjectTrackerDbContext>(options => options.UseSqlite(connection))
                .AddSingleton<IHttpContextAccessor, HttpContextAccessor>()
                .AddScoped<CurrentUserService>().AddScoped<ProjectAuditService>()
                .AddSingleton<ScheduleCalculator>().AddScoped<ProjectMetricsService>()
                .AddScoped<ProjectRoutingSyncService>().AddScoped<ProjectTaskReferenceCleanupService>()
                .AddSingleton<IPushNotificationQueue, PushNotificationQueue>()
                .AddScoped<ProjectNotificationAudienceService>().AddScoped<OperationScheduleReminderService>()
                .AddScoped<ProjectQuantitySyncApplicationService>().AddScoped<ProjectQuantityScheduledSyncService>()
                .AddSingleton<IProjectQuantityProvider>(provider)
                .AddSingleton<IEnterpriseProviderSource>(new StubProviderSource(providerName))
                .AddSingleton<IProjectTrackerIntegrationCredentialReader>(new StubCredentialReader(token))
                .BuildServiceProvider();
            var scope = services.CreateAsyncScope();
            var fixture = new Fixture(connection, services, scope, provider);
            await fixture.Db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class StubProvider : IProjectQuantityProvider
    {
        public string ProviderName => "Fulcrum";
        public string RouteName => EnterpriseDataRoutes.ProjectQuantities;
        public List<string> Pulled { get; } = [];
        public HttpStatusCode? FailureStatus { get; set; }
        public Task<ProjectQuantitySnapshot> PullAsync(Project project, CancellationToken cancellationToken)
        {
            Pulled.Add(project.ProgramName);
            if (project.ProgramName == "FAIL") throw new HttpRequestException("Simulated failure", null, FailureStatus);
            return Task.FromResult(new ProjectQuantitySnapshot(10, 20, [], true));
        }
        public Task<IReadOnlyList<ProjectQuantityLookupOption>> SearchAsync(ProjectQuantityLookupKind kind,
            string query, CancellationToken cancellationToken, string? partNumber = null) =>
            Task.FromResult<IReadOnlyList<ProjectQuantityLookupOption>>([]);
    }

    private sealed class StubProviderSource(string provider) : IEnterpriseProviderSource
    {
        public Task<string> GetActiveProviderAsync(CancellationToken cancellationToken) => Task.FromResult(provider);
    }

    private sealed class StubCredentialReader(string? token) : IProjectTrackerIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string credentialKey, CancellationToken cancellationToken) => Task.FromResult(token);
    }
}
