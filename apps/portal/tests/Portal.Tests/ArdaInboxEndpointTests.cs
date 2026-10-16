using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Portal.Api.Data;
using Portal.Api.Endpoints;
using Portal.Api.Services;
using SonAero.Platform.Security;

namespace Portal.Tests;

public sealed class ArdaInboxEndpointTests
{
    [Theory]
    [InlineData("https://hub.example", true)]
    [InlineData("https://quality.example:6145", true)]
    [InlineData("https://QUALITY.EXAMPLE:6145", true)]
    [InlineData(null, false)]
    [InlineData("null", false)]
    [InlineData("https://quality.example", false)]
    [InlineData("http://quality.example:6145", false)]
    [InlineData("https://quality.example:6145.attacker.example", false)]
    [InlineData("https://quality.example:6145/", false)]
    [InlineData("https://quality.example:6145/path", false)]
    [InlineData("https://quality.example:6145?query=yes", false)]
    [InlineData("https://quality.example:6145#fragment", false)]
    [InlineData("https://user@quality.example:6145", false)]
    [InlineData(" https://quality.example:6145", false)]
    [InlineData("https://quality.example:6145 https://attacker.example", false)]
    public void OriginBoundary_RequiresExactlyAnApprovedBrowserOrigin(string? origin, bool expected)
    {
        var context = Context(origin);
        // A legacy client header must not provide a bypass for a bad or missing Origin.
        context.Request.Headers[ArdaPushEndpoints.ModuleClientHeader] = ArdaPushEndpoints.ModuleClientHeaderValue;
        Assert.Equal(expected, ArdaInboxRequestIntegrity.IsTrustedOrigin(context.Request, new ApplicationRegistry(Configuration())));
        context.Request.Headers.Origin = new StringValues(["https://quality.example:6145", "https://hub.example"]);
        Assert.False(ArdaInboxRequestIntegrity.IsTrustedOrigin(context.Request, new ApplicationRegistry(Configuration())));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("dismiss")]
    [InlineData("clear")]
    public async Task InboxMutation_RejectsMissingOriginBeforeResolvingUserOrWriting(string operation)
    {
        var request = Context(null).Request;
        var registry = new ApplicationRegistry(Configuration());
        var result = operation switch
        {
            "read" => await ArdaPushEndpoints.MarkInboxReadAsync("push:1", request, registry, null!, null!, default),
            "dismiss" => await ArdaPushEndpoints.DismissInboxAsync("push:1", request, registry, null!, null!, default),
            _ => await ArdaPushEndpoints.ClearInboxAsync(request, registry, null!, null!, default)
        };
        Assert.IsType<BadRequest>(result);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("dismiss")]
    [InlineData("clear")]
    public async Task HeaderlessCrossOriginPost_PreservesRecipientStateAndSourceRecords(string operation)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PortalRoleDbContext(new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var own = Notification(@"SON4L\recipient");
        var other = Notification(@"SON4L\other");
        db.ArdaPushNotifications.AddRange(own, other);
        await db.SaveChangesAsync();
        var context = Context("https://quality.example:6145");
        var configuration = Configuration();
        var registry = new ApplicationRegistry(configuration);
        var users = new PortalUserService(new HttpContextAccessor { HttpContext = context }, configuration, new RoleStore());
        var inbox = new ArdaGlobalInboxService(db, registry, NullLogger<ArdaGlobalInboxService>.Instance);
        var id = $"push:{own.Id}";

        var result = operation switch
        {
            "read" => await ArdaPushEndpoints.MarkInboxReadAsync(id, context.Request, registry, users, inbox, default),
            "dismiss" => await ArdaPushEndpoints.DismissInboxAsync(id, context.Request, registry, users, inbox, default),
            _ => await ArdaPushEndpoints.ClearInboxAsync(context.Request, registry, users, inbox, default)
        };

        Assert.Equal(operation == "clear" ? 200 : 204, ((IStatusCodeHttpResult)result).StatusCode);
        var current = await users.CurrentAsync();
        var after = await inbox.GetAsync(current, default);
        Assert.Equal(0, after.UnreadCount);
        Assert.Equal(operation == "read" ? 1 : 0, after.Items.Count);
        Assert.Equal(2, await db.ArdaPushNotifications.CountAsync());
        Assert.Null(other.ReadAt);
        Assert.Single((await inbox.GetAsync(current with { AccountName = other.RecipientAccountName }, default)).Items);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("dismiss")]
    [InlineData("clear")]
    public async Task TrustedOrigin_DoesNotBypassInactiveAccount(string operation)
    {
        var context = Context("https://quality.example:6145");
        var configuration = Configuration();
        var registry = new ApplicationRegistry(configuration);
        var users = new PortalUserService(new HttpContextAccessor { HttpContext = context }, configuration, new RoleStore(false));
        var result = operation switch
        {
            "read" => await ArdaPushEndpoints.MarkInboxReadAsync("push:1", context.Request, registry, users, null!, default),
            "dismiss" => await ArdaPushEndpoints.DismissInboxAsync("push:1", context.Request, registry, users, null!, default),
            _ => await ArdaPushEndpoints.ClearInboxAsync(context.Request, registry, users, null!, default)
        };
        Assert.IsType<ForbidHttpResult>(result);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Authentication:Mode"] = "Windows",
        ["Portal:Applications:0:Id"] = "quality-assurance",
        ["Portal:Applications:0:Name"] = "Quality",
        ["Portal:Applications:0:Url"] = "https://quality.example:6145",
        ["Portal:Applications:0:Status"] = "Active"
    }).Build();

    private static DefaultHttpContext Context(string? origin)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("hub.example");
        if (origin is not null) context.Request.Headers.Origin = origin;
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, @"SON4L\recipient")], "Windows"));
        return context;
    }

    private static ArdaPushNotificationRecord Notification(string recipient) => new()
    {
        RecipientAccountName = recipient, SourceModule = "quality-assurance", SourceNotificationKey = Guid.NewGuid().ToString("N"),
        Title = "Review shipment", Body = "Review shipment", TargetUrl = "https://quality.example:6145/#/shipping-status",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class RoleStore(bool active = true) : IPortalRoleStore
    {
        public Task<PortalAccountLookup> FindAccountAsync(string accountName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PortalAccountLookup(PortalAccountLookupStatus.Found, active, "Viewer", "Recipient", false,
                new Dictionary<string, string> { [ApplicationModules.QualityAssurance] = "Viewer" }));
        public Task<PortalAccountLookup> RegisterPendingAccountAsync(string accountName, string displayName, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected registration");
    }
}
