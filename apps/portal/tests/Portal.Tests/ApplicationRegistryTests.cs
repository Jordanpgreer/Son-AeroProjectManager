using System.Text;
using Microsoft.Extensions.Configuration;
using Portal.Api.Models;
using Portal.Api.Services;

namespace Portal.Tests;

public sealed class ApplicationRegistryTests
{
    private const string CatalogJson = """
    {
      "Portal": {
        "Applications": [
          { "Id": "beta",  "Name": "Beta",          "Category": "Ops",   "Order": 20, "Status": "Active",     "AllowedRoles": [] },
          { "Id": "alpha", "Name": "Alpha",         "Category": "Ops",   "Order": 10, "Status": "ComingSoon", "AllowedRoles": [] },
          { "Id": "admin", "Name": "Admin Console", "Category": "Admin", "Order": 5,  "Status": "Active",     "AllowedRoles": [ "Admin" ] }
        ]
      }
    }
    """;

    private static IConfiguration BuildConfiguration(string json)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    [Fact]
    public void GetVisibleFor_Viewer_HidesAdminOnlyApplications()
    {
        var registry = new ApplicationRegistry(BuildConfiguration(CatalogJson));

        var visible = registry.GetVisibleFor("Viewer");

        Assert.Equal(2, visible.Count);
        Assert.DoesNotContain(visible, application => application.Id == "admin");
    }

    [Fact]
    public void GetVisibleFor_Admin_SeesAllApplicationsOrderedByOrderThenName()
    {
        var registry = new ApplicationRegistry(BuildConfiguration(CatalogJson));

        var visible = registry.GetVisibleFor("Admin");

        Assert.Equal(new[] { "admin", "alpha", "beta" }, visible.Select(application => application.Id).ToArray());
    }

    [Fact]
    public void GetVisibleFor_IncludesComingSoonEntries()
    {
        var registry = new ApplicationRegistry(BuildConfiguration(CatalogJson));

        var visible = registry.GetVisibleFor("Admin");

        Assert.Contains(visible, application => application.Status == ApplicationStatus.ComingSoon);
    }

    [Fact]
    public void IsVisibleTo_EmptyAllowedRoles_IsVisibleToEveryone()
    {
        var entry = new ApplicationEntry { AllowedRoles = new List<string>() };

        Assert.True(ApplicationRegistry.IsVisibleTo(entry, "Viewer"));
    }

    [Fact]
    public void IsVisibleTo_RoleMatchIsCaseInsensitive()
    {
        var entry = new ApplicationEntry { AllowedRoles = new List<string> { "Admin" } };

        Assert.True(ApplicationRegistry.IsVisibleTo(entry, "admin"));
        Assert.False(ApplicationRegistry.IsVisibleTo(entry, "Viewer"));
    }

    [Fact]
    public void MissingApplicationsSection_YieldsEmptyCatalog()
    {
        var registry = new ApplicationRegistry(BuildConfiguration("{}"));

        Assert.Empty(registry.All);
        Assert.Empty(registry.GetVisibleFor("Admin"));
    }
}
