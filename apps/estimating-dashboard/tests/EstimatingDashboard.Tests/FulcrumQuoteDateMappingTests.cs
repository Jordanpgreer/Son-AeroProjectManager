using System.Globalization;
using System.Text.Json;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumQuoteDateMappingTests
{
    [Theory]
    [InlineData("""{"Estimating Complete Date":"2026-09-04"}""", "2026-09-04")]
    [InlineData("""{"Estimate Completion Date":"2026-09-03"}""", "2026-09-03")]
    [InlineData("""{"Estimating Complete Date":"2026-09-04","Estimating Completion Date":"2026-09-02"}""", "2026-09-02")]
    [InlineData("""{"Estimating Complete Date":"2026-09-04","Estimating Completion Date":null}""", null)]
    public void Source_completion_date_supports_aliases_and_honors_the_configured_field_first(
        string customFieldsJson, string? expectedDate)
    {
        var customFields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(customFieldsJson);
        var snapshot = new FulcrumQuoteSnapshot(
            new FulcrumQuoteDto("quote-id", 4395, "customer-id", "won", 1200m, customFields, null), null);
        var existing = new Dictionary<int, EstimatingQuoteHistoryRecord>
        {
            [4395] = new() { EstimatingCompletionDate = new DateTime(2026, 9, 1) }
        };

        var mapping = FulcrumQuoteMapper.Map([snapshot], existing, new FulcrumQuoteSyncOptions());

        var date = expectedDate is null ? (DateTime?)null
            : DateTime.ParseExact(expectedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(date, Assert.Single(mapping.Rows).EstimatingCompletionDate);
        Assert.Empty(mapping.Warnings);
    }
}
