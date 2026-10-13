using System.IO.Compression;
using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Endpoints;
using EstimatingDashboard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EstimatingDashboard.Tests;

public sealed class OutlookConnectorPackageTests
{
    [Theory]
    [InlineData("http://arda.example.test")]
    [InlineData("https://user:password@arda.example.test")]
    [InlineData("https://arda.example.test/?redirect=other")]
    [InlineData("https://arda.example.test/#fragment")]
    [InlineData("//arda.example.test")]
    [InlineData("")]
    public void Rejects_untrusted_or_insecure_windows_auth_destinations(string address) =>
        Assert.False(OutlookConnectorPackage.TryGetDestination(address, out _));

    [Fact]
    public void Default_is_fixed_and_deployment_may_configure_https_path()
    {
        Assert.True(OutlookConnectorPackage.TryGetDestination(null, out var defaultAddress));
        Assert.Equal("https://estimating.hub.son4l.local/", defaultAddress!.AbsoluteUri);
        Assert.True(OutlookConnectorPackage.TryGetDestination("https://arda.example.test/estimating/", out var configured));
        Assert.Equal("/estimating/", configured!.AbsolutePath);
    }

    [Fact]
    public void Published_package_contains_installation_lifecycle_and_deployment_configuration()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "OutlookConnector");
        Assert.True(OutlookConnectorPackage.IsAvailable(directory));
        using var archive = new ZipArchive(new MemoryStream(OutlookConnectorPackage.Create(directory,
            new Uri("https://arda.example.test/estimating/"))), ZipArchiveMode.Read);
        foreach (var name in new[] { "Install.cmd", "Start.cmd", "Start-Background.cmd", "Stop.cmd", "Status.cmd",
            "Uninstall.cmd", "Start-OutlookEstimating.ps1", "Connector.Core.ps1", "Connector.Outlook.ps1",
            "Connector.Lifecycle.ps1", "Connector.Session.ps1", "Manage-Connector.ps1", "Test-Lifecycle.ps1", "README.md" })
            Assert.NotNull(archive.GetEntry(name));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith(".example", StringComparison.OrdinalIgnoreCase));
        Assert.Single(archive.Entries.Where(entry => entry.FullName == "configuration.json"));
        using var document = JsonDocument.Parse(archive.GetEntry("configuration.json")!.Open());
        Assert.Equal("https://arda.example.test/estimating", document.RootElement.GetProperty("baseUrl").GetString());
        Assert.Equal("", document.RootElement.GetProperty("mailbox").GetString());
        Assert.Equal(120, document.RootElement.GetProperty("pollIntervalSeconds").GetInt32());
        Assert.All(archive.Entries, entry => Assert.Equal(Path.GetFileName(entry.FullName), entry.FullName));
    }

    [Fact]
    public void Incomplete_package_is_not_advertised_as_installable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "arda-connector-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Start.cmd"), "@echo off");
            Assert.False(OutlookConnectorPackage.IsAvailable(directory));
            Assert.Throws<DirectoryNotFoundException>(() => OutlookConnectorPackage.Create(directory, new Uri("https://arda.example.test")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Download_requires_quote_visibility_and_editor_permission()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.MapGroup("/api").MapOutlookConnectorEndpoints();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));
        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        Assert.Contains(policies, policy => policy.Policy == EstimatingPolicies.QuoteStatusView);
        Assert.Contains(policies, policy => policy.Policy == EstimatingPolicies.Editor);
    }
}
