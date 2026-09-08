using System.Net;
using System.Text;
using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Endpoints;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumQuoteGenerationTests
{
    [Theory]
    [InlineData("https://example.invalid/")]
    [InlineData("https://user@api.fulcrumpro.us/")]
    [InlineData("https://api.fulcrumpro.us:8443/")]
    [InlineData("https://api.fulcrumpro.us/tenant/")]
    [InlineData("https://api.fulcrumpro.us/?redirect=example.invalid")]
    public void Rejects_noncanonical_Fulcrum_API_origins(string configured)
    {
        Assert.Throws<InvalidOperationException>(() =>
            FulcrumApiEndpoint.ResolveItarBaseUri(configured, "FulcrumQuoteSync:BaseUrl"));
    }

    [Fact]
    public void Rewrites_the_legacy_standard_host_to_the_exact_ITAR_origin()
    {
        Assert.Equal(FulcrumApiEndpoint.ItarBaseUrl,
            FulcrumApiEndpoint.ResolveItarBaseUri("https://api.fulcrumpro.com/", "FulcrumQuoteSync:BaseUrl").AbsoluteUri);
    }

    [Fact]
    public void Endpoint_requires_history_and_manage_inputs()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<FulcrumQuoteGenerationService>();
        var app = builder.Build();
        app.MapGroup("/api").MapFulcrumQuoteGenerationEndpoints();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints));
        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        Assert.Contains(policies, x => x.Policy == EstimatingPolicies.ViewHistory);
        Assert.Contains(policies, x => x.Policy == EstimatingPolicies.ManageInputs);
        Assert.Contains(policies, x => x.Policy == EstimatingPolicies.Editor);
    }

    [Fact]
    public void Generation_requires_the_non_simple_Arda_request_header()
    {
        var context = new DefaultHttpContext();
        Assert.False(FulcrumQuoteGenerationEndpoints.HasArdaRequestHeader(context.Request));
        context.Request.Headers["X-Arda-Request"] = "quote-generation";
        Assert.True(FulcrumQuoteGenerationEndpoints.HasArdaRequestHeader(context.Request));
        context.Request.Headers["X-Arda-Request"] = "other";
        Assert.False(FulcrumQuoteGenerationEndpoints.HasArdaRequestHeader(context.Request));
    }

    [Fact]
    public async Task Generates_each_line_with_available_stock_recursive_usage_times_and_unmapped_titles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Service.GenerateAsync(1, Editor, default);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(new decimal[] { 32, 50, 75 }, result.Items[0].Quantities);
        var top = result.Items[0].Item;
        Assert.Equal(32, top.AvailableStock);
        var child = Assert.Single(top.Subassemblies);
        Assert.Equal(4, child.QuantityPerParent);
        Assert.Equal(0, child.AvailableStock); // Available endpoint omits zero-stock IDs.
        var operation = Assert.Single(top.Operations);
        Assert.Equal("Unmapped lathe", operation.TargetOperation);
        Assert.Equal(65, operation.SetupMinutes);
        Assert.Equal(0.5m, operation.RunMinutes);
        Assert.Null(operation.RateReferenceKey);
        Assert.Equal(0.25m, Assert.Single(child.Materials).QuantityPerParent);
        Assert.Equal(5m, Assert.Single(child.Materials).UnitCost);
        Assert.Equal(20, Assert.Single(top.Processes).LotCost);
        Assert.All(fixture.Handler.Requests, x => Assert.Equal("Bearer test-key", x.Auth));
        Assert.DoesNotContain(fixture.Handler.Requests, x => x.Path.Contains("byItem", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, x => x.Contains("pricing breaks", StringComparison.Ordinal));
        Assert.Equal(0, (await fixture.Db.QuoteHistory.SingleAsync()).Version);
    }

    [Theory]
    [InlineData("Other User", "Editor", false)]
    [InlineData("Casey Lee", "Viewer", false)]
    [InlineData("Casey Lee", "Editor", true)]
    public async Task Denies_unassigned_viewer_and_completed_before_api(string name, string role, bool completed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var record = await fixture.Db.QuoteHistory.SingleAsync();
        record.IsCompleted = completed;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<EstimatingQuoteWorkflowForbiddenException>(() => fixture.Service.GenerateAsync(1,
            Editor with { DisplayName = name, AccountName = name, Role = role }, default));
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Rejects_fresh_reassignment_before_reading_parts()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Assignee = "Other User";
        await Assert.ThrowsAsync<EstimatingQuoteWorkflowForbiddenException>(() => fixture.Service.GenerateAsync(1, Editor, default));
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Cyclic_bom_fails_without_partial_estimate()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Cycle = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync(1, Editor, default));
        Assert.Contains("circular", error.Message);
    }

    [Fact]
    public async Task Normalized_estimator_alias_is_accepted_and_bad_inventory_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Alias = true;
        Assert.Equal(2, (await fixture.Service.GenerateAsync(1, Editor, default)).Items.Count);
        fixture.Handler.BadInventory = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync(1, Editor, default));
        Assert.Contains("invalid available inventory", error.Message);
    }

    [Fact]
    public async Task Fresh_closed_quote_is_rejected_before_parts_are_pulled()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Status = "won";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync(1, Editor, default));
        Assert.Contains("no longer active", error.Message);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Active_controlled_rules_map_titles_and_deactivated_rules_preserve_source()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = new EstimatingRateReferenceRecord
        {
            Key = "lathe",
            SourceRow = 10,
            Category = "Labor",
            OperationName = "Lathe controlled",
            IsActive = true
        };
        var rule = new EstimatingOperationMappingRecord
        {
            FulcrumOperation = "Unmapped lathe",
            FulcrumOperationKey = "UNMAPPED LATHE",
            RateReferenceKey = "lathe",
            RateReference = reference,
            IsActive = true
        };
        fixture.Db.EstimatingRateReferences.Add(reference);
        fixture.Db.EstimatingOperationMappings.Add(rule);
        await fixture.Db.SaveChangesAsync();
        var mapped = Assert.Single((await fixture.Service.GenerateAsync(1, Editor, default)).Items[0].Item.Operations);
        Assert.Equal("Lathe controlled", mapped.TargetOperation);
        Assert.Equal("lathe", mapped.RateReferenceKey);
        rule.IsActive = false;
        await fixture.Db.SaveChangesAsync();
        var unmapped = Assert.Single((await fixture.Service.GenerateAsync(1, Editor, default)).Items[0].Item.Operations);
        Assert.Equal("Unmapped lathe", unmapped.TargetOperation);
        Assert.Null(unmapped.RateReferenceKey);
    }

    [Fact]
    public async Task Upstream_failure_never_returns_a_partial_estimate_or_error_response_body()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.FailChild = true;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.GenerateAsync(1, Editor, default));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.DoesNotContain("private upstream body", error.Message);
    }

    [Fact]
    public async Task Missing_admin_credential_fails_before_network()
    {
        await using var fixture = await Fixture.CreateAsync(null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync(1, Editor, default));
        Assert.Contains("Admin Hub", error.Message);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Rejects_oversized_nonpaged_quote_line_lists()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.TooManyQuoteLines = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync(1, Editor, default));
        Assert.Contains("safe generation limit", error.Message);
    }

    [Fact]
    public async Task Rejects_response_bodies_larger_than_the_generation_limit()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.OversizedBody = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync(1, Editor, default));
        Assert.Contains("safe generation size", error.Message);
    }

    [Theory]
    [InlineData("fixedHours", 2, 120, 0)]
    [InlineData("fixedSeconds", 90, 1.5, 0)]
    [InlineData("minutesPerUnit", 3, 0, 3)]
    [InlineData("unitsPerHour", 120, 0, 0.5)]
    public void Converts_documented_time_units(string option, decimal amount, decimal fixedTime, decimal perUnit)
    {
        var result = FulcrumQuoteGenerationService.Time(JsonSerializer.SerializeToElement(new { time = amount, option }));
        Assert.Equal((fixedTime, perUnit), result);
    }

    private static readonly EstimatingAccessProfile Editor = new(1, "DOMAIN\\casey.lee", "Casey Lee", "Editor", true);
    private sealed class Fixture(SqliteConnection connection, EstimatingAccessDbContext db,
        FulcrumQuoteGenerationService service, Handler handler) : IAsyncDisposable
    {
        public EstimatingAccessDbContext Db => db;
        public FulcrumQuoteGenerationService Service => service;
        public Handler Handler => handler;
        public static async Task<Fixture> CreateAsync(string? credential = "Bearer test-key")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new EstimatingAccessDbContext(new DbContextOptionsBuilder<EstimatingAccessDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            db.QuoteHistory.Add(new EstimatingQuoteHistoryRecord
            {
                Id = 1,
                SourceId = "quote",
                QuoteNumber = 4460,
                Customer = "Synthetic customer",
                EstimatingRep = "Casey Lee"
            });
            await db.SaveChangesAsync();
            var handler = new Handler();
            var options = Options.Create(new FulcrumQuoteSyncOptions());
            var client = new FulcrumQuoteGenerationClient(new HttpClient(handler), options, new Credentials(credential));
            return new(connection, db, new(db, client, new(db), options, TimeProvider.System), handler);
        }
        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }
    }
    private sealed class Credentials(string? token) : IIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(token);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<(string Path, string? Auth)> Requests { get; } = [];
        public string Assignee { get; set; } = "Casey Lee";
        public bool Cycle { get; set; }
        public bool Alias { get; set; }
        public bool BadInventory { get; set; }
        public bool FailChild { get; set; }
        public bool TooManyQuoteLines { get; set; }
        public bool OversizedBody { get; set; }
        public string Status { get; set; } = "needsApproval";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((path, request.Headers.Authorization?.ToString()));
            if (OversizedBody && path == "/api/quotes/quote")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(new string('x', 16 * 1024 * 1024 + 1)) });
            if (FailChild && path == "/api/items/child") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("private upstream body") });
            var body = path switch
            {
                "/api/quotes/quote" => JsonSerializer.Serialize(new { number = 4460, status = Status, customFields = new Dictionary<string, object> { [Alias ? "ESTIMATOR" : "Estimating Rep"] = Alias ? new { displayValue = Assignee } : Assignee } }),
                "/api/quotes/quote/part-line-items/list" when TooManyQuoteLines => "[" + string.Join(',', Enumerable.Range(1, 100001).Select(index => $"{{\"id\":\"line{index}\"}}")) + "]",
                "/api/quotes/quote/part-line-items/list" => """[{"id":"line1","number":1,"itemId":"top","quantity":32,"priceBreaks":[{"quantity":50},{"quantity":75}]},{"id":"line2","number":2,"itemId":"second","quantity":4}]""",
                "/api/inventory/availableByItem" => BadInventory ? """{"top":"unknown"}""" : """{"top":32,"second":2}""",
                "/api/items/top" => """{"id":"top","number":"TOP","itemOrigin":"make","revision":{"revision":"C"}}""",
                "/api/items/child" => """{"id":"child","number":"CHILD","itemOrigin":"make"}""",
                "/api/items/second" => """{"id":"second","number":"SECOND","itemOrigin":"make"}""",
                "/api/items/buy" => """{"id":"buy","number":"BUY","itemOrigin":"buy","unitOfMeasureName":"Piece","vendorDetails":[{"isPrimary":true,"price":10,"unitQuantity":1,"inventoryUnitQuantity":2}]}""",
                "/api/items/top/routing/input-items/list" => """[{"id":"component1","itemId":"child","valueType":"requires","valueTypeUnits":4}]""",
                "/api/items/child/routing/input-items/list" => Cycle ? """[{"id":"cycle","itemId":"top","valueType":"requires","valueTypeUnits":1}]""" : """[{"id":"component2","itemId":"buy","valueType":"creates","valueTypeUnits":4}]""",
                "/api/items/top/routing/operations/list" => """[{"id":"op1","order":1,"name":"Unmapped lathe","operation":{"setupTime":{"time":1,"option":"fixedHours"},"laborTime":{"time":5,"option":"fixedMinutes"},"machineTime":{"time":2,"option":"minutesPerUnit"}}},{"id":"op2","order":2,"name":"Outside coating","isOutsideProcessing":true,"outsideProcessingOperation":{"outsideProcessingCost":{"costOption":"fixed","fixedCost":20}}}]""",
                _ when path.EndsWith("/list", StringComparison.Ordinal) => "[]",
                _ => throw new InvalidOperationException("Unexpected mock URL: " + path)
            };
            // Separate time basis verifies fixed labor remains fixed. Add a per-unit setup component for this fixture.
            if (path == "/api/items/top/routing/operations/list") body = body.Replace("\"time\":1,\"option\":\"fixedHours\"", "\"time\":65,\"option\":\"fixedMinutes\"")
                .Replace("\"time\":5,\"option\":\"fixedMinutes\"", "\"time\":120,\"option\":\"unitsPerHour\"");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
