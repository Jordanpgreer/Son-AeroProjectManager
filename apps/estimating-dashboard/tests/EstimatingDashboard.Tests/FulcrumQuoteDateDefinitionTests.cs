using System.Net;
using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumQuoteDateDefinitionTests
{
    private static FulcrumQuoteDto Quote(Dictionary<string, object?>? extra = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["DateSentToCustomer"] = "2026-10-28", ["EstimatingCompletionDate"] = "2026-11-17",
            ["RfqDueDate"] = "2026-11-18", ["EstimatingRep"] = "Jordan"
        };
        foreach (var pair in extra ?? []) values[pair.Key] = pair.Value;
        return new("6ae221283339318983fe5f0d", 4705, "customer", "Sent", 1200,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values)), null);
    }
    private static FulcrumQuoteDateDefinitions Definition() => new("Succeeded", [new("Date to Estimating", "DateSentToCustomer")]);
    private static FulcrumQuoteMappingResult Map(FulcrumQuoteDto quote, FulcrumQuoteDateDefinitions? definitions,
        EstimatingQuoteHistoryRecord? existing = null) => FulcrumQuoteMapper.Map([new(quote, null, definitions)],
            existing is null ? new Dictionary<int, EstimatingQuoteHistoryRecord>() : new Dictionary<int, EstimatingQuoteHistoryRecord> { [4705] = existing }, new());

    [Fact]
    public async Task Definition_proven_label_persists_example_start_and_fifteen_workdays()
    {
        var mapping = Map(Quote(), Definition());
        var row = Assert.Single(mapping.Rows);
        Assert.Equal(new DateTime(2026, 10, 28), row.DateToEstimating);
        Assert.Equal(15, row.Workdays);
        Assert.Empty(mapping.Warnings);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new EstimatingAccessDbContext(new DbContextOptionsBuilder<EstimatingAccessDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var importer = new EstimatingHistoryImportService(db, new());
        await importer.ApplyAutomatedAsync(mapping.Rows, "definition regression", "TEST", default);
        var stored = await db.QuoteHistory.AsNoTracking().SingleAsync();
        Assert.Equal(row.DateToEstimating, stored.DateToEstimating);
        Assert.Equal(15, stored.Workdays);
        Assert.True(stored.IsOnTime);
        var statistics = await new EstimatingQuoteWorkflowService(db, new FixedClock()).GetStatsAsync(
            new EstimatingAccessProfile(1, "SON4L\\Jordan.Greer", "Jordan", EstimatingRoles.Editor, true), default);
        Assert.Equal(15m, statistics.AverageCompletionWorkdays);
        Assert.Equal(1, statistics.CompletedCount);
        Assert.Equal(1, statistics.CompletionSampleSize);
        var source = Quote();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { source.Id, source.Number, source.Status, source.CustomFields }));
        var projection = QuoteTimingDiagnosticProjection.Project(document.RootElement, stored, new(), definitions: Definition());
        Assert.Equal(row.DateToEstimating, projection.ProjectedDates!.DateToEstimating);
        Assert.Contains(projection.RecognizedFields!, field => field.Purpose == "assignment"
            && field.Name == "DateSentToCustomer" && field.SelectedByMapper);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("ambiguous")]
    [InlineData("failed")]
    [InlineData("unrelated")]
    public void No_semantic_alias_is_inferred_without_unique_matching_definition(string scenario)
    {
        FulcrumQuoteDateDefinitions? definitions = scenario switch
        {
            "ambiguous" => new("Succeeded", [new("Date to Estimating", "DateSentToCustomer"), new("Date to Estimating", "OtherDate")]),
            "failed" => new("UpstreamRejected", Definition().Fields, 403),
            "unrelated" => new("Succeeded", [new("Date sent to customer", "DateSentToCustomer")]),
            _ => null
        };
        Assert.Null(Assert.Single(Map(Quote(), definitions).Rows).DateToEstimating);
        var previous = new EstimatingQuoteHistoryRecord { DateToEstimating = new(2026, 10, 1) };
        Assert.Equal(previous.DateToEstimating, Assert.Single(Map(Quote(), definitions, previous).Rows).DateToEstimating);
    }

    [Fact]
    public void Ambiguous_optional_metadata_does_not_suppress_existing_documented_direct_alias()
    {
        var quote = Quote();
        quote.CustomFields!.Remove("EstimatingCompletionDate");
        quote.CustomFields["Estimating Complete Date"] = JsonSerializer.SerializeToElement("2026-11-17");
        var definitions = new FulcrumQuoteDateDefinitions("Succeeded",
            [new("Estimating Completion Date", "FirstKey"), new("Estimating Completion Date", "SecondKey")]);
        Assert.Equal(new DateTime(2026, 11, 17), Assert.Single(Map(quote, definitions).Rows).EstimatingCompletionDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad date")]
    [InlineData("2026-10-20")]
    public void Explicit_primary_field_wins_over_definition_and_preserves_original_invalid_date_behavior(string? value)
    {
        var previous = new EstimatingQuoteHistoryRecord { DateToEstimating = new(2026, 10, 1) };
        var result = Map(Quote(new() { ["DateToEstimating"] = value }), Definition(), previous);
        var expected = value switch { "bad date" => previous.DateToEstimating, "2026-10-20" => new DateTime(2026, 10, 20), _ => (DateTime?)null };
        Assert.Equal(expected, Assert.Single(result.Rows).DateToEstimating);
    }

    [Fact]
    public async Task Reader_filters_quote_date_definitions_and_uses_normal_cache_for_sync()
    {
        using var handler = new Handler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("Take=100", request.RequestUri!.Query);
            Assert.Equal("{\"module\":\"quote\",\"type\":\"date\"}", await request.Content!.ReadAsStringAsync());
            Assert.True(FulcrumReadSnapshotHandler.IsSupportedRead(request));
            Assert.True(request.Options.TryGetValue(FulcrumReadSnapshotHandler.RequireFresh, out var fresh));
            Assert.False(fresh);
            return Json("[" + Row(1) + "]");
        });
        using var http = new HttpClient(handler);
        var result = await Read(http);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal("DateSentToCustomer", Assert.Single(result.Fields).Label);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Reader_pages_with_fixed_bound_and_discards_incomplete_metadata()
    {
        using var handler = new Handler((request, _) =>
        {
            var skip = int.Parse(request.RequestUri!.Query.Split('&')[0].Split('=')[1]);
            return Task.FromResult(Json("[" + string.Join(',', Enumerable.Range(skip, 100).Select(index => Row(index + 1))) + "]"));
        });
        using var http = new HttpClient(handler);
        var result = await Read(http);
        Assert.Equal("Truncated", result.Status);
        Assert.Empty(result.Fields);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task Metadata_denial_preserves_direct_mapping_and_never_returns_provider_text()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent("SECRET-BODY") }));
        using var http = new HttpClient(handler);
        var result = await Read(http);
        Assert.Equal(403, result.UpstreamStatusCode);
        Assert.DoesNotContain("SECRET-BODY", JsonSerializer.Serialize(result));
        var mapping = Map(Quote(new() { ["Date to Estimating"] = "2026-10-28" }), result);
        Assert.Equal(15, Assert.Single(mapping.Rows).Workdays);
        Assert.Single(mapping.Warnings);
    }

    [Fact]
    public async Task Metadata_cancellation_is_preserved()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, _) => { cancellation.Cancel(); throw new HttpRequestException("SECRET"); });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(http, cancellation.Token));
    }

    private static string Row(int id) => JsonSerializer.Serialize(new { id = id.ToString("x24"), name = "Date to Estimating", label = "DateSentToCustomer", module = "quote", type = "date", helpText = "SECRET-BODY" });
    private static Task<FulcrumQuoteDateDefinitions> Read(HttpClient http, CancellationToken ct = default) =>
        FulcrumQuoteDateDefinitionReader.ReadAsync(http, new(FulcrumApiEndpoint.ItarBaseUrl), "test", false, ct);
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return response(request, ct); }
    }
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    }
}
