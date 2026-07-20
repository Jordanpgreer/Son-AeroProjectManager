namespace Portal.Api.Models;

public enum ApplicationStatus
{
    Active,
    ComingSoon,
    Maintenance
}

/// <summary>
/// A single entry in the portal's application catalog. The catalog is data-driven and bound
/// from configuration ("Portal:Applications") so new applications can be added without code
/// changes to the portal frontend.
/// </summary>
public sealed class ApplicationEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "General";

    /// <summary>Icon key resolved to a lucide icon by the frontend (falls back to a default).</summary>
    public string Icon { get; set; } = "app-window";

    /// <summary>Absolute URL the "Open" button navigates to. Empty for coming-soon entries.</summary>
    public string Url { get; set; } = string.Empty;

    public int Order { get; set; } = 100;

    public ApplicationStatus Status { get; set; } = ApplicationStatus.Active;

    /// <summary>Roles allowed to see this card. Empty means visible to everyone.</summary>
    public List<string> AllowedRoles { get; set; } = new();

    /// <summary>
    /// Optional read-only API path (relative to <see cref="Url"/>) the portal can fetch to
    /// render a live "minimized dashboard" preview on the card. Null disables the preview.
    /// </summary>
    public string? PreviewPath { get; set; }
}
