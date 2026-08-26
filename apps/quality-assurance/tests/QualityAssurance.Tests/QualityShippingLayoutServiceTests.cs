using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Tests;

public sealed class QualityShippingLayoutServiceTests
{
    [Fact]
    public async Task Saved_layout_persists_drag_order_and_narrow_width_for_one_user_only()
    {
        await using var fixture = await LayoutFixture.CreateAsync();
        var defaults = await fixture.Service.GetAsync(fixture.UserOne, CancellationToken.None);
        var columns = defaults.Columns.Select(column => column with { }).ToList();
        var action = columns.Single(column => column.Key == "nextAction");
        columns.Remove(action);
        columns.Insert(0, action with { Width = 28 });

        var saved = await fixture.Service.SaveAsync(
            new QualityShippingLayoutUpdateDto(columns, defaults.Version),
            fixture.UserOne,
            CancellationToken.None);
        var reloaded = await new QualityShippingLayoutService(fixture.Db)
            .GetAsync(fixture.UserOne, CancellationToken.None);
        var otherUser = await fixture.Service.GetAsync(fixture.UserTwo, CancellationToken.None);

        Assert.Equal(1, saved.Version);
        Assert.Equal("nextAction", reloaded.Columns[0].Key);
        Assert.Equal(28, reloaded.Columns[0].Width);
        Assert.All(reloaded.Columns, column => Assert.True(column.IsVisible));
        Assert.Equal(0, otherUser.Version);
        Assert.Equal("status", otherUser.Columns[0].Key);
        Assert.All(otherUser.Columns, column => Assert.True(column.IsVisible));
    }

    [Fact]
    public async Task Shipping_columns_cannot_be_hidden_from_the_live_layout()
    {
        await using var fixture = await LayoutFixture.CreateAsync();
        var defaults = await fixture.Service.GetAsync(fixture.UserOne, CancellationToken.None);

        foreach (var key in new[] { "status", "customer", "comments" })
        {
            var columns = defaults.Columns
                .Select(column => column.Key == key ? column with { IsVisible = false } : column)
                .ToList();
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SaveAsync(
                new QualityShippingLayoutUpdateDto(columns, defaults.Version),
                fixture.UserOne,
                CancellationToken.None));
            Assert.Contains("must remain visible", exception.Message);
        }
    }

    [Fact]
    public async Task Invalid_widths_and_stale_versions_are_rejected_and_reset_restores_defaults()
    {
        await using var fixture = await LayoutFixture.CreateAsync();
        var defaults = await fixture.Service.GetAsync(fixture.UserOne, CancellationToken.None);
        var invalid = defaults.Columns
            .Select(column => column.Key == "customer" ? column with { Width = 999 } : column)
            .ToList();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SaveAsync(
            new QualityShippingLayoutUpdateDto(invalid, defaults.Version),
            fixture.UserOne,
            CancellationToken.None));

        var saved = await fixture.Service.SaveAsync(
            new QualityShippingLayoutUpdateDto(defaults.Columns, defaults.Version),
            fixture.UserOne,
            CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => fixture.Service.SaveAsync(
            new QualityShippingLayoutUpdateDto(saved.Columns, 0),
            fixture.UserOne,
            CancellationToken.None));

        var reset = await fixture.Service.ResetAsync(fixture.UserOne, CancellationToken.None);
        Assert.Equal(0, reset.Version);
        Assert.Equal("status", reset.Columns[0].Key);
        Assert.False(await fixture.Db.ShippingLayoutPreferences.AnyAsync());
    }

    private sealed class LayoutFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private LayoutFixture(SqliteConnection connection, QualityAssuranceDbContext db)
        {
            this.connection = connection;
            Db = db;
            Service = new QualityShippingLayoutService(db);
            UserOne = Access(41, "TEST\\one", "User One");
            UserTwo = Access(42, "TEST\\two", "User Two");
        }

        public QualityAssuranceDbContext Db { get; }
        public QualityShippingLayoutService Service { get; }
        public QualityAssuranceAccessProfile UserOne { get; }
        public QualityAssuranceAccessProfile UserTwo { get; }

        public static async Task<LayoutFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new QualityAssuranceDbContext(new DbContextOptionsBuilder<QualityAssuranceDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            return new LayoutFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static QualityAssuranceAccessProfile Access(int id, string accountName, string displayName) =>
            new(
                id,
                accountName,
                displayName,
                ApplicationRoles.Viewer,
                QualityAssurancePermissions.ViewerDefaults,
                []);
    }
}
