using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class PushSubscriptionServiceTests
{
    [Fact]
    public async Task UpsertAndDelete_AffectOnlyTheOwningUser()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var owner = new AppUser { AccountName = @"SON4L\owner", DisplayName = "Owner" };
        var other = new AppUser { AccountName = @"SON4L\other", DisplayName = "Other" };
        db.Users.AddRange(owner, other);
        await db.SaveChangesAsync();

        var service = new PushSubscriptionService(db);
        var request = ValidRequest();
        var saved = await service.UpsertAsync(owner.Id, request);
        Assert.Equal(PushSubscriptionUpsertStatus.Saved, saved.Status);

        var updatedRequest = request with
        {
            Keys = new PushSubscriptionKeysDto(
                Base64Url([4, .. Enumerable.Repeat((byte)8, 64)]),
                Base64Url(Enumerable.Repeat((byte)10, 16).ToArray()))
        };
        Assert.Equal(PushSubscriptionUpsertStatus.Saved, (await service.UpsertAsync(owner.Id, updatedRequest)).Status);
        Assert.Equal(updatedRequest.Keys!.Auth, (await db.PushSubscriptions.SingleAsync()).Auth);

        var takeover = await service.UpsertAsync(other.Id, request);
        Assert.Equal(PushSubscriptionUpsertStatus.EndpointOwnedByAnotherUser, takeover.Status);

        await service.DeleteAsync(other.Id, request.Endpoint);
        Assert.Equal(owner.Id, (await db.PushSubscriptions.SingleAsync()).AppUserId);

        await service.DeleteAsync(owner.Id, request.Endpoint);
        Assert.Empty(await db.PushSubscriptions.ToListAsync());
    }

    [Theory]
    [InlineData("http://push.example.test/subscription")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void Validation_RejectsUnsafeEndpoints(string endpoint)
    {
        var request = ValidRequest() with { Endpoint = endpoint };
        Assert.Contains("endpoint", PushSubscriptionValidation.Validate(request).Keys);
    }

    [Fact]
    public void Validation_RejectsMalformedBrowserKeysAndExpiration()
    {
        var request = new PushSubscriptionUpsertDto(
            "https://push.example.test/subscription",
            -1,
            new PushSubscriptionKeysDto("not-base64", "too-short"));

        var errors = PushSubscriptionValidation.Validate(request);
        Assert.Contains("keys.p256dh", errors.Keys);
        Assert.Contains("keys.auth", errors.Keys);
        Assert.Contains("expirationTime", errors.Keys);
    }

    [Fact]
    public void Validation_AcceptsBrowserGeneratedSubscription()
    {
        Assert.Empty(PushSubscriptionValidation.Validate(ValidRequest()));
    }

    [Fact]
    public void Validation_RejectsEndpointCredentialsAndOversizedValues()
    {
        var credentials = ValidRequest() with { Endpoint = "https://user:password@push.example.test/subscription" };
        var oversized = ValidRequest() with
        {
            Endpoint = $"https://push.example.test/{new string('a', 2100)}",
            Keys = new PushSubscriptionKeysDto(new string('a', 257), new string('a', 129))
        };

        Assert.Contains("endpoint", PushSubscriptionValidation.Validate(credentials).Keys);
        var errors = PushSubscriptionValidation.Validate(oversized);
        Assert.Contains("endpoint", errors.Keys);
        Assert.Contains("keys.p256dh", errors.Keys);
        Assert.Contains("keys.auth", errors.Keys);
    }

    internal static PushSubscriptionUpsertDto ValidRequest() => new(
        "https://push.example.test/subscription/123",
        null,
        new PushSubscriptionKeysDto(
            Base64Url([4, .. Enumerable.Repeat((byte)7, 64)]),
            Base64Url(Enumerable.Repeat((byte)9, 16).ToArray())));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
