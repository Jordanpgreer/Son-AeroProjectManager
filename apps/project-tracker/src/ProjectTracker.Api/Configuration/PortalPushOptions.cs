namespace ProjectTracker.Api.Configuration;

public sealed class PortalPushOptions
{
    public const string SectionName = "PortalPush";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string ProducerKey { get; set; } = string.Empty;
}
