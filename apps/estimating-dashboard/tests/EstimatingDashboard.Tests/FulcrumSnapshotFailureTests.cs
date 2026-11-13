using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumSnapshotFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_database_returns_safe_503_without_calling_Fulcrum(bool fresh)
    {
        using var fixture = await Fixture.CreateAsync();
        var failure = new TestDatabaseException("PRIVATE database connection information");
        fixture.ConnectionFailure = () => failure;
        using var client = fixture.Client((_, _) => throw new Xunit.Sdk.XunitException("Must not bypass coordination."));
        using var request = Read(fresh);

        var exception = await Assert.ThrowsAsync<FulcrumCoordinationUnavailableException>(() => client.SendAsync(request, default));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Same(failure, exception.InnerException);
        Assert.Equal("Fulcrum data is temporarily unavailable. Try again shortly.", exception.Message);
    }

    [Theory]
    [InlineData("request", false)]
    [InlineData("slot", false)]
    [InlineData("slot", true)]
    [InlineData("pace", true)]
    [InlineData("pacing-delay", true)]
    public async Task Lease_and_pacing_failures_never_admit_an_upstream_request(string stage, bool fresh)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Fault = (operation, key) => stage switch
        {
            "request" => operation == "acquire" && key == RequestKey,
            "slot" => operation == "acquire" && key == SlotKey,
            "pace" => operation == "acquire" && key == PaceKey,
            _ => operation == "defer" && key == PaceKey
        };
        using var client = fixture.Client((_, _) => throw new Xunit.Sdk.XunitException("Must not bypass admission."));
        using var request = Read(fresh);

        var exception = await Assert.ThrowsAsync<FulcrumCoordinationUnavailableException>(() => client.SendAsync(request, default));

        Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    [Fact]
    public async Task Admission_failure_survives_all_failed_lease_cleanup_attempts()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Fault = (operation, key) => operation == "release" || operation == "defer" && key == PaceKey;
        using var client = fixture.Client((_, _) => throw new Xunit.Sdk.XunitException("Admission failed."));
        using var request = Read();

        var exception = await Assert.ThrowsAsync<FulcrumCoordinationUnavailableException>(() => client.SendAsync(request, default));

        Assert.Contains("PRIVATE-defer", exception.InnerException!.Message);
        Assert.Contains(("release", PaceKey), fixture.Writes);
        Assert.Contains(("release", SlotKey), fixture.Writes);
        Assert.Contains(("release", RequestKey), fixture.Writes);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("defer")]
    public async Task Mandatory_database_writes_do_not_fall_back_to_a_stale_snapshot(string failingWrite)
    {
        using var fixture = await Fixture.CreateAsync();
        var calls = 0;
        using var client = fixture.Client((_, _) => Task.FromResult(++calls == 1 || failingWrite == "publish"
            ? Json("{\"version\":" + calls + "}") : new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        using (var original = Read()) using (await client.SendAsync(original, default)) { }
        fixture.Clock.Advance(TimeSpan.FromMinutes(21));
        fixture.Fault = (operation, key) => operation == failingWrite && (operation == "publish" || key == ThrottleKey);
        using var request = Read(allowStale: true);

        await Assert.ThrowsAsync<FulcrumCoordinationUnavailableException>(() => client.SendAsync(request, default));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Upstream_exception_survives_failed_retry_writes_and_every_lease_is_released()
    {
        using var fixture = await Fixture.CreateAsync();
        var original = new HttpRequestException("Original upstream failure");
        using var client = fixture.Client((_, _) =>
        {
            fixture.Fault = (operation, _) => operation is "defer" or "release";
            throw original;
        });
        using var request = Read();

        var actual = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, default));

        Assert.Same(original, actual);
        Assert.Contains(("defer", RequestKey), fixture.Writes);
        Assert.Contains(("defer", ThrottleKey), fixture.Writes);
        Assert.Contains(("release", SlotKey), fixture.Writes);
        Assert.Contains(("release", RequestKey), fixture.Writes);
    }

    [Fact]
    public async Task Original_cancellation_survives_failed_backoff_and_all_releases()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException(cancellation.Token);
        using var client = fixture.Client((_, _) =>
        {
            fixture.Fault = (operation, _) => operation is "defer" or "release";
            cancellation.Cancel();
            throw original;
        });
        using var request = Read();

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => client.SendAsync(request, cancellation.Token));

        Assert.Same(original, actual);
        Assert.Contains(("defer", ThrottleKey), fixture.Writes);
        Assert.Contains(("release", SlotKey), fixture.Writes);
        Assert.Contains(("release", RequestKey), fixture.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_retry_cleanup_prevents_stale_success_even_if_database_writes_fail(bool failWrites)
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var client = fixture.Client((_, _) =>
        {
            if (++calls == 1) return Task.FromResult(Json("{\"old\":true}"));
            fixture.Fault = (operation, _) =>
            {
                if (operation != "defer") return false;
                cancellation.Cancel();
                return failWrites;
            };
            throw new HttpRequestException("Upstream unavailable");
        });
        using (var seed = Read()) using (await client.SendAsync(seed, default)) { }
        fixture.Clock.Advance(TimeSpan.FromMinutes(21));
        using var request = Read(allowStale: true);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => client.SendAsync(request, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Contains(("defer", RequestKey), fixture.Writes);
        Assert.Contains(("defer", ThrottleKey), fixture.Writes);
        Assert.Contains(("release", SlotKey), fixture.Writes);
        Assert.Contains(("release", RequestKey), fixture.Writes);
    }

    [Fact]
    public async Task Cancelled_database_operation_is_cancellation_instead_of_503()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.ConnectionFailure = () =>
        {
            cancellation.Cancel();
            return new TestDatabaseException("Database operation interrupted");
        };
        using var client = fixture.Client((_, _) => throw new Xunit.Sdk.XunitException("Must not call upstream."));
        using var request = Read(true);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => client.SendAsync(request, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.BadRequest)]
    public async Task Failed_cleanup_does_not_replace_a_live_upstream_response(bool fresh, HttpStatusCode status)
    {
        using var fixture = await Fixture.CreateAsync();
        using var client = fixture.Client((_, _) =>
        {
            fixture.Fault = (operation, _) => operation == "release";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("live upstream body") });
        });
        using var request = Read(fresh);

        using var response = await client.SendAsync(request, default);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("live upstream body", await response.Content.ReadAsStringAsync());
        Assert.Contains(("release", SlotKey), fixture.Writes);
        if (!fresh) Assert.Contains(("release", RequestKey), fixture.Writes);
    }

    [Fact]
    public async Task Retention_failure_does_not_discard_a_successfully_admitted_fresh_response()
    {
        using var fixture = await Fixture.CreateAsync();
        // Pruning is hourly across handler instances; force a later boundary without resetting shared state.
        AdvanceToPruneBoundary(fixture.Clock);
        fixture.Fault = (operation, _) => operation == "prune";
        using var client = fixture.Client((_, _) => Task.FromResult(Json("{\"live\":true}")));
        using var request = Read(true);

        using var response = await client.SendAsync(request, default);

        Assert.Equal("{\"live\":true}", await response.Content.ReadAsStringAsync());
        Assert.Contains(fixture.Writes, write => write.Operation == "prune");
        Assert.Contains(("defer", PaceKey), fixture.Writes);
    }

    [Fact]
    public async Task Cancellation_during_pruning_is_not_replaced_by_a_database_503()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        AdvanceToPruneBoundary(fixture.Clock);
        fixture.Fault = (operation, _) =>
        {
            if (operation != "prune") return false;
            cancellation.Cancel();
            return true;
        };
        using var client = fixture.Client((_, _) => Task.FromResult(Json("{\"live\":true}")));
        using var request = Read(true);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => client.SendAsync(request, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Contains(("release", SlotKey), fixture.Writes);
    }

    private static long pruneBoundaryTicks = DateTimeOffset.UtcNow.AddYears(20).UtcTicks;
    private static void AdvanceToPruneBoundary(TestClock clock) => clock.Advance(TimeSpan.FromTicks(
        Interlocked.Add(ref pruneBoundaryTicks, TimeSpan.FromHours(2).Ticks) - clock.GetUtcNow().UtcTicks));

    private const string Credential = "test-credential";
    private static readonly string CredentialKey = FulcrumSnapshotStore.Hash("https://api.fulcrumpro.us\nBearer " + Credential);
    private static readonly string RequestKey = FulcrumSnapshotStore.Hash(CredentialKey + "\nPOST\nhttps://api.fulcrumpro.us/api/quotes/list?\n{}");
    private static readonly string SlotKey = FulcrumSnapshotStore.Hash(CredentialKey + ":slot:0");
    private static readonly string PaceKey = FulcrumSnapshotStore.Hash(CredentialKey + ":pace");
    private static readonly string ThrottleKey = FulcrumSnapshotStore.Hash(CredentialKey + ":cooldown");

    private static HttpRequestMessage Read(bool fresh = false, bool allowStale = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.fulcrumpro.us/api/quotes/list")
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Credential);
        request.Options.Set(FulcrumReadSnapshotHandler.RequireFresh, fresh);
        request.Options.Set(FulcrumReadSnapshotHandler.AllowStale, allowStale);
        return request;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class TestDatabaseException(string message) : DbException(message);

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "arda-fulcrum-failure-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly ServiceProvider provider;
        private readonly FulcrumSnapshotStore store;
        public TestClock Clock { get; } = new();
        public ConcurrentQueue<(string Operation, string Key)> Writes { get; } = new();
        public Func<string, string, bool>? Fault { get; set; }
        public Func<DbException>? ConnectionFailure { get; set; }

        private Fixture()
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => Connection());
            provider = services.BuildServiceProvider();
            store = new FulcrumSnapshotStore(provider.GetRequiredService<IServiceScopeFactory>(), services =>
            {
                if (ConnectionFailure is not null) throw ConnectionFailure();
                return services.GetRequiredService<SqliteConnection>();
            });
        }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.store.ReadAsync("initialize", default);
            await using var connection = fixture.Connection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO FulcrumReadSnapshots (CacheKey,FetchedTicks,ExpiresTicks,RetryTicks,LeaseTicks)
                    VALUES ('expired-retention-fixture',0,0,0,0);
                CREATE TRIGGER TestSnapshotWrite BEFORE UPDATE ON FulcrumReadSnapshots BEGIN
                    SELECT test_write(CASE
                        WHEN NEW.FetchedTicks <> OLD.FetchedTicks THEN 'publish'
                        WHEN NEW.RetryTicks <> OLD.RetryTicks THEN 'defer'
                        WHEN NEW.LeaseOwner IS NOT NULL THEN 'acquire'
                        ELSE 'release' END, NEW.CacheKey);
                END;
                CREATE TRIGGER TestSnapshotPrune BEFORE DELETE ON FulcrumReadSnapshots BEGIN
                    SELECT test_write('prune', OLD.CacheKey);
                END;
                """;
            await command.ExecuteNonQueryAsync();
            return fixture;
        }

        private SqliteConnection Connection()
        {
            var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.CreateFunction<string, string, int>("test_write", (operation, key) =>
            {
                Writes.Enqueue((operation, key));
                if (Fault?.Invoke(operation, key) == true) throw new InvalidOperationException("PRIVATE-" + operation + " database failure");
                return 0;
            });
            return connection;
        }

        public HttpMessageInvoker Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => new(
            new FulcrumReadSnapshotHandler(store, Options.Create(new UniversalFulcrumSyncOptions()), Clock,
                NullLogger<FulcrumReadSnapshotHandler>.Instance) { InnerHandler = new Upstream(respond) });

        public void Dispose()
        {
            provider.Dispose();
            File.Delete(path);
        }
    }

    private sealed class Upstream(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
