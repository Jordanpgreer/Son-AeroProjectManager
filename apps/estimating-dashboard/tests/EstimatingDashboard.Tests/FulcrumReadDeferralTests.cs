using System.Net;
using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumReadDeferralTests
{
    [Theory]
    [InlineData(429, false)]
    [InlineData(429, true)]
    [InlineData(503, false)]
    [InlineData(503, true)]
    public async Task Local_reads_preserve_original_retry_after_and_resume_at_its_deadline(int upstreamStatus, bool fresh)
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var first = fixture.Client((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage((HttpStatusCode)upstreamStatus);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });
        using (var request = Read(fresh)) using (var response = await first.SendAsync(request, default))
            Assert.Equal((HttpStatusCode)upstreamStatus, response.StatusCode);
        var deadline = fixture.Clock.GetUtcNow().AddSeconds(30);
        using var otherProcess = fixture.Client((_, _) =>
        {
            calls++;
            return Task.FromResult(Json());
        });

        foreach (var seconds in new[] { 20, 9 })
        {
            fixture.Clock.Advance(seconds);
            using var request = Read(fresh);
            var deferred = await Assert.ThrowsAsync<FulcrumReadDeferredException>(() => otherProcess.SendAsync(request, default));
            Assert.Equal(FulcrumReadDeferralReason.Cooldown, deferred.Reason);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, deferred.StatusCode);
            Assert.Equal(deadline, deferred.RetryAt);
            Assert.Equal(deadline, (await fixture.Store.ReadAsync(ThrottleKey, default))!.RetryAt);
            Assert.Equal(1, calls);
        }

        fixture.Clock.Advance(2);
        using var retry = Read(fresh);
        using var recovered = await otherProcess.SendAsync(retry, default);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(401, FulcrumReadDeferralReason.CredentialRejected)]
    [InlineData(403, FulcrumReadDeferralReason.PermissionRejected)]
    public async Task Cached_authorization_denials_are_distinguished_without_extending_them(int upstreamStatus,
        FulcrumReadDeferralReason reason)
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var client = fixture.Client((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage((HttpStatusCode)upstreamStatus) : Json()));
        using (var request = Read(true)) using (var response = await client.SendAsync(request, default))
            Assert.Equal((HttpStatusCode)upstreamStatus, response.StatusCode);
        var deadline = fixture.Clock.GetUtcNow().AddMinutes(5);
        fixture.Clock.Advance(299);
        using (var request = Read(true))
        {
            var deferred = await Assert.ThrowsAsync<FulcrumReadDeferredException>(() => client.SendAsync(request, default));
            Assert.Equal(reason, deferred.Reason);
            Assert.Equal(HttpStatusCode.Forbidden, deferred.StatusCode);
            Assert.Equal(deadline, deferred.RetryAt);
            Assert.Null(deferred.InnerException);
            Assert.Equal(1, calls);
        }
        fixture.Clock.Advance(2);
        using var retry = Read(true);
        using var responseAfterPause = await client.SendAsync(retry, default);
        Assert.Equal(HttpStatusCode.OK, responseAfterPause.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_admission_does_not_start_a_global_pause()
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var client = fixture.Client((_, _) => { calls++; return Task.FromResult(Json()); });
        var slotKey = FulcrumSnapshotStore.Hash(CredentialKey + ":slot:0");
        Assert.True(await fixture.Store.TryAcquireAsync(slotKey, "existing-reader", fixture.Clock.GetUtcNow(),
            TimeSpan.FromMinutes(1), default, false));
        using var cancellation = new CancellationTokenSource();
        using var request = Read(true);
        var pending = client.SendAsync(request, cancellation.Token);
        await Task.Delay(100);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, calls);
        Assert.Null(await fixture.Store.ReadAsync(ThrottleKey, default));
    }

    [Fact]
    public async Task Actual_network_failures_still_pause_other_readers_and_can_start_a_new_pause_after_retry()
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var client = fixture.Client((_, _) => { calls++; throw new HttpRequestException("Synthetic network failure"); });
        var firstDeadline = fixture.Clock.GetUtcNow().AddSeconds(30);
        using (var request = Read(true))
            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, default));
        Assert.Equal(firstDeadline, (await fixture.Store.ReadAsync(ThrottleKey, default))!.RetryAt);
        fixture.Clock.Advance(20);
        using (var request = Read(true))
            await Assert.ThrowsAsync<FulcrumReadDeferredException>(() => client.SendAsync(request, default));
        Assert.Equal(firstDeadline, (await fixture.Store.ReadAsync(ThrottleKey, default))!.RetryAt);
        Assert.Equal(1, calls);

        fixture.Clock.Advance(11);
        using (var request = Read(true))
            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, default));
        Assert.Equal(2, calls);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(30), (await fixture.Store.ReadAsync(ThrottleKey, default))!.RetryAt);
    }

    [Fact]
    public async Task An_actual_upstream_timeout_still_starts_backoff()
    {
        using var fixture = new Fixture();
        using var client = fixture.Client((_, _) => throw new TaskCanceledException("Synthetic upstream timeout"));
        using var request = Read(true);
        await Assert.ThrowsAsync<TaskCanceledException>(() => client.SendAsync(request, default));
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(30), (await fixture.Store.ReadAsync(ThrottleKey, default))!.RetryAt);
    }

    private static readonly string CredentialKey = FulcrumSnapshotStore.Hash("https://api.fulcrumpro.us\nBearer synthetic-token");
    private static readonly string ThrottleKey = FulcrumSnapshotStore.Hash(CredentialKey + ":cooldown");
    private static HttpResponseMessage Json() => new(HttpStatusCode.OK)
        { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
    private static HttpRequestMessage Read(bool fresh)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.fulcrumpro.us/api/quotes/quote");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-token");
        request.Options.Set(FulcrumReadSnapshotHandler.RequireFresh, fresh);
        return request;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-11-19T16:01:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "arda-deferral-test-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly List<ServiceProvider> providers = [];
        public TestClock Clock { get; } = new();
        public FulcrumSnapshotStore Store => providers[0].GetRequiredService<FulcrumSnapshotStore>();
        public HttpMessageInvoker Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddScoped(_ => new SqliteConnection($"Data Source={path};Pooling=False"));
            services.AddUniversalFulcrumSync(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UniversalFulcrumSync:MaximumConcurrentRequests"] = "1",
                ["UniversalFulcrumSync:MinimumRequestSpacingMilliseconds"] = "100"
            }).Build(), provider => provider.GetRequiredService<SqliteConnection>());
            var provider = services.BuildServiceProvider();
            providers.Add(provider);
            var handler = provider.GetRequiredService<FulcrumReadSnapshotHandler>();
            handler.InnerHandler = new Upstream(respond);
            return new HttpMessageInvoker(handler);
        }
        public void Dispose()
        {
            foreach (var provider in providers) provider.Dispose();
            File.Delete(path);
        }
    }
    private sealed class Upstream(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
