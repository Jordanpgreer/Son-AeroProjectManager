using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Estimating;

namespace EstimatingDashboard.Tests;

public sealed class EstimatingEstimatorSettingsServiceTests
{
    [Fact]
    public async Task Explicit_settings_override_deterministic_defaults()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EstimatingAccessDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EstimatingAccessDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.EstimatorSettings.AddRange(
            new EstimatingEstimatorSettingRecord
            {
                EstimatorKey = EstimatorSettings.NormalizeKey("Alex Morgan"),
                EstimatorName = "Alex Morgan",
                IsActive = false,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "TEST\\admin"
            },
            new EstimatingEstimatorSettingRecord
            {
                EstimatorKey = EstimatorSettings.NormalizeKey("Abel"),
                EstimatorName = "Abel",
                IsActive = true,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "TEST\\admin"
            });
        await db.SaveChangesAsync();

        var service = new EstimatingEstimatorSettingsService(db);
        var active = await service.GetActiveEstimatorNamesAsync(
            ["Alex Morgan", "Casey Lee", "Abel", "Sales"],
            CancellationToken.None);

        Assert.DoesNotContain("Alex Morgan", active);
        Assert.Contains("Casey Lee", active);
        Assert.Contains("Abel", active);
        Assert.Contains("Sales", active);
    }

    [Fact]
    public void Legacy_former_estimator_is_inactive_until_an_admin_overrides_it()
    {
        Assert.False(EstimatorSettings.IsActiveByDefault("Abel"));
        Assert.False(EstimatorSettings.IsActiveByDefault("Abel Example"));
        Assert.True(EstimatorSettings.IsActiveByDefault("Casey Lee"));
    }
}
