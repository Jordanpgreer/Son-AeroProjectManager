using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Endpoints;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EstimatingDashboard.Tests;

public sealed class EstimatingQuoteDashboardEndpointTests
{
    [Theory]
    [InlineData("dashboard", "?scope=mine&from=2026-11-01&to=2026-11-30&search=%232100&pageSize=1", true, 200)]
    [InlineData("dashboard", "?scope=all", false, 403)]
    [InlineData("dashboard", "?scope=invalid", true, 400)]
    [InlineData("dashboard", "?from=2026-11-30&to=2026-11-01", true, 400)]
    [InlineData("dashboard", "?from=11/01/2026", true, 400)]
    [InlineData("dashboard", "?scope=mine&pageSize=1", false, 200)]
    [InlineData("dashboard", "?scope=all&estimator=Casey%20lee&pageSize=1", true, 200)]
    [InlineData("dashboard", "?scope=all&estimator=CASEY%20LEE", false, 403)]
    [InlineData("dashboard", "?scope=mine&estimator=CASEY%20LEE", false, 403)]
    [InlineData("dashboard", "?scope=invalid&estimator=", false, 403)]
    [InlineData("dashboard", "?scope=mine&estimator=CASEY%20LEE", true, 400)]
    [InlineData("dashboard", "?scope=all&estimator=unknown", true, 400)]
    [InlineData("dashboard", "?scope=all&estimator=", true, 400)]
    [InlineData("performance", "", false, 200)]
    [InlineData("performance", "?scope=all", false, 403)]
    [InlineData("performance", "?scope=all&estimator=CASEY%20LEE", false, 403)]
    [InlineData("performance", "?scope=mine&estimator=CASEY%20LEE", true, 400)]
    [InlineData("performance", "?scope=all&estimator=CASEY%20LEE", true, 200)]
    [InlineData("performance", "?scope=all&estimator=unknown", true, 400)]
    [InlineData("performance", "?scope=mine&month=2026-11", false, 200)]
    [InlineData("performance", "?scope=all&estimator=CASEY%20LEE&month=2026-11", true, 200)]
    [InlineData("performance", "?scope=all&month=2026-11", false, 403)]
    [InlineData("performance", "?month=2026-1", true, 400)]
    [InlineData("performance", "?month=2026-13", true, 400)]
    [InlineData("performance", "?month=2026-11-01", true, 400)]
    [InlineData("performance", "?month=", true, 400)]
    public async Task Database_only_dashboard_endpoints_bind_query_return_safe_errors_and_never_write(
        string routeName, string query, bool historyPermission, int expectedStatus)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new EstimatingAccessDbContext(new DbContextOptionsBuilder<EstimatingAccessDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.QuoteHistory.Add(new EstimatingQuoteHistoryRecord
        {
            SourceId = "fixture", QuoteNumber = 2100, Customer = "Aero Fixture", EstimatingRep = "Casey Lee",
            RfqDueDate = new(2026, 11, 15), FulcrumQuoteItemsJson = """[{"PartNumber":"PN-TEST"}]""",
            FirstImportedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var permissions = new List<string> { EstimatingPermissions.QuotesView };
        if (historyPermission) permissions.Add(EstimatingPermissions.ViewHistory);
        var access = new EstimatingAccessProfile(1, "SONAERO\\casey", "Casey Lee", EstimatingRoles.Viewer,
            true, GrantedPermissions: permissions);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(new EstimatingQuoteWorkflowService(db, TimeProvider.System));
        builder.Services.AddScoped<EnterpriseQuoteSyncService>();
        // No Fulcrum client, credential reader, or integration coordinator is registered.
        await using var app = builder.Build();
        app.MapGroup("/api").MapEstimatingQuoteWorkflowEndpoints();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(route => route.RoutePattern.RawText == $"/api/quote-workflow/{routeName}");
        Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == EstimatingPolicies.QuotesView);
        Assert.Equal(new[] { "GET" }, endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Items[EstimatingPolicies.AccessItem] = access;
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        if (expectedStatus == 200 && routeName == "dashboard")
        {
            Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("pageSize").GetInt32());
            var row = Assert.Single(json.RootElement.GetProperty("records").EnumerateArray());
            Assert.Equal(2100, row.GetProperty("quoteNumber").GetInt32());
            Assert.Equal("PN-TEST", row.GetProperty("partNumbers")[0].GetString());
            Assert.False(row.GetProperty("canOpenQuoteStatus").GetBoolean());
            Assert.False(row.GetProperty("canGenerateEstimate").GetBoolean());
            Assert.True(json.RootElement.GetProperty("stats").TryGetProperty("onTimeMissingCount", out _));
        }
        if (expectedStatus == 200 && routeName == "performance")
        {
            Assert.False(json.RootElement.TryGetProperty("records", out _));
            Assert.Equal(1, json.RootElement.GetProperty("stats").GetProperty("activeCount").GetInt32());
        }
        if (expectedStatus == 200)
        {
            var estimators = json.RootElement.GetProperty("estimators").EnumerateArray();
            if (historyPermission)
            {
                var estimator = Assert.Single(estimators);
                Assert.Equal("CASEY LEE", estimator.GetProperty("id").GetString());
                Assert.Equal("Casey Lee", estimator.GetProperty("name").GetString());
            }
            else
                Assert.Empty(estimators);
        }
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(1, await db.QuoteHistory.CountAsync());
        Assert.Empty(await db.QuoteHistoryAudits.ToListAsync());
    }
}
