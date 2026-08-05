using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProjectTracker.Api.Configuration;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Endpoints;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class PushNotificationEndpointTests
{
    [Fact]
    public void DeleteSubscription_ExplicitlyBindsItsRequestBody()
    {
        var requestParameter = typeof(PushNotificationEndpoints)
            .GetMethod(nameof(PushNotificationEndpoints.DeleteAsync), BindingFlags.Public | BindingFlags.Static)!
            .GetParameters()
            .Single(parameter => parameter.ParameterType == typeof(ProjectTracker.Api.Dtos.PushSubscriptionDeleteDto));

        Assert.NotNull(requestParameter.GetCustomAttribute<FromBodyAttribute>());
    }

    [Fact]
    public async Task UnregisteredUser_CannotRegisterSubscription()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, @"SON4L\unknown")], "Test"))
        };
        var currentUser = new CurrentUserService(new HttpContextAccessor { HttpContext = context });

        var result = await PushNotificationEndpoints.UpsertAsync(
            PushSubscriptionServiceTests.ValidRequest(),
            db,
            currentUser,
            new PushSubscriptionService(db),
            CancellationToken.None);

        Assert.IsType<ForbidHttpResult>(result);
        Assert.Empty(await db.PushSubscriptions.ToListAsync());
    }

    [Fact]
    public void PublicKey_IsExposedOnlyWhenAllVapidSettingsAreValid()
    {
        var invalid = Assert.IsType<Ok<ProjectTracker.Api.Dtos.PushPublicKeyDto>>(
            PushNotificationEndpoints.GetPublicKey(Options.Create(new WebPushOptions
        {
            Enabled = true,
            PublicKey = "invalid",
            PrivateKey = "invalid",
            Subject = "mailto:push@example.test"
        })));
        Assert.False(invalid.Value!.Enabled);
        Assert.Empty(invalid.Value.PublicKey);

        var valid = PushNotificationEndpoints.GetPublicKey(Options.Create(new WebPushOptions
        {
            Enabled = true,
            PublicKey = PushSubscriptionServiceTests.Base64Url([4, .. Enumerable.Repeat((byte)1, 64)]),
            PrivateKey = PushSubscriptionServiceTests.Base64Url(Enumerable.Repeat((byte)2, 32).ToArray()),
            Subject = "mailto:push@example.test"
        }));
        var result = Assert.IsType<Ok<ProjectTracker.Api.Dtos.PushPublicKeyDto>>(valid);
        Assert.True(result.Value!.Enabled);
        Assert.NotEmpty(result.Value.PublicKey);
    }

    [Fact]
    public void VapidValidation_AllowsDisabledDefaultsAndRejectsInvalidEnabledConfiguration()
    {
        var validator = new WebPushOptionsValidator();
        Assert.True(validator.Validate(null, new WebPushOptions()).Succeeded);
        Assert.False(validator.Validate(null, new WebPushOptions { Enabled = true }).Succeeded);

        var valid = new WebPushOptions
        {
            Enabled = true,
            PublicKey = PushSubscriptionServiceTests.Base64Url([4, .. Enumerable.Repeat((byte)1, 64)]),
            PrivateKey = PushSubscriptionServiceTests.Base64Url(Enumerable.Repeat((byte)2, 32).ToArray()),
            Subject = "https://sonaero.example.test/push"
        };
        Assert.True(validator.Validate(null, valid).Succeeded);
    }

    [Fact]
    public async Task AccessPreview_CannotRegisterOrDeleteSubscriptions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, @"SON4L\admin"),
                    new Claim(AccessPreviewClaimTypes.Active, "true")
                ],
                "Test"))
        };
        var currentUser = new CurrentUserService(new HttpContextAccessor { HttpContext = context });
        var service = new PushSubscriptionService(db);

        var upsert = await PushNotificationEndpoints.UpsertAsync(
            PushSubscriptionServiceTests.ValidRequest(), db, currentUser, service, CancellationToken.None);
        var delete = await PushNotificationEndpoints.DeleteAsync(
            new("https://push.example.test/subscription/123"), db, currentUser, service, CancellationToken.None);

        Assert.IsType<ForbidHttpResult>(upsert);
        Assert.IsType<ForbidHttpResult>(delete);
        Assert.Empty(await db.PushSubscriptions.ToListAsync());
    }
}
