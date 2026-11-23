namespace EstimatingDashboard.Api.Services;

public sealed class QuoteMailImportOptions
{
    public const string SectionName = "QuoteMailImport";
    public string[] InternalDomains { get; set; } = [];
}
