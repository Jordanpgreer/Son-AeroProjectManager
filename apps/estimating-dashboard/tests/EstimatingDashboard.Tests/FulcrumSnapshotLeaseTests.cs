using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumSnapshotLeaseTests
{
    [Fact]
    public async Task ExpiredLease_RecoversAcrossProcesses_AndOldOwnerCannotPublishOrReleaseReplacement()
    {
        using var fixture = new Fixture();
        var first = fixture.Graph().GetRequiredService<FulcrumSnapshotStore>();
        var second = fixture.Graph().GetRequiredService<FulcrumSnapshotStore>();
        var key = FulcrumSnapshotStore.Hash("request");
        var now = fixture.Clock.GetUtcNow();
        Assert.True(await first.TryAcquireAsync(key, "owner-a", now, TimeSpan.FromMinutes(5), default));
        Assert.False(await second.TryAcquireAsync(key, "owner-b", now, TimeSpan.FromMinutes(5), default));

        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        now = fixture.Clock.GetUtcNow();
        Assert.True(await second.TryAcquireAsync(key, "owner-b", now, TimeSpan.FromMinutes(5), default));
        await first.PublishAsync(key, "owner-a", "old"u8.ToArray(), "application/json", now, now.AddMinutes(20), default);
        Assert.Null((await second.ReadAsync(key, default))!.Body);
        await first.ReleaseAsync(key, "owner-a", default);
        Assert.False(await first.TryAcquireAsync(key, "owner-c", now, TimeSpan.FromMinutes(5), default));
        await second.PublishAsync(key, "owner-b", "new"u8.ToArray(), "application/json", now, now.AddMinutes(20), default);
        Assert.Equal("new", Encoding.UTF8.GetString((await first.ReadAsync(key, default))!.Body!));
    }

    [Fact]
    public async Task Pruning_RemovesOldDataAndIdleCoordinationRows_PreservesActiveLeasesAndCooldowns()
    {
        using var fixture = new Fixture();
        var store = fixture.Graph().GetRequiredService<FulcrumSnapshotStore>();
        var now = fixture.Clock.GetUtcNow();
        var old = now.AddDays(-10);
        var expired = FulcrumSnapshotStore.Hash("expired");
        var active = FulcrumSnapshotStore.Hash("active");
        var delayed = FulcrumSnapshotStore.Hash("delayed");
        var idle = FulcrumSnapshotStore.Hash("idle");
        Assert.True(await store.TryAcquireAsync(expired, "a", old, TimeSpan.FromMinutes(1), default));
        await store.PublishAsync(expired, "a", "old"u8.ToArray(), "application/json", old, old.AddMinutes(20), default);
        Assert.True(await store.TryAcquireAsync(active, "b", now, TimeSpan.FromHours(1), default));
        await store.DeferAsync(delayed, now.AddHours(1), default);
        Assert.True(await store.TryAcquireAsync(idle, "c", old, TimeSpan.FromMinutes(1), default));
        await store.ReleaseAsync(idle, "c", default);

        await store.PruneAsync(now, default);
        Assert.Null(await store.ReadAsync(expired, default));
        Assert.Null(await store.ReadAsync(idle, default));
        Assert.NotNull(await store.ReadAsync(active, default));
        Assert.NotNull(await store.ReadAsync(delayed, default));
    }

    [Fact]
    public async Task Coordinators_OnlyRunOneModuleCycleAcrossProcesses_AndRetryAfterCancellation()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureCredentialAsync();
        var first = fixture.Graph().GetRequiredService<FulcrumScheduledRunCoordinator>();
        var second = fixture.Graph().GetRequiredService<FulcrumScheduledRunCoordinator>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var running = first.RunAsync("module", async cancellationToken =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await second.RunAsync("module", _ => { calls++; return Task.CompletedTask; }, default));
        release.SetResult();
        Assert.True(await running);
        Assert.False(await second.RunAsync("module", _ => { calls++; return Task.CompletedTask; }, default));
        Assert.Equal(1, calls);

        fixture.Clock.Advance(TimeSpan.FromMinutes(21));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync("module", ct =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, cancellation.Token));
        Assert.True(await second.RunAsync("module", _ => { calls++; return Task.CompletedTask; }, default));
        Assert.Equal(2, calls);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset current = DateTimeOffset.Parse("2026-10-15T14:01:00Z");
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan interval) => current += interval;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"arda-fulcrum-leases-{Guid.NewGuid():N}.db");
        private readonly List<ServiceProvider> graphs = [];
        public ManualClock Clock { get; } = new();
        private SqliteConnection Connection() => new($"Data Source={path};Pooling=False");

        public ServiceProvider Graph()
        {
            var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(Clock)
                .AddScoped(_ => Connection());
            services.AddUniversalFulcrumSync(new ConfigurationBuilder().Build(),
                provider => provider.GetRequiredService<SqliteConnection>());
            var graph = services.BuildServiceProvider();
            graphs.Add(graph);
            return graph;
        }

        public async Task ConfigureCredentialAsync()
        {
            await using var connection = Connection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = EnterpriseIntegrationSchema.Sqlite + IntegrationCredentialSchema.Sqlite + """
                INSERT INTO IntegrationCredentials
                    (CredentialKey, DisplayName, EncryptedSecret, CreatedAt, UpdatedAt, UpdatedBy)
                VALUES (@key, 'Test', 'nonempty-test-placeholder', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 'TEST');
                """;
            command.Parameters.AddWithValue("@key", IntegrationCredentialNames.FulcrumPublicApi);
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            foreach (var graph in graphs) graph.Dispose();
            File.Delete(path);
        }
    }
}
