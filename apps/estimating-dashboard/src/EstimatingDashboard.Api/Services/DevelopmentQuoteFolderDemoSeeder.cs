using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstimatingDashboard.Api.Services;

public sealed class DevelopmentQuoteFolderDemoOptions
{
    public const string SectionName = "DevelopmentQuoteFolderDemo";

    public bool Enabled { get; set; }
    public int QuoteNumber { get; set; } = 990001;
    public string SourceId { get; set; } = "development-demo-quote-folder";
    public string Estimator { get; set; } = "ProjectTrackerAdmin";
    public string FolderPath { get; set; } = @"S:\Estimating\Development Demo\Quote 990001";
}

internal sealed class DevelopmentQuoteFolderDemoSeeder(
    EstimatingAccessDbContext db,
    IOptions<DevelopmentQuoteFolderDemoOptions> options,
    TimeProvider clock,
    ILogger<DevelopmentQuoteFolderDemoSeeder> logger)
{
    internal const string DemoCustomer = "[DEVELOPMENT DEMO] Quote folder link";
    internal const string SeedActor = "Development demo seed";

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled) return;

        var sourceId = settings.SourceId.Trim();
        var estimator = settings.Estimator.Trim();
        var folderPath = FulcrumQuoteFolderPath.Normalize(settings.FolderPath);
        if (sourceId.Length == 0 || estimator.Length == 0 || folderPath is null || settings.QuoteNumber <= 0)
            throw new InvalidOperationException(
                $"{DevelopmentQuoteFolderDemoOptions.SectionName} contains an invalid demo quote setting.");

        var record = await db.QuoteHistory.SingleOrDefaultAsync(
            quote => quote.SourceId == sourceId,
            cancellationToken);
        if (record is null)
        {
            if (await db.QuoteHistory.AnyAsync(
                    quote => quote.QuoteNumber == settings.QuoteNumber,
                    cancellationToken))
            {
                logger.LogWarning(
                    "Skipped the development quote-folder demo because quote number {QuoteNumber} already exists.",
                    settings.QuoteNumber);
                return;
            }

            var now = clock.GetUtcNow();
            record = new EstimatingQuoteHistoryRecord
            {
                SourceId = sourceId,
                QuoteNumber = settings.QuoteNumber,
                Customer = DemoCustomer,
                SalesPerson = "Development Demo",
                QuoteStatus = "Draft",
                EstimatingRep = estimator,
                QuoteFolderPath = folderPath,
                RfqDueDate = new DateTime(2099, 12, 31),
                NumberOfParts = 1,
                ArdaStatus = EstimatingArdaStatuses.Untouched,
                ArdaStatusChangedAt = now,
                OnTimeStatus = EstimatingOnTimeStatuses.NoData,
                IsCompleted = false,
                LastImportBatchId = Guid.Empty,
                FirstImportedAt = now,
                UpdatedAt = now,
                UpdatedBy = SeedActor
            };
            db.QuoteHistory.Add(record);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Seeded development demo quote {QuoteNumber} for quote-folder link testing.",
                record.QuoteNumber);
            return;
        }

        var changed = false;
        changed |= Set(record.Customer, DemoCustomer, value => record.Customer = value);
        changed |= Set(record.SalesPerson, "Development Demo", value => record.SalesPerson = value);
        changed |= Set(record.QuoteStatus, "Draft", value => record.QuoteStatus = value);
        changed |= Set(record.EstimatingRep, estimator, value => record.EstimatingRep = value);
        changed |= Set(record.QuoteFolderPath, folderPath, value => record.QuoteFolderPath = value);
        if (record.IsCompleted)
        {
            record.IsCompleted = false;
            record.EstimatingCompletionDate = null;
            changed = true;
        }
        if (!changed) return;

        record.UpdatedAt = clock.GetUtcNow();
        record.UpdatedBy = SeedActor;
        record.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool Set(string? current, string value, Action<string> assign)
    {
        if (string.Equals(current, value, StringComparison.Ordinal)) return false;
        assign(value);
        return true;
    }
}
