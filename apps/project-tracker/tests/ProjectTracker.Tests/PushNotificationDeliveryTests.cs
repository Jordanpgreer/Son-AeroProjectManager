using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProjectTracker.Api.Configuration;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class PushNotificationDeliveryTests
{
    [Fact]
    public async Task DeliverAsync_RemovesStaleSubscriptionAndBuildsDurableDeepLink()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var user = new AppUser { AccountName = @"SON4L\recipient", DisplayName = "Recipient" };
        var project = new Project { ProgramName = "Push Test" };
        var task = new ProjectTask { Project = project, Sequence = 1, Title = "Inspect" };
        project.Tasks.Add(task);
        db.AddRange(user, project);
        await db.SaveChangesAsync();
        var notification = new UserNotification
        {
            RecipientUserId = user.Id,
            ProjectId = project.Id,
            ProjectTaskId = task.Id,
            Kind = NotificationKind.OperationNoteMention,
            ActorAccountName = @"SON4L\actor",
            ActorDisplayName = "Actor",
            Title = "Actor mentioned you",
            BodyPreview = "Please review."
        };
        db.UserNotifications.Add(notification);
        db.PushSubscriptions.Add(new PushSubscriptionRecord
        {
            AppUserId = user.Id,
            Endpoint = "https://push.example.test/stale",
            P256dh = "secret-p256dh",
            Auth = "secret-auth"
        });
        await db.SaveChangesAsync();

        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var sender = new RecordingSender(PushSendStatus.Stale);
        var options = Options.Create(new WebPushOptions
        {
            Enabled = true,
            PublicKey = "public",
            PrivateKey = "private",
            Subject = "mailto:push@example.test"
        });
        var worker = new PushNotificationWorker(
            new PushNotificationQueue(),
            services.GetRequiredService<IServiceScopeFactory>(),
            sender,
            options,
            NullLogger<PushNotificationWorker>.Instance);

        await worker.DeliverAsync(notification.Id, CancellationToken.None);

        Assert.Empty(await db.PushSubscriptions.AsNoTracking().ToListAsync());
        using var payload = JsonDocument.Parse(Assert.Single(sender.Payloads));
        var root = payload.RootElement;
        Assert.Equal(notification.Title, root.GetProperty("title").GetString());
        Assert.Equal(notification.BodyPreview, root.GetProperty("body").GetString());
        Assert.Equal(notification.Id, root.GetProperty("notificationId").GetInt32());
        Assert.Equal(project.Id, root.GetProperty("projectId").GetInt32());
        Assert.Contains($"notificationProjectId={project.Id}", root.GetProperty("url").GetString());
        Assert.Contains($"notificationTaskId={task.Id}", root.GetProperty("url").GetString());
        Assert.StartsWith("/?", root.GetProperty("url").GetString());
    }

    [Fact]
    public async Task MentionNotifications_AreQueuedOnlyAfterPersistenceIsExplicitlyCompleted()
    {
        var queue = new RecordingQueue();
        var service = new MentionNotificationService(queue);
        var notification = new UserNotification();

        service.DispatchAfterPersistence([notification]);
        Assert.Empty(queue.NotificationIds);

        notification.Id = 42;
        service.DispatchAfterPersistence([notification]);
        Assert.Equal([42], queue.NotificationIds);
        await Task.CompletedTask;
    }

    private sealed class RecordingSender(PushSendStatus status) : IWebPushSender
    {
        public List<string> Payloads { get; } = [];

        public Task<PushSendStatus> SendAsync(
            PushSubscriptionRecord subscription,
            string payload,
            WebPushOptions options,
            CancellationToken cancellationToken)
        {
            Payloads.Add(payload);
            return Task.FromResult(status);
        }
    }

    private sealed class RecordingQueue : IPushNotificationQueue
    {
        public List<int> NotificationIds { get; } = [];
        public bool TryEnqueue(int notificationId)
        {
            NotificationIds.Add(notificationId);
            return true;
        }

        public async IAsyncEnumerable<int> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
