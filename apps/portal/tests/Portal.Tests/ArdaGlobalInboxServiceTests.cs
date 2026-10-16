using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Portal.Api.Data;
using Portal.Api.Dtos;
using Portal.Api.Services;
using SonAero.Platform.Security;

namespace Portal.Tests;

public sealed class ArdaGlobalInboxServiceTests
{
    [Fact]
    public async Task Inbox_IsRecipientScoped_AndHidesLostModuleAccess()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.ArdaPushNotifications.AddRange(
            Push(@"SON4L\recipient"), Push(@"SON4L\other"));
        await fixture.Db.SaveChangesAsync();

        var own = await fixture.Inbox.GetAsync(fixture.UserWithQuality, CancellationToken.None);
        Assert.Single(own.Items);
        Assert.Equal(1, own.UnreadCount);
        Assert.StartsWith("push:", own.Items[0].Id);
        Assert.Null(await fixture.Inbox.OpenAsync("push:2", fixture.UserWithQuality, CancellationToken.None));
        Assert.False(await fixture.Inbox.MarkReadAsync("push:2", fixture.UserWithQuality, CancellationToken.None));

        var revoked = fixture.UserWithQuality with { Modules = [] };
        Assert.Empty((await fixture.Inbox.GetAsync(revoked, CancellationToken.None)).Items);
        Assert.Null(await fixture.Inbox.OpenAsync(own.Items[0].Id, revoked, CancellationToken.None));
        Assert.False(await fixture.Inbox.MarkReadAsync(own.Items[0].Id, revoked, CancellationToken.None));
    }

    [Fact]
    public async Task Inbox_ReadAndOpen_RejectsExternalTargetAndPersistsReadState()
    {
        await using var fixture = await Fixture.CreateAsync();
        var good = Push(@"SON4L\recipient");
        var unsafeTarget = Push(@"SON4L\recipient");
        unsafeTarget.TargetUrl = "https://attacker.example/path";
        fixture.Db.ArdaPushNotifications.AddRange(good, unsafeTarget);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Inbox.GetAsync(fixture.UserWithQuality, CancellationToken.None);
        Assert.Single(result.Items);
        Assert.Equal(good.TargetUrl,
            await fixture.Inbox.OpenAsync($"push:{good.Id}", fixture.UserWithQuality, CancellationToken.None));
        Assert.NotNull((await fixture.Db.ArdaPushNotifications.FindAsync(good.Id))!.ReadAt);
        var afterOpen = await fixture.Inbox.GetAsync(fixture.UserWithQuality, CancellationToken.None);
        Assert.Single(afterOpen.Items);
        Assert.Equal(0, afterOpen.UnreadCount);
        Assert.Null(await fixture.Inbox.OpenAsync($"push:{unsafeTarget.Id}", fixture.UserWithQuality,
            CancellationToken.None));
    }

    [Fact]
    public async Task Dismissal_IsDurableIdempotentAndRecipientScoped_WithoutDeletingSource()
    {
        await using var fixture = await Fixture.CreateAsync();
        var own = Push(@"SON4L\recipient");
        var other = Push(@"SON4L\other");
        var unsafeTarget = Push(@"SON4L\recipient");
        unsafeTarget.TargetUrl = "http://localhost:5170.attacker.example/";
        fixture.Db.ArdaPushNotifications.AddRange(own, other, unsafeTarget);
        await fixture.Db.SaveChangesAsync();

        Assert.False(await fixture.Inbox.DismissAsync($"push:{other.Id}", fixture.UserWithQuality, default));
        Assert.False(await fixture.Inbox.DismissAsync($"push:{unsafeTarget.Id}", fixture.UserWithQuality, default));
        Assert.False(await fixture.Inbox.DismissAsync($"push:{own.Id}",
            fixture.UserWithQuality with { Modules = [] }, default));
        Assert.True(await fixture.Inbox.DismissAsync($"push:{own.Id}", fixture.UserWithQuality, default));
        Assert.True(await fixture.Inbox.DismissAsync($"push:00{own.Id}",
            fixture.UserWithQuality with { AccountName = "son4l/recipient" }, default));
        fixture.Db.ChangeTracker.Clear();

        Assert.Single(await fixture.Db.ArdaInboxDismissals.ToListAsync());
        Assert.Equal(3, await fixture.Db.ArdaPushNotifications.CountAsync());
        Assert.Null((await fixture.Db.ArdaPushNotifications.FindAsync(own.Id))!.ReadAt);
        var inbox = await fixture.Inbox.GetAsync(fixture.UserWithQuality, default);
        Assert.Empty(inbox.Items);
        Assert.Equal(0, inbox.UnreadCount);
        Assert.Null(await fixture.Inbox.OpenAsync($"push:{own.Id}", fixture.UserWithQuality, default));
        Assert.False(await fixture.Inbox.MarkReadAsync($"push:{own.Id}", fixture.UserWithQuality, default));
        Assert.Single((await fixture.Inbox.GetAsync(fixture.UserWithQuality with
            { AccountName = other.RecipientAccountName }, default)).Items);
    }

    [Fact]
    public async Task Clear_RemovesReadAndUnreadBeyondFirstPage_AndKeepsFutureAndOtherRecipients()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rows = Enumerable.Range(0, 65).Select(_ => Push(@"SON4L\recipient")).ToList();
        rows[0].ReadAt = DateTimeOffset.UtcNow;
        fixture.Db.ArdaPushNotifications.AddRange(rows);
        fixture.Db.ArdaPushNotifications.Add(Push(@"SON4L\other"));
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(50, (await fixture.Inbox.GetAsync(fixture.UserWithQuality, default)).Items.Count);

        Assert.Equal(65, await fixture.Inbox.ClearAsync(fixture.UserWithQuality, default));
        Assert.Equal(0, await fixture.Inbox.ClearAsync(fixture.UserWithQuality, default));
        Assert.Empty((await fixture.Inbox.GetAsync(fixture.UserWithQuality, default)).Items);
        Assert.Equal(66, await fixture.Db.ArdaPushNotifications.CountAsync());
        Assert.Single((await fixture.Inbox.GetAsync(fixture.UserWithQuality with
            { AccountName = @"SON4L\other" }, default)).Items);

        fixture.Db.ArdaPushNotifications.Add(Push(@"SON4L\recipient"));
        await fixture.Db.SaveChangesAsync();
        var afterNew = await fixture.Inbox.GetAsync(fixture.UserWithQuality, default);
        Assert.Single(afterNew.Items);
        Assert.Equal(1, afterNew.UnreadCount);
    }

    [Fact]
    public async Task ProjectTrackerInbox_IncludesMentionsAndLiveScheduleRows_AndRequiresProjectDetailAccess()
    {
        await using var fixture = await Fixture.CreateAsync();
        var db = fixture.Db;
        var group = new PortalProjectTrackerGroupRecord { Name = "Project readers" };
        group.Permissions.Add(new PortalProjectTrackerPermissionRecord
        {
            PermissionKey = ApplicationPermissions.ProjectDetailView
        });
        var confirmationPermission = new PortalProjectTrackerPermissionRecord
        {
            PermissionKey = "notifications.operationSchedule.confirm"
        };
        group.Permissions.Add(confirmationPermission);
        db.ProjectTrackerGroups.Add(group);
        db.Set<PortalProjectTrackerMembershipRecord>().Add(new PortalProjectTrackerMembershipRecord
        {
            AppUserId = fixture.Recipient.Id,
            Group = group
        });
        var project = new PortalNotificationProjectRecord();
        db.NotificationProjects.Add(project);
        await db.SaveChangesAsync();
        var message = new PortalNotificationMessageRecord { ProjectId = project.Id };
        var task = new PortalNotificationTaskRecord
        {
            ProjectId = project.Id,
            StartDate = new DateOnly(2026, 10, 15),
            EndDate = new DateOnly(2026, 10, 20),
            PercentComplete = 0,
            PercentCompleteManual = true
        };
        db.NotificationMessages.Add(message);
        db.NotificationTasks.Add(task);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        db.UserNotifications.AddRange(
            Tracker("ProjectChatMention", message.Id, null, null),
            Tracker("OperationNoteMention", null, task.Id, null),
            Tracker("OperationStartConfirmation", null, task.Id, task.StartDate),
            Tracker("OperationFinishConfirmation", null, task.Id, task.EndDate),
            Tracker("OperationStartResponse", null, task.Id, null),
            Tracker("OperationFinishResponse", null, task.Id, null),
            Tracker("OperationStartConfirmation", null, task.Id, new DateOnly(2026, 10, 16)));
        await db.SaveChangesAsync();

        var result = await fixture.Inbox.GetAsync(fixture.UserWithQuality, CancellationToken.None);
        Assert.Equal(6, result.UnreadCount);
        Assert.Equal(6, result.Items.Count);
        var confirmation = result.Items.Single(item => item.Title == "OperationStartConfirmation");
        db.Set<PortalProjectTrackerPermissionRecord>().Remove(confirmationPermission);
        await db.SaveChangesAsync();
        Assert.Equal(4, (await fixture.Inbox.GetAsync(fixture.UserWithQuality, default)).Items.Count);
        Assert.Null(await fixture.Inbox.OpenAsync(confirmation.Id, fixture.UserWithQuality, default));
        Assert.False(await fixture.Inbox.DismissAsync(confirmation.Id, fixture.UserWithQuality, default));
        group.Permissions.Add(new PortalProjectTrackerPermissionRecord
        {
            PermissionKey = "notifications.operationSchedule.confirm"
        });
        await db.SaveChangesAsync();
        var first = result.Items[0];
        Assert.StartsWith("project-tracker:", first.Id);
        Assert.Contains("notificationProjectId=", first.TargetUrl);
        Assert.True(await fixture.Inbox.MarkReadAsync(first.Id, fixture.UserWithQuality,
            CancellationToken.None));
        Assert.Equal(5, (await fixture.Inbox.GetAsync(fixture.UserWithQuality, CancellationToken.None)).UnreadCount);

        Assert.True(await fixture.Inbox.DismissAsync(first.Id, fixture.UserWithQuality, default));
        Assert.Equal(5, (await fixture.Inbox.GetAsync(fixture.UserWithQuality, default)).Items.Count);
        Assert.Equal(7, await db.UserNotifications.CountAsync());
        Assert.Equal(5, await fixture.Inbox.ClearAsync(fixture.UserWithQuality, default));
        Assert.Empty((await fixture.Inbox.GetAsync(fixture.UserWithQuality, default)).Items);
        Assert.Equal(7, await db.UserNotifications.CountAsync());
        Assert.Equal(1, await db.NotificationTasks.CountAsync());
        Assert.Equal(1, await db.NotificationMessages.CountAsync());

        db.Set<PortalProjectTrackerPermissionRecord>().RemoveRange(group.Permissions);
        await db.SaveChangesAsync();
        Assert.Empty((await fixture.Inbox.GetAsync(fixture.UserWithQuality, CancellationToken.None)).Items);
        Assert.Null(await fixture.Inbox.OpenAsync(first.Id, fixture.UserWithQuality, CancellationToken.None));

        PortalNotificationRecord Tracker(string kind, int? messageId, int? taskId, DateOnly? scheduleDate) => new()
        {
            RecipientUserId = fixture.Recipient.Id,
            ProjectId = project.Id,
            ProjectMessageId = messageId,
            ProjectTaskId = taskId,
            Kind = kind,
            ActorAccountName = @"SON4L\other",
            Title = kind,
            BodyPreview = "Notification body",
            ScheduledDate = scheduleDate,
            CreatedAt = now
        };
    }

    [Fact]
    public async Task SchemaInitializer_CanRunRepeatedlyOnExistingSqliteDatabase()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initializer = new ArdaPushSchemaInitializer(fixture.Db);
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();
        Assert.Contains("ADD [ReadAt]", ArdaPushSchemaInitializer.SqlServerSchema);
        Assert.Contains("[ArdaInboxDismissals]", ArdaPushSchemaInitializer.SqlServerSchema);
        Assert.Equal(0, await fixture.Db.ArdaInboxDismissals.CountAsync());
    }

    [Fact]
    public async Task SchemaInitializer_AddsReadAtToLegacySqliteTable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var oldTable = connection.CreateCommand())
        {
            oldTable.CommandText = """
                CREATE TABLE "ArdaPushNotifications" (
                    "Id" INTEGER PRIMARY KEY, "RecipientAccountName" TEXT NOT NULL,
                    "SourceModule" TEXT NOT NULL, "SourceNotificationKey" TEXT NOT NULL,
                    "Title" TEXT NOT NULL, "Body" TEXT NOT NULL, "TargetUrl" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL, "NextAttemptAt" TEXT NOT NULL,
                    "DeliveredAt" TEXT NULL, "DeliveryMethod" TEXT NULL,
                    "AttemptCount" INTEGER NOT NULL, "LastError" TEXT NULL,
                    "ForegroundClaimedBy" TEXT NULL, "ForegroundClaimExpiresAt" TEXT NULL
                );
                """;
            await oldTable.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        var initializer = new ArdaPushSchemaInitializer(db);
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ArdaPushNotifications') WHERE name = 'ReadAt';";
        Assert.Equal(1L, await check.ExecuteScalarAsync());
        Assert.Equal(0, await db.ArdaInboxDismissals.CountAsync());
    }

    private static ArdaPushNotificationRecord Push(string recipient) => new()
    {
        RecipientAccountName = recipient,
        SourceModule = "quality-assurance",
        SourceNotificationKey = Guid.NewGuid().ToString("N"),
        Title = "Quality notification",
        Body = "Review shipment",
        TargetUrl = "http://localhost:5170/#/shipping-status",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class Fixture(SqliteConnection connection, PortalRoleDbContext db,
        PortalRoleRecord recipient, ArdaGlobalInboxService inbox) : IAsyncDisposable
    {
        public PortalRoleDbContext Db { get; } = db;
        public PortalRoleRecord Recipient { get; } = recipient;
        public ArdaGlobalInboxService Inbox { get; } = inbox;
        public MeDto UserWithQuality { get; } = new(
            recipient.AccountName, recipient.DisplayName, PortalAccountStatus.Configured, "Viewer",
            [new PortalModuleAccessDto(ApplicationModules.QualityAssurance, "Viewer",
                [QualityAssurancePermissions.ShipmentsView])]);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
            var db = new PortalRoleDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var recipient = new PortalRoleRecord
            {
                AccountName = @"SON4L\recipient", DisplayName = "Recipient", Role = "Viewer", IsActive = true
            };
            db.Users.AddRange(recipient, new PortalRoleRecord
            {
                AccountName = @"SON4L\other", DisplayName = "Other", Role = "Viewer", IsActive = true
            });
            await db.SaveChangesAsync();
            var json = """
                { "Portal": { "Applications": [
                  { "Id": "quality-assurance", "Name": "Quality", "Url": "http://localhost:5170", "Status": "Active" },
                  { "Id": "project-tracker", "Name": "Projects", "Url": "http://localhost:5173", "Status": "Active" }
                ] } }
                """;
            var config = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
            var inbox = new ArdaGlobalInboxService(db, new ApplicationRegistry(config),
                NullLogger<ArdaGlobalInboxService>.Instance);
            return new Fixture(connection, db, recipient, inbox);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
