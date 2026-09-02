using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Portal.Api.Data;
using Portal.Api.Endpoints;
using Portal.Api.Services;
using SonAero.Platform.Integrations;
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
                "/api/admin/integration-") == true)
            .ToList();

        Assert.Equal(5, routes.Count);
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
        Assert.Equal(
            "IntegrationCredentialTests",
            db.Model.FindEntityType(typeof(PortalIntegrationCredentialTestRecord))!.GetTableName());
        Assert.Equal(
            "EnterpriseIntegrationSettings",
            db.Model.FindEntityType(typeof(PortalEnterpriseIntegrationSettingRecord))!.GetTableName());
        Assert.Equal(
            "EnterpriseIntegrationSettingAudits",
            db.Model.FindEntityType(typeof(PortalEnterpriseIntegrationSettingAuditRecord))!.GetTableName());
    }

    [Fact]
    public async Task Integration_schema_defaults_to_fulcrum_and_can_be_read_by_modules()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PortalRoleDbContext(options);
        var initializer = new PortalIntegrationCredentialSchemaInitializer(db);

        await initializer.InitializeAsync();

        Assert.Equal(
            EnterpriseProviderNames.Fulcrum,
            await EnterpriseIntegrationStore.ReadActiveProviderAsync(connection, default));
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

    [Fact]
    public async Task Fulcrum_connection_test_uses_the_saved_token_for_one_read_only_request()
    {
        var handler = new CapturingHandler(System.Net.HttpStatusCode.OK);
        var tester = new FulcrumCredentialTester(
            new HttpClient(handler),
            Options.Create(new IntegrationCredentialTestOptions()));

        var result = await tester.TestAsync("saved-token", default);

        Assert.True(result.Succeeded);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal("Bearer saved-token", handler.Authorization);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.StartsWith(
            "https://api.fulcrumpro.us/api/reporting/quote/list?Skip=0&Take=1",
            handler.RequestUri?.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Fulcrum_legacy_public_host_is_upgraded_to_the_itar_endpoint()
    {
        var actual = FulcrumApiEndpoint.ResolveItarBaseUri(
            "https://api.fulcrumpro.com/",
            "Test:FulcrumBaseUrl");

        Assert.Equal("https://api.fulcrumpro.us/", actual.ToString());
    }

    [Fact]
    public async Task Fulcrum_connection_test_explains_missing_quote_permission()
    {
        var tester = new FulcrumCredentialTester(
            new HttpClient(new CapturingHandler(System.Net.HttpStatusCode.Forbidden)),
            Options.Create(new IntegrationCredentialTestOptions()));

        var result = await tester.TestAsync("saved-token", default);

        Assert.False(result.Succeeded);
        Assert.Equal(403, result.HttpStatusCode);
        Assert.Contains("permission to view quotes", result.Message);
    }

    private sealed class CapturingHandler(System.Net.HttpStatusCode statusCode) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            Method = request.Method;
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
