using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Portal.Api.Data;
using Portal.Api.Endpoints;
using SonAero.Platform.Security;

namespace Portal.Tests;

public sealed class IntegrationCredentialAdminEndpointTests
{
    [Fact]
    public void Integration_credential_routes_require_authenticated_users()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<PortalRoleDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        var app = builder.Build();
        app.MapGroup("/api").MapIntegrationCredentialAdminEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/api/admin/integration-credentials") == true)
            .ToList();

        Assert.Equal(3, routes.Count);
        Assert.All(routes, endpoint =>
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));
    }

    [Fact]
    public void Portal_context_maps_encrypted_credentials_without_exposing_a_secret_property()
    {
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new PortalRoleDbContext(options);
        var entity = db.Model.FindEntityType(typeof(PortalIntegrationCredentialRecord))!;

        Assert.Equal("IntegrationCredentials", entity.GetTableName());
        Assert.Equal(
            nameof(PortalIntegrationCredentialRecord.CredentialKey),
            entity.FindPrimaryKey()!.Properties.Single().Name);
        Assert.DoesNotContain(
            typeof(IntegrationCredentialDto).GetProperties(),
            property => property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Machine_protector_round_trips_without_storing_plaintext()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string secret = "test-integration-secret";
        var protector = new MachineIntegrationSecretProtector();

        var encrypted = protector.Protect(secret);

        Assert.NotEqual(secret, encrypted);
        Assert.DoesNotContain(secret, encrypted, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(encrypted));
    }

    [Theory]
    [InlineData("Fulcrum Public API", "fulcrum-public-api")]
    [InlineData("  ERP / Inventory Key  ", "erp-inventory-key")]
    public void Credential_names_normalize_to_stable_identifiers(string value, string expected)
    {
        Assert.Equal(expected, IntegrationCredentialNames.NormalizeKey(value));
    }
}
