using System.Text;
using EstimatingDashboard.Api.Services;
using Microsoft.Extensions.Configuration;

namespace EstimatingDashboard.Tests;

public sealed class EnterpriseQuoteSyncConfigurationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Shipped_defaults_preserve_legacy_production_schedule(bool enabled)
    {
        using var configuration = BuildConfiguration($$"""
            {
              "FulcrumQuoteSync": {
                "Enabled": {{enabled.ToString().ToLowerInvariant()}},
                "TimeZoneId": "Eastern Standard Time"
              }
            }
            """);

        var settings = ReadSchedule(configuration);

        Assert.Equal(enabled, settings.Enabled);
        Assert.Equal("Eastern Standard Time", settings.TimeZoneId);
    }

    [Fact]
    public void Shipped_defaults_leave_unconfigured_scheduling_disabled()
    {
        using var configuration = BuildConfiguration("{}");

        var settings = ReadSchedule(configuration);

        Assert.False(settings.Enabled);
        Assert.Equal("Mountain Standard Time", settings.TimeZoneId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_enterprise_schedule_takes_precedence_over_legacy_values(bool enabled)
    {
        using var configuration = BuildConfiguration($$"""
            {
              "FulcrumQuoteSync": {
                "Enabled": {{(!enabled).ToString().ToLowerInvariant()}},
                "TimeZoneId": "Eastern Standard Time"
              },
              "EnterpriseQuoteSync": {
                "Enabled": {{enabled.ToString().ToLowerInvariant()}},
                "TimeZoneId": "Pacific Standard Time"
              }
            }
            """);

        var settings = ReadSchedule(configuration);

        Assert.Equal(enabled, settings.Enabled);
        Assert.Equal("Pacific Standard Time", settings.TimeZoneId);
    }

    [Fact]
    public void Higher_priority_configuration_can_explicitly_disable_legacy_schedule()
    {
        using var configuration = BuildConfiguration(
            """{"FulcrumQuoteSync":{"Enabled":true,"TimeZoneId":"Eastern Standard Time"}}""",
            new Dictionary<string, string?>
            {
                ["EnterpriseQuoteSync:Enabled"] = "false",
                ["EnterpriseQuoteSync:TimeZoneId"] = "UTC"
            });

        var settings = ReadSchedule(configuration);

        Assert.False(settings.Enabled);
        Assert.Equal("UTC", settings.TimeZoneId);
    }

    [Fact]
    public void Invalid_explicit_enterprise_enabled_value_does_not_fall_back_to_legacy()
    {
        using var configuration = BuildConfiguration("""
            {
              "FulcrumQuoteSync": { "Enabled": true },
              "EnterpriseQuoteSync": { "Enabled": "invalid" }
            }
            """);

        Assert.Throws<InvalidOperationException>(() => ReadSchedule(configuration));
    }

    private static ConfigurationRoot BuildConfiguration(
        string productionJson,
        Dictionary<string, string?>? overrides = null)
    {
        // This is the real base JSON copied from the API project by the build,
        // layered with a representative preserved Production file. No app or DB starts.
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(productionJson)));
        if (overrides is not null) builder.AddInMemoryCollection(overrides);
        return (ConfigurationRoot)builder.Build();
    }

    private static EnterpriseQuoteSyncScheduleOptions ReadSchedule(IConfiguration configuration)
    {
        var settings = new EnterpriseQuoteSyncScheduleOptions();
        settings.BindConfiguration(configuration);
        return settings;
    }
}
