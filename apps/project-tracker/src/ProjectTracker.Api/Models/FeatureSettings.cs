namespace ProjectTracker.Api.Models;

public sealed class FeatureSettings
{
    public const int SingletonId = 1;
    public const int AssistantNameMaxLength = 40;
    public const string DefaultAssistantName = "Benny";

    public int Id { get; set; } = SingletonId;
    public bool WalkthroughEnabled { get; set; } = true;
    public bool AssistantEnabled { get; set; } = true;
    public string AssistantName { get; set; } = DefaultAssistantName;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
