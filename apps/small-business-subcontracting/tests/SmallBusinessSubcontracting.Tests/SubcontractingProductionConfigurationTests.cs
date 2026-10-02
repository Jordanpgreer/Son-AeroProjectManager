using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using SmallBusinessSubcontracting.Api;

namespace SmallBusinessSubcontracting.Tests;

public sealed class SubcontractingProductionConfigurationTests
{
    [Fact]
    public void Production_template_allows_the_verified_public_and_internal_hosts()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "deployment", "templates")))
            root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "deployment", "templates",
            "small-business-subcontracting.appsettings.Production.json");
        using var template = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("subcontracting.hub.son4l.local;SON-IIS2;localhost",
            template.RootElement.GetProperty("AllowedHosts").GetString());
    }

    [Theory]
    [InlineData("Authentication:Mode", "Development")]
    [InlineData("Authentication:Mode", "Windwos")]
    [InlineData("Database:Provider", "Sqlite")]
    [InlineData("SubcontractingDatabase:Provider", "Sqlite")]
    [InlineData("SubcontractingDatabase:Provider", "Unknown")]
    [InlineData("ConnectionStrings:SubcontractingStore", "Server=(local);Database=ProjectTracker;Integrated Security=True")]
    [InlineData("ConnectionStrings:SubcontractingStore", "Server=(local);Integrated Security=True")]
    public void Unsafe_production_configuration_fails_closed(string key, string value)
    {
        var values = ValidValues();
        values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Throws<InvalidOperationException>(() =>
            SubcontractingDatabaseConfiguration.Validate(configuration, new TestEnvironment()));
    }

    [Fact]
    public void Windows_authentication_and_separate_sql_databases_are_accepted()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ValidValues()).Build();
        SubcontractingDatabaseConfiguration.Validate(configuration, new TestEnvironment());
    }

    [Fact]
    public void Development_sqlite_is_still_supported()
    {
        var values = ValidValues();
        values["Authentication:Mode"] = "Development";
        values["Database:Provider"] = "Sqlite";
        values["SubcontractingDatabase:Provider"] = "Sqlite";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        SubcontractingDatabaseConfiguration.Validate(configuration, new TestEnvironment { EnvironmentName = "Development" });
    }

    private static Dictionary<string, string?> ValidValues() => new()
    {
        ["Authentication:Mode"] = "Windows", ["Database:Provider"] = "SqlServer",
        ["SubcontractingDatabase:Provider"] = "SqlServer",
        ["ConnectionStrings:RoleStore"] = "Server=(local);Database=ProjectTracker;Integrated Security=True",
        ["ConnectionStrings:SubcontractingStore"] = "Server=(local);Database=SmallBusinessSubcontracting;Integrated Security=True"
    };
}

internal sealed class TestEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = "Subcontracting.Tests";
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
