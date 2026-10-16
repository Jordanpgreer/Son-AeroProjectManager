using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Tests;

public sealed class UniversalFulcrumSnapshotTests
{
    [Fact]
    public async Task Independent_process_service_graphs_coalesce_identical_concurrent_reads()
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var first = fixture.Client(async _ =>
        { Interlocked.Increment(ref calls); await Task.Delay(150); return Json("{\"version\":1}"); });
        using var second = fixture.Client(_ =>
        { Interlocked.Increment(ref calls); return Task.FromResult(Json("{\"version\":2}")); });
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var request = Read("secret-a");
            using var response = await (i % 2 == 0 ? first : second).SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }));
        Assert.Equal(1, calls);
        Assert.Single(results.Distinct());
        await using var db = fixture.OpenConnection();
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT CacheKey FROM FulcrumReadSnapshots";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) Assert.Matches("^[A-F0-9]{64}$", reader.GetString(0));
    }

    [Fact]
    public async Task Credential_and_filter_changes_never_reuse_other_snapshots()
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var client = fixture.Client(_ => Task.FromResult(Json($"{{\"call\":{++calls}}}")));
        foreach (var (credential, filter) in new[] { ("one", "{}"), ("two", "{}"), ("one", "{\"active\":true}") })
        {
            using var request = Read(credential, filter);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(3, calls);
        using var repeated = Read("one");
        using var cached = await client.SendAsync(repeated);
        Assert.Equal("{\"call\":1}", await cached.Content.ReadAsStringAsync());
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Encoded_query_separators_cannot_collide_with_distinct_parameters()
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var client = fixture.Client(_ => Task.FromResult(Json($"{{\"call\":{++calls}}}")));
        using var first = Read("one");
        first.RequestUri = new Uri("https://api.fulcrumpro.us/api/quotes/list?q=a%26skip%3D1");
        using (await client.SendAsync(first)) { }
        using var second = Read("one");
        second.RequestUri = new Uri("https://api.fulcrumpro.us/api/quotes/list?q=a&skip=1");
        using var response = await client.SendAsync(second);
        Assert.Equal("{\"call\":2}", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Fresh_safety_checks_bypass_cached_responses_and_do_not_overwrite_them()
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var client = fixture.Client(_ => Task.FromResult(Json($"{{\"call\":{++calls}}}")));
        using (var request = Read("one")) using (await client.SendAsync(request)) { }
        using (var request = Read("one"))
        {
            request.Options.Set(FulcrumReadSnapshotHandler.RequireFresh, true);
            using var response = await client.SendAsync(request);
            Assert.Equal("{\"call\":2}", await response.Content.ReadAsStringAsync());
        }
        using (var request = Read("one"))
        {
            using var response = await client.SendAsync(request);
            Assert.Equal("{\"call\":1}", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Expired_snapshot_survives_retry_after_and_other_process_observes_backoff(int status)
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var client = fixture.Client(_ =>
        {
            calls++;
            var response = calls == 1 ? Json("{\"original\":true}") : new HttpResponseMessage((HttpStatusCode)status);
            if (calls > 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        });
        using (var request = Read("one")) using (await client.SendAsync(request)) { }
        fixture.Clock.Advance(TimeSpan.FromMinutes(21));
        using (var request = Read("one"))
        {
            request.Options.Set(FulcrumReadSnapshotHandler.AllowStale, true);
            using var response = await client.SendAsync(request);
            Assert.Equal("{\"original\":true}", await response.Content.ReadAsStringAsync());
            Assert.Equal("stale", Assert.Single(response.Headers.GetValues("X-Arda-Fulcrum-Snapshot")));
        }
        using var other = fixture.Client(_ => throw new Xunit.Sdk.XunitException("Retry-After must prevent another upstream request."));
        using (var request = Read("one"))
        {
            request.Options.Set(FulcrumReadSnapshotHandler.AllowStale, true);
            using var response = await other.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using (var ingest = Read("one"))
            await Assert.ThrowsAsync<HttpRequestException>(() => other.SendAsync(ingest));
        using (var request = Read("one"))
        {
            request.Options.Set(FulcrumReadSnapshotHandler.RequireFresh, true);
            await Assert.ThrowsAsync<HttpRequestException>(() => other.SendAsync(request));
        }
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Authorization_failures_never_fall_back_to_stale_data()
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var client = fixture.Client(_ => Task.FromResult(++calls == 1
            ? Json("{\"private\":true}") : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using (var request = Read("one")) using (await client.SendAsync(request)) { }
        fixture.Clock.Advance(TimeSpan.FromMinutes(21));
        using var blocked = Read("one");
        using var response = await client.SendAsync(blocked);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("https://api.fulcrumpro.us/api/items/a/routing/operations/list", "POST", true)]
    [InlineData("https://api.fulcrumpro.us/api/vendors/a/contacts/list", "POST", true)]
    [InlineData("https://api.fulcrumpro.us/api/items/list/v2", "POST", true)]
    [InlineData("https://api.fulcrumpro.us/api/quotes/a", "GET", true)]
    [InlineData("https://api.fulcrumpro.us/api/quotes/a", "PUT", false)]
    [InlineData("https://api.fulcrumpro.us/api/quotes/create", "POST", false)]
    [InlineData("https://elsewhere.example/api/quotes/list", "POST", false)]
    public void Only_verified_read_endpoints_are_intercepted(string url, string method, bool expected)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        Assert.Equal(expected, FulcrumReadSnapshotHandler.IsSupportedRead(request));
    }

    [Fact]
    public async Task Forbidden_quote_permission_does_not_block_authorized_shipping_reads()
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var client = fixture.Client(request =>
        {
            calls++;
            return Task.FromResult(request.RequestUri!.AbsolutePath.Contains("quotes")
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Json("{\"data\":[]}"));
        });
        using (var request = Read("one")) using (var response = await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var denied = Read("one")) await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(denied));
        using var shipment = Read("one");
        shipment.RequestUri = new Uri("https://api.fulcrumpro.us/api/shipments/list");
        using var allowed = await client.SendAsync(shipment);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Schedule_uses_shared_twenty_minute_boundaries()
    {
        var options = new UniversalFulcrumSyncOptions();
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T14:20:00Z"), options.NextBoundary(DateTimeOffset.Parse("2026-10-15T14:01:00Z")));
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T14:40:00Z"), options.NextBoundary(DateTimeOffset.Parse("2026-10-15T14:20:00Z")));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpRequestMessage Read(string credential, string body = "{}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.fulcrumpro.us/api/quotes/list?Take=5000&Skip=0")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        private long offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-15T14:01:00Z")
            .Add(DateTimeOffset.UtcNow - started).AddTicks(Interlocked.Read(ref offset));
        public void Advance(TimeSpan time) => Interlocked.Add(ref offset, time.Ticks);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "arda-fulcrum-snapshot-test-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly List<ServiceProvider> providers = [];
        public TestClock Clock { get; } = new();
        public SqliteConnection OpenConnection() => new($"Data Source={path};Pooling=False");
        public HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddScoped(_ => OpenConnection());
            services.AddUniversalFulcrumSync(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UniversalFulcrumSync:MinimumRequestSpacingMilliseconds"] = "100",
                ["UniversalFulcrumSync:MaximumConcurrentRequests"] = "2"
            }).Build(), provider => provider.GetRequiredService<SqliteConnection>());
            var provider = services.BuildServiceProvider();
            providers.Add(provider);
            var handler = provider.GetRequiredService<FulcrumReadSnapshotHandler>();
            handler.InnerHandler = new TestHandler(respond);
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        }
        public void Dispose()
        {
            foreach (var provider in providers) provider.Dispose();
            File.Delete(path);
        }
    }

    private sealed class TestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
