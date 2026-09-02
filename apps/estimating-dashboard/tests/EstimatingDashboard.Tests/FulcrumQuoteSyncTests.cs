using System.Net;
using System.Text;
using System.Text.Json;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumQuoteSyncTests
{
    [Fact]
    public async Task Acumatica_quote_slot_fails_safely_until_mapping_is_configured()
    {
        IEstimatingQuoteProvider provider = new AcumaticaEstimatingQuoteProvider();

        Assert.Equal(EnterpriseDataRoutes.EstimatingQuotes, provider.RouteName);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.PullAsync(default));

        Assert.Contains("not configured", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("2026-09-01T01:30:00+00:00", "2026-09-01T02:00:00+00:00")]
    [InlineData("2026-09-01T02:00:01+00:00", "2026-09-01T19:00:00+00:00")]
    [InlineData("2026-09-01T19:00:01+00:00", "2026-09-02T02:00:00+00:00")]
    public void Schedule_returns_only_the_next_two_configured_windows(
        string nowValue,
        string expectedValue)
    {
        var actual = FulcrumQuoteSchedule.NextRunUtc(
            DateTimeOffset.Parse(nowValue),
            TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse(expectedValue), actual);
    }

    [Fact]
    public async Task Client_combines_reporting_rows_with_quote_custom_fields_and_uses_bearer_token()
    {
        var handler = new FulcrumHandler();
        var options = Options.Create(new FulcrumQuoteSyncOptions
        {
            Enabled = true,
            PageSize = 5000
        });
        var client = new FulcrumQuoteClient(
            new HttpClient(handler),
            options,
            new StubCredentialReader("secret-token"),
            NullLogger<FulcrumQuoteClient>.Instance);

        var snapshots = await client.GetQuotesAsync(default);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("quote-id", snapshot.Quote.Id);
        Assert.Equal("Acme Aerospace", snapshot.Report?.CustomerName);
        Assert.True(snapshot.Quote.CustomFields?.ContainsKey("Estimating Rep"));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer secret-token", request.Authorization));
        Assert.All(handler.Requests, request => Assert.Equal("api.fulcrumpro.us", request.Host));
        Assert.Contains(handler.Requests, request => request.Path.StartsWith("/api/reporting/quote/list", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Path.StartsWith("/api/quotes/list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Client_requires_the_named_admin_hub_credential_before_any_api_call()
    {
        var handler = new FulcrumHandler();
        var client = new FulcrumQuoteClient(
            new HttpClient(handler),
            Options.Create(new FulcrumQuoteSyncOptions { Enabled = true }),
            new StubCredentialReader(null),
            NullLogger<FulcrumQuoteClient>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetQuotesAsync(default));

        Assert.Contains("Admin Hub", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Credential_reader_decrypts_the_saved_value_and_records_usage()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<EstimatingAccessDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EstimatingAccessDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var protector = new SonAero.Platform.Security.MachineIntegrationSecretProtector();
        db.IntegrationCredentials.Add(new EstimatingIntegrationCredentialRecord
        {
            CredentialKey = SonAero.Platform.Security.IntegrationCredentialNames.FulcrumPublicApi,
            DisplayName = "Fulcrum Public API",
            EncryptedSecret = protector.Protect("saved-token"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "SON4L\\administrator"
        });
        await db.SaveChangesAsync();
        var reader = new IntegrationCredentialReader(db, protector, TimeProvider.System);

        var secret = await reader.GetSecretAsync(
            SonAero.Platform.Security.IntegrationCredentialNames.FulcrumPublicApi,
            default);

        Assert.Equal("saved-token", secret);
        Assert.NotNull((await db.IntegrationCredentials.SingleAsync()).LastUsedAt);
    }

    [Fact]
    public async Task Mapping_and_automated_import_populate_the_current_log_and_are_idempotent()
    {
        var customFields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
            {
              "CustomerContact": "Buyer One",
              "RFQ/REF No": "RFQ-100",
              "RFQ Due Date": "2026-09-10",
              "Date to Estimating": "2026-09-01",
              "Issues?": "None",
              "Quote On Track?": "Yes",
              "Quote Complexity": "B (Medium)",
              "Number of Parts in Quote": 4,
              "Estimating Status": "Quote Assigned",
              "Estimating Rep": "Bethany",
              "Estimating Completion Date": "2026-09-04"
            }
            """);
        var snapshot = new FulcrumQuoteSnapshot(
            new FulcrumQuoteDto(
                "quote-id",
                4395,
                "customer-id",
                "needsApproval",
                1200m,
                customFields,
                null),
            new FulcrumQuoteReportDto(
                "quote-id",
                4395,
                "Acme Aerospace",
                "Sales One",
                "needsApproval",
                1250m));
        var mapping = FulcrumQuoteMapper.Map(
            [snapshot],
            new Dictionary<int, EstimatingQuoteHistoryRecord>(),
            new FulcrumQuoteSyncOptions());
        var row = Assert.Single(mapping.Rows);
        Assert.Equal("Needs Approval", row.QuoteStatus);
        Assert.Equal("Acme Aerospace", row.Customer);
        Assert.Equal("Buyer One", row.CustomerContact);
        Assert.Equal("Bethany", row.EstimatingRep);
        Assert.Equal(4, row.NumberOfParts);
        Assert.Equal(1250m, row.TotalValue);
        Assert.Equal(new DateTime(2026, 9, 10), row.RfqDueDate);
        Assert.Equal(new DateTime(2026, 9, 4), row.EstimatingCompletionDate);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<EstimatingAccessDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EstimatingAccessDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var importer = new EstimatingHistoryImportService(db, new EstimatingHistoryReviewStore());

        var first = await importer.ApplyAutomatedAsync(mapping.Rows, "Fulcrum API test", "FULCRUM_API_SCHEDULE", default);
        var second = await importer.ApplyAutomatedAsync(mapping.Rows, "Fulcrum API test", "FULCRUM_API_SCHEDULE", default);

        Assert.Equal(1, first.NewRecords);
        Assert.Equal(0, first.UpdatedRecords);
        Assert.Equal(0, second.NewRecords);
        Assert.Equal(1, second.UnchangedRecords);
        var record = Assert.Single(await db.QuoteHistory.ToListAsync());
        Assert.Equal("quote-id", record.SourceId);
        Assert.Equal("Bethany", record.EstimatingRep);
        Assert.Equal(2, await db.QuoteHistoryImportBatches.CountAsync());
        Assert.Single(await db.QuoteHistoryAudits.ToListAsync());
    }

    private sealed class FulcrumHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.RequestUri?.Host ?? string.Empty,
                request.Headers.Authorization?.ToString()));
            var path = request.RequestUri?.AbsolutePath;
            var json = path switch
            {
                "/api/reporting/quote/list" => """
                    {
                      "data": [{
                        "id": "quote-id",
                        "number": 4395,
                        "customerName": "Acme Aerospace",
                        "salesPersonName": "Sales One",
                        "status": "needsApproval",
                        "totalInPrimaryCurrency": 1250
                      }],
                      "page": 1,
                      "pageSize": 5000,
                      "totalCount": 1,
                      "totalPages": 1,
                      "hasPreviousPage": false,
                      "hasNextPage": false
                    }
                    """,
                "/api/quotes/list" => """
                    [{
                      "id": "quote-id",
                      "number": 4395,
                      "customerId": "customer-id",
                      "status": "needsApproval",
                      "totalInPrimaryCurrency": 1250,
                      "customFields": { "Estimating Rep": "Bethany" },
                      "externalReferences": null
                    }]
                    """,
                _ => throw new InvalidOperationException($"Unexpected Fulcrum request: {request.RequestUri}")
            };
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubCredentialReader(string? secret) : IIntegrationCredentialReader
    {
        public Task<string?> GetSecretAsync(
            string credentialKey,
            CancellationToken cancellationToken)
        {
            Assert.Equal(SonAero.Platform.Security.IntegrationCredentialNames.FulcrumPublicApi, credentialKey);
            return Task.FromResult(secret);
        }
    }

    private sealed record CapturedRequest(string Path, string Host, string? Authorization);
}
