using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Portal.Api.Configuration;
using Portal.Api.Data;
using Portal.Api.Dtos;
using Portal.Api.Endpoints;
using Portal.Api.Services;
using SonAero.Platform.Notifications;

namespace Portal.Tests;

public sealed class ArdaPushServiceTests
{
    [Fact]
    public void ModuleBrowserRequests_RequireNonSimpleClientHeader()
    {
        var context = new DefaultHttpContext();
        Assert.False(ArdaPushEndpoints.IsModuleClientRequest(context.Request));

        context.Request.Headers[ArdaPushEndpoints.ModuleClientHeader] =
            ArdaPushEndpoints.ModuleClientHeaderValue;
        Assert.True(ArdaPushEndpoints.IsModuleClientRequest(context.Request));
    }

    [Fact]
    public void SqlServerSubscriptionSchema_IndexesFixedWidthEndpointHash()
    {
        Assert.Contains("[EndpointHash] AS CONVERT(binary(32), HASHBYTES('SHA2_256', [Endpoint])) PERSISTED",
            ArdaPushSchemaInitializer.SqlServerSchema);
        Assert.Contains("([EndpointHash])", ArdaPushSchemaInitializer.SqlServerSchema);
        Assert.DoesNotContain("([Endpoint]);", ArdaPushSchemaInitializer.SqlServerSchema);
    }

    [Fact]
    public async Task SubscriptionUpsert_IsValidatedOwnedAndIdempotentlyUpdated()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new ArdaPushSubscriptionService(fixture.Db);
        var request = ValidSubscription();

        Assert.Equal(ArdaPushSubscriptionUpsertStatus.Saved,
            (await service.UpsertAsync(@"SON4L\recipient", request)).Status);
        Assert.Equal(ArdaPushSubscriptionUpsertStatus.EndpointOwnedByAnotherUser,
            (await service.UpsertAsync(@"SON4L\other", request)).Status);

        var updated = request with
        {
            Keys = new ArdaPushSubscriptionKeysDto(
                Base64Url([4, .. Enumerable.Repeat((byte)8, 64)]),
                Base64Url(Enumerable.Repeat((byte)10, 16).ToArray()))
        };
        Assert.Equal(ArdaPushSubscriptionUpsertStatus.Saved,
            (await service.UpsertAsync(@"SON4L\recipient", updated)).Status);
        Assert.Equal(updated.Keys.Auth, (await fixture.Db.ArdaPushSubscriptions.SingleAsync()).Auth);
    }

    [Fact]
    public void SubscriptionValidation_RejectsUnsafeEndpointAndMalformedKeys()
    {
        var request = new ArdaPushSubscriptionUpsertDto(
            "http://push.example.test/user",
            -1,
            new ArdaPushSubscriptionKeysDto("bad", "bad"));

        var errors = ArdaPushSubscriptionValidation.Validate(request);
        Assert.Contains("endpoint", errors);
        Assert.Contains("keys.p256dh", errors);
        Assert.Contains("keys.auth", errors);
        Assert.Contains("expirationTime", errors);
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc", true)]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=abc", true)]
    [InlineData("https://fcm.googleapis.com.evil.example/subscription", false)]
    [InlineData("https://127.0.0.1/subscription", false)]
    [InlineData("https://push.example.test/subscription", false)]
    public void SubscriptionValidation_AllowsOnlyApprovedBrowserPushServices(string endpoint, bool expected) =>
        Assert.Equal(expected, ArdaPushSubscriptionValidation.IsValidEndpoint(endpoint));

    [Fact]
    public async Task Producer_AuthenticatesResolvesCatalogTargetAndDeduplicatesSourceKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Producer();
        var request = new ArdaPushNotificationRequest(
            "mention-42", @"SON4L\recipient", "New mention", "Please review.",
            "/#/shipping-status?shipment=42");

        var unauthorized = await service.EnqueueAsync("quality-assurance", "wrong", request);
        Assert.Equal(ArdaPushEnqueueStatus.Unauthorized, unauthorized.Status);

        var first = await service.EnqueueAsync("quality-assurance", "quality-secret", request);
        var duplicate = await service.EnqueueAsync("QUALITY-ASSURANCE", "quality-secret", request);
        Assert.Equal(ArdaPushEnqueueStatus.Accepted, first.Status);
        Assert.Equal(first.NotificationId, duplicate.NotificationId);
        var saved = await fixture.Db.ArdaPushNotifications.SingleAsync();
        Assert.Equal("quality-assurance", saved.SourceModule);
        Assert.Equal("http://localhost:5170/#/shipping-status?shipment=42", saved.TargetUrl);
    }

    [Theory]
    [InlineData("//evil.example/path")]
    [InlineData("https://evil.example/path")]
    [InlineData("")]
    public async Task Producer_RejectsUnsafeTargetPaths(string targetPath)
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Producer().EnqueueAsync(
            "quality-assurance",
            "quality-secret",
            new ArdaPushNotificationRequest("key", @"SON4L\recipient", "Title", "Body", targetPath));

        Assert.Equal(ArdaPushEnqueueStatus.Invalid, result.Status);
        Assert.Contains("targetPath", result.Errors!);
    }

    [Fact]
    public async Task ForegroundClaim_RequiresVisibleClient_AndAckCompletesInAppDelivery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tracker = new ArdaPresenceTracker();
        var service = new ArdaPushForegroundService(fixture.Db, tracker);
        var request = new ArdaPushForegroundRequest("portal", "tab-1");
        fixture.Db.ArdaPushNotifications.Add(Notification());
        await fixture.Db.SaveChangesAsync();

        Assert.Null(await service.ClaimAsync(@"SON4L\recipient", request));
        tracker.Record(@"SON4L\recipient", "portal", "tab-1", true);
        var claimed = Assert.Single((await service.ClaimAsync(@"SON4L\recipient", request))!);
        Assert.True(await service.AcknowledgeAsync(claimed.Id, @"SON4L\recipient", request));

        var saved = await fixture.Db.ArdaPushNotifications.SingleAsync();
        Assert.NotNull(saved.DeliveredAt);
        Assert.Equal("InApp", saved.DeliveryMethod);
    }

    [Fact]
    public async Task Dispatcher_DefersForVisibleArda_ThenDeliversSecurePortalDeepLink()
    {
        await using var fixture = await Fixture.CreateAsync();
        var notification = Notification();
        fixture.Db.ArdaPushNotifications.Add(notification);
        fixture.Db.ArdaPushSubscriptions.Add(new ArdaPushSubscriptionRecord
        {
            AppUserId = fixture.Recipient.Id,
            Endpoint = "https://fcm.googleapis.com/fcm/send/subscription",
            P256dh = "p256dh",
            Auth = "auth"
        });
        await fixture.Db.SaveChangesAsync();
        var tracker = new ArdaPresenceTracker();
        tracker.Record(@"SON4L\recipient", "portal", "tab-1", true);
        var sender = new RecordingSender(ArdaPushSendStatus.Delivered);
        var worker = fixture.Worker(sender, tracker);

        await worker.DeliverAsync(notification.Id, CancellationToken.None);
        Assert.Empty(sender.Payloads);
        Assert.Null(notification.DeliveredAt);

        tracker.Record(@"SON4L\recipient", "portal", "tab-1", false);
        await worker.DeliverAsync(notification.Id, CancellationToken.None);
        using var payload = JsonDocument.Parse(Assert.Single(sender.Payloads));
        Assert.Equal($"/api/push/open/{notification.Id}", payload.RootElement.GetProperty("targetUrl").GetString());
        Assert.DoesNotContain("localhost:5170", sender.Payloads[0]);
        await fixture.Db.Entry(notification).ReloadAsync();
        Assert.Equal("WebPush", notification.DeliveryMethod);
    }

    private static ArdaPushNotificationRecord Notification() => new()
    {
        RecipientAccountName = @"SON4L\recipient",
        SourceModule = "quality-assurance",
        SourceNotificationKey = Guid.NewGuid().ToString("N"),
        Title = "Quality mention",
        Body = "Please review.",
        TargetUrl = "http://localhost:5170/#/shipping-status?shipment=42"
    };

    private static ArdaPushSubscriptionUpsertDto ValidSubscription() => new(
        "https://fcm.googleapis.com/fcm/send/subscription-123",
        null,
        new ArdaPushSubscriptionKeysDto(
            Base64Url([4, .. Enumerable.Repeat((byte)7, 64)]),
            Base64Url(Enumerable.Repeat((byte)9, 16).ToArray())));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class RecordingSender(ArdaPushSendStatus status) : IArdaWebPushSender
    {
        public List<string> Payloads { get; } = [];
        public Task<ArdaPushSendStatus> SendAsync(
            ArdaPushSubscriptionRecord subscription,
            string payload,
            WebPushOptions options,
            CancellationToken cancellationToken)
        {
            _ = options;
            Payloads.Add(payload);
            return Task.FromResult(status);
        }
    }

    private sealed class Fixture(
        SqliteConnection connection,
        PortalRoleDbContext db,
        IConfiguration configuration,
        PortalRoleRecord recipient) : IAsyncDisposable
    {
        public PortalRoleDbContext Db { get; } = db;
        public PortalRoleRecord Recipient { get; } = recipient;

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
            return new Fixture(connection, db, Configuration(), recipient);
        }

        public ArdaPushProducerService Producer() => new(
            Db,
            new ApplicationRegistry(configuration),
            Options.Create(new WebPushOptions
            {
                ProducerKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["quality-assurance"] = "quality-secret"
                }
            }));

        public ArdaPushDeliveryWorker Worker(IArdaWebPushSender sender, ArdaPresenceTracker tracker)
        {
            var services = new ServiceCollection()
                .AddDbContext<PortalRoleDbContext>(builder => builder.UseSqlite(connection))
                .BuildServiceProvider();
            return new ArdaPushDeliveryWorker(
                services.GetRequiredService<IServiceScopeFactory>(),
                sender,
                Options.Create(new WebPushOptions()),
                tracker,
                NullLogger<ArdaPushDeliveryWorker>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static IConfiguration Configuration()
        {
            const string json = """
            { "Portal": { "Applications": [
              { "Id": "quality-assurance", "Name": "Quality", "Url": "http://localhost:5170", "Status": "Active" }
            ] } }
            """;
            return new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                .Build();
        }
    }
}
