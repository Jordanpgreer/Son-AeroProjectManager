namespace Portal.Api.Services;

public sealed class EngineeringStorageAdminOptions
{
    public const string SectionName = "DrawingStorage";
    public string RootPath { get; set; } = string.Empty;
    public bool RequireUncPath { get; set; } = true;
}
