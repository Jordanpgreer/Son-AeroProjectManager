using System.Net;
using System.Security.Claims;
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

public sealed class QuoteTimingDiagnosticTests
{
    private const string SourceId = "6ae221283339318983fe5f0d";
    private static EstimatingQuoteHistoryRecord Stored() => new()
    {
        Id = 3317, SourceId = SourceId, QuoteNumber = 4705, EstimatingRep = "Jordan",
        EstimatingCompletionDate = new(2026, 11, 17), RfqDueDate = new(2026, 11, 18),
        IsCompleted = true, UpdatedAt = DateTimeOffset.UtcNow
    };
    private static string Quote(object fields, string id = SourceId, int number = 4705) =>
        JsonSerializer.Serialize(new { id, number, status = "sent", customFields = fields, internalNotes = "DO-NOT-RETURN-NOTES" });
    private static Dictionary<string, object?> Dates() => new()
    {
        ["Date to Estimating"] = "10/28/2026", ["Estimating Completion Date"] = "11/17/2026",
        ["RFQ Due Date"] = "11/18/2026", ["Customer Notes"] = "DO-NOT-RETURN-CUSTOMER",
        ["API key"] = "DO-NOT-RETURN-SECRET"
    };

    [Fact]
    public async Task Exactly_two_filtered_fresh_reads_use_existing_credentials_and_actual_mapper_without_raw_values()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Post
            ? "[" + Quote(Dates()) + "]" : Quote(Dates()))));
        using var http = new HttpClient(handler);
        var reader = new CredentialReader();
        var client = Client(http, reader);
        var result = await client.ReadAsync(Stored(), default);

        Assert.Equal(1, reader.Calls);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/quotes/list", handler.Requests[0].Uri.AbsolutePath);
        Assert.Contains("Take=2", handler.Requests[0].Uri.Query);
        Assert.Equal("{\"numbers\":[4705]}", handler.Requests[0].Body);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("/api/quotes/" + SourceId, handler.Requests[1].Uri.AbsolutePath);
        Assert.All(handler.Requests, request => { Assert.True(request.Fresh); Assert.Equal("Bearer TEST-SECRET", request.Authorization); });
        Assert.Equal("Succeeded", result.List.Status);
        Assert.Equal(new DateTime(2026, 10, 28), result.List.ProjectedDates!.DateToEstimating);
        Assert.Equal(result.List.ProjectedDates, result.Detail.ProjectedDates);
        Assert.Equal(3, result.List.RecognizedFields!.Count);
        var output = JsonSerializer.Serialize(new { result.List, result.Detail });
        Assert.DoesNotContain("DO-NOT-RETURN", output);
        Assert.DoesNotContain("TEST-SECRET", output);
        Assert.DoesNotContain(SourceId, output);
    }

    [Fact]
    public void Projection_exposes_configured_precedence_presence_and_retained_dates_without_invalid_values()
    {
        var stored = Stored();
        stored.DateToEstimating = new(2026, 10, 28);
        var fields = Dates();
        fields["Start Custom"] = new { value = "DO-NOT-RETURN-BAD-DATE", extra = "DO-NOT-RETURN-OBJECT" };
        fields["Estimating Completion Date"] = null;
        fields["Estimating Complete Date"] = "2026-11-16";
        var options = new FulcrumQuoteSyncOptions();
        options.CustomFields.DateToEstimating = "Start Custom";
        using var json = JsonDocument.Parse(Quote(fields));
        var result = QuoteTimingDiagnosticProjection.Project(json.RootElement, stored, options);

        Assert.Equal(stored.DateToEstimating, result.ProjectedDates!.DateToEstimating);
        Assert.Null(result.ProjectedDates.EstimatingCompletionDate);
        Assert.True(result.HasMappingWarnings);
        Assert.Contains(result.RecognizedFields!, field => field.Name == "Start Custom" && field.SelectedByMapper && field.Shape == "value/String");
        Assert.Contains(result.RecognizedFields!, field => field.Name == "Date to Estimating" && !field.SelectedByMapper);
        Assert.Contains(result.RecognizedFields!, field => field.Name == "Estimating Completion Date" && field.SelectedByMapper && field.ValueKind == "Null");
        Assert.DoesNotContain("DO-NOT-RETURN", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Absent_source_retains_stored_date_but_explicit_blank_clears_it(bool explicitBlank)
    {
        var stored = Stored(); stored.DateToEstimating = new(2026, 10, 28);
        var fields = new Dictionary<string, object?>();
        if (explicitBlank) fields["Date to Estimating"] = " ";
        using var json = JsonDocument.Parse(Quote(fields));
        var result = QuoteTimingDiagnosticProjection.Project(json.RootElement, stored, new());
        Assert.Equal(explicitBlank ? null : stored.DateToEstimating, result.ProjectedDates!.DateToEstimating);
        if (explicitBlank)
            Assert.Equal("BlankString", Assert.Single(result.RecognizedFields!).Shape);
        else Assert.Empty(result.RecognizedFields!);
    }

    [Theory]
    [InlineData("[]", "NotReturned", false)]
    [InlineData("[{},{}]", "AmbiguousList", true)]
    [InlineData("{}", "InvalidResponse", false)]
    [InlineData("[{\"id\":\"000000000000000000000000\",\"number\":4705}]", "UnexpectedQuote", false)]
    [InlineData("[{\"id\":\"6ae221283339318983fe5f0d\",\"number\":4782}]", "UnexpectedQuote", false)]
    public async Task List_never_selects_missing_ambiguous_or_different_record(string body, string status, bool truncated)
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Post ? body : Quote(Dates()))));
        using var http = new HttpClient(handler);
        var result = await Client(http).ReadAsync(Stored(), default);
        Assert.Equal(status, result.List.Status);
        Assert.Equal(truncated, result.List.PossiblyTruncated);
        Assert.Null(result.List.ProjectedDates);
        Assert.Equal("Succeeded", result.Detail.Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Upstream_denial_is_safe_and_does_not_hide_other_operation(int status)
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("DO-NOT-RETURN-ERROR") }
            : Json(Quote(Dates()))));
        using var http = new HttpClient(handler);
        var result = await Client(http).ReadAsync(Stored(), default);
        Assert.Equal("UpstreamRejected", result.List.Status);
        Assert.Equal(status, result.List.UpstreamStatusCode);
        Assert.Equal("Succeeded", result.Detail.Status);
        Assert.DoesNotContain("DO-NOT-RETURN", JsonSerializer.Serialize(result.List));
    }

    [Fact]
    public async Task Local_cooldown_is_not_reported_as_a_new_upstream_response()
    {
        var retry = DateTimeOffset.UtcNow.AddMinutes(1);
        using var handler = new Handler((_, _) => throw new FulcrumReadDeferredException(FulcrumReadDeferralReason.PermissionRejected, retry));
        using var http = new HttpClient(handler);
        var result = await Client(http).ReadAsync(Stored(), default);
        Assert.Equal("PermissionRejected", result.List.Status);
        Assert.Null(result.List.UpstreamStatusCode);
        Assert.Equal(retry, result.Detail.RetryAt);
    }

    [Fact]
    public async Task Connection_errors_and_timeouts_never_return_exception_text()
    {
        using var handler = new Handler((request, _) => request.Method == HttpMethod.Post
            ? throw new HttpRequestException("DO-NOT-RETURN-SECRET")
            : throw new OperationCanceledException("DO-NOT-RETURN-TIMEOUT"));
        using var http = new HttpClient(handler);
        var result = await Client(http).ReadAsync(Stored(), default);
        Assert.Equal("ConnectionFailed", result.List.Status);
        Assert.Equal("TimedOut", result.Detail.Status);
        Assert.DoesNotContain("DO-NOT-RETURN", JsonSerializer.Serialize(new { result.List, result.Detail }));
    }

    [Fact]
    public async Task Cancellation_stops_before_the_second_read_and_is_not_rewritten_as_failure()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, _) => { cancellation.Cancel(); throw new HttpRequestException("DO-NOT-RETURN-ERROR"); });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).ReadAsync(Stored(), cancellation.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Oversized_body_and_invalid_source_identity_are_bounded_without_a_pagination_followup()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(new string('x', 256 * 1024 + 1))));
        using var http = new HttpClient(handler);
        var client = Client(http);
        var result = await client.ReadAsync(Stored(), default);
        Assert.Equal("ResponseTooLarge", result.List.Status);
        Assert.Equal(2, handler.Requests.Count);
        var stored = Stored(); stored.SourceId = "../../secret";
        result = await client.ReadAsync(stored, default);
        Assert.Equal("InvalidStoredIdentity", result.Detail.Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void Available_names_are_bounded_and_never_include_values()
    {
        var fields = Enumerable.Range(1, 70).ToDictionary(index => new string('n', 110) + index,
            _ => (object?)"DO-NOT-RETURN-SECRET");
        using var source = JsonDocument.Parse(Quote(fields));
        var result = QuoteTimingDiagnosticProjection.Project(source.RootElement, Stored(), new());
        Assert.True(result.FieldNamesTruncated);
        Assert.Equal(48, result.AvailableFieldNames!.Count);
        Assert.All(result.AvailableFieldNames, name => Assert.True(name.Length <= 100));
        Assert.Empty(result.RecognizedFields!);
        Assert.DoesNotContain("DO-NOT-RETURN", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Service_and_endpoint_leave_business_data_unchanged_limit_sync_rows_and_disable_response_caching()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new EstimatingAccessDbContext(new DbContextOptionsBuilder<EstimatingAccessDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.QuoteHistory.Add(Stored());
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) db.FulcrumQuoteSyncRuns.Add(new()
        {
            Id = Guid.NewGuid(), ScheduledForUtc = now.AddMinutes(i), StartedAt = now.AddMinutes(i),
            Status = "Completed", QuotesReceived = i, ErrorMessage = "DO-NOT-RETURN-SQL-ERROR"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using var changes = connection.CreateCommand(); changes.CommandText = "SELECT total_changes()";
        var before = await changes.ExecuteScalarAsync();
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Post
            ? "[" + Quote(Dates()) + "]" : Quote(Dates()))));
        using var http = new HttpClient(handler);
        var service = new QuoteTimingDiagnosticService(db, Client(http), Options.Create(new FulcrumQuoteSyncOptions()));
        var result = await service.ReadAsync(3317, default);
        Assert.NotNull(result);
        Assert.Equal(new[] { 4, 3, 2 }, result.RecentSyncs.Select(run => run.QuotesReceived));
        Assert.Null(result.Stored.Dates.DateToEstimating);
        Assert.Equal(new DateTime(2026, 10, 28), result.List.ProjectedDates!.DateToEstimating);
        Assert.DoesNotContain("DO-NOT-RETURN", JsonSerializer.Serialize(result));
        Assert.Equal(before, await changes.ExecuteScalarAsync());
        Assert.Empty(db.ChangeTracker.Entries());

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(service);
        await using var app = builder.Build();
        app.MapGroup("/api").MapQuoteTimingDiagnosticEndpoints();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.RouteValues["quoteHistoryId"] = "999";
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(before, await changes.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task Endpoint_requires_both_actual_history_and_admin_permissions(bool view, bool admin, bool allowed)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(EstimatingPolicies.ViewHistory, policy => policy.RequireClaim(EstimatingPolicies.PermissionClaim, EstimatingPermissions.ViewHistory));
            options.AddPolicy(EstimatingPolicies.Admin, policy => policy.RequireClaim(EstimatingPolicies.PermissionClaim, EstimatingPermissions.AdministerSettings));
        });
        builder.Services.AddScoped<QuoteTimingDiagnosticService>();
        await using var app = builder.Build();
        app.MapGroup("/api").MapQuoteTimingDiagnosticEndpoints();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>());
        var metadata = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        Assert.Contains(metadata, entry => entry.Policy == EstimatingPolicies.ViewHistory);
        Assert.Contains(metadata, entry => entry.Policy == EstimatingPolicies.Admin);
        var policy = await AuthorizationPolicy.CombineAsync(app.Services.GetRequiredService<IAuthorizationPolicyProvider>(), metadata);
        var claims = new List<Claim>();
        if (view) claims.Add(new(EstimatingPolicies.PermissionClaim, EstimatingPermissions.ViewHistory));
        if (admin) claims.Add(new(EstimatingPolicies.PermissionClaim, EstimatingPermissions.AdministerSettings));
        var result = await app.Services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), null, policy!);
        Assert.Equal(allowed, result.Succeeded);
    }

    private static QuoteTimingDiagnosticClient Client(HttpClient http, CredentialReader? credentials = null) =>
        new(http, Options.Create(new FulcrumQuoteSyncOptions()), credentials ?? new());
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
    private sealed class CredentialReader : IIntegrationCredentialReader
    {
        public int Calls { get; private set; }
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult<string?>("TEST-SECRET"); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new(request.Method, request.RequestUri!, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct),
                request.Options.TryGetValue(FulcrumReadSnapshotHandler.RequireFresh, out var fresh) && fresh,
                request.Headers.Authorization?.ToString()));
            return await response(request, ct);
        }
    }
    private sealed record Request(HttpMethod Method, Uri Uri, string? Body, bool Fresh, string? Authorization);
}
