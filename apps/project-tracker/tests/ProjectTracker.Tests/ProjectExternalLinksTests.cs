using ProjectTracker.Api.Services;
using ProjectTracker.Api.Mapping;
using ProjectTracker.Api.Models;
using System.Text.Json;

namespace ProjectTracker.Tests;

public sealed class ProjectExternalLinksTests
{
    [Fact]
    public void EditPermission_IsRequiredOnlyWhenAStoredUrlChanges()
    {
        var project = new Project
        {
            SalesOrderUrl = "https://fulcrum.son4l.local/orders/123",
            JobUrl = null
        };

        Assert.Null(ProjectExternalLinks.FindDeniedEditPermission(
            project,
            project.SalesOrderUrl,
            project.JobUrl,
            _ => false));
        Assert.Equal(
            ProjectTracker.Api.Auth.ProjectTrackerPermissions.ProjectEditExternalLinks,
            ProjectExternalLinks.FindDeniedEditPermission(
                project,
                "https://fulcrum.son4l.local/orders/456",
                project.JobUrl,
                _ => false));
        Assert.Null(ProjectExternalLinks.FindDeniedEditPermission(
            project,
            "https://fulcrum.son4l.local/orders/456",
            project.JobUrl,
            _ => true));
    }

    [Fact]
    public void DetailMapping_ExposesConfiguredUrlsToEveryAuthorizedViewer()
    {
        var project = new Project
        {
            ProgramName = "Shared external links",
            SalesOrderNumber = "SO-123",
            SalesOrderUrl = "https://fulcrum.son4l.local/orders/123",
            JobNumber = "JOB-456",
            JobUrl = "https://fulcrum.son4l.local/jobs/456"
        };

        var detail = ProjectDtoMapper.ToDetailDto(project);

        Assert.Equal(project.SalesOrderNumber, detail.SalesOrderNumber);
        Assert.Equal(project.SalesOrderUrl, detail.SalesOrderUrl);
        Assert.Equal(project.JobNumber, detail.JobNumber);
        Assert.Equal(project.JobUrl, detail.JobUrl);
    }

    [Fact]
    public void ActivityMapping_IncludesLinkChangesForAuthorizedViewers()
    {
        var entry = new ProjectAuditEntry
        {
            ChangesJson = JsonSerializer.Serialize(new[]
            {
                new ProjectAuditChange("Sales order", "SO-1", "SO-2"),
                new ProjectAuditChange("Sales order link", "https://old.example/", "https://new.example/")
            })
        };

        var mapped = ProjectDtoMapper.ToAuditEntryDto(entry);

        Assert.Equal(2, mapped.Changes.Count);
        Assert.Contains(mapped.Changes, change => change.Field == "Sales order link");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_AllowsAnOptionalEmptyValue(string? value)
    {
        Assert.True(ProjectExternalLinks.TryNormalize(value, "Job URL", out var normalized, out var error));
        Assert.Null(normalized);
        Assert.Null(error);
    }

    [Fact]
    public void TryNormalize_AcceptsAnAbsoluteHttpsUrl()
    {
        Assert.True(ProjectExternalLinks.TryNormalize(
            " https://fulcrum.son4l.local/orders/123?view=details ",
            "Sales order URL",
            out var normalized,
            out var error));

        Assert.Equal("https://fulcrum.son4l.local/orders/123?view=details", normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("http://fulcrum.son4l.local/orders/123", "must be an absolute HTTPS URL")]
    [InlineData("javascript:alert(1)", "must be an absolute HTTPS URL")]
    [InlineData("ftp://fulcrum.son4l.local/orders/123", "must be an absolute HTTPS URL")]
    [InlineData("/orders/123", "must be an absolute HTTPS URL")]
    [InlineData("not a URL", "must be an absolute HTTPS URL")]
    [InlineData("https://user:password@fulcrum.son4l.local/orders/123", "cannot contain a username or password")]
    public void TryNormalize_RejectsUnsafeOrInvalidUrls(string value, string expectedError)
    {
        Assert.False(ProjectExternalLinks.TryNormalize(value, "Sales order URL", out var normalized, out var error));
        Assert.Null(normalized);
        Assert.Contains(expectedError, error);
    }

    [Fact]
    public void TryNormalize_RejectsValuesBeyondTheDatabaseLimit()
    {
        var value = $"https://fulcrum.son4l.local/{new string('a', ProjectExternalLinks.MaxLength)}";

        Assert.False(ProjectExternalLinks.TryNormalize(value, "Job URL", out _, out var error));
        Assert.Contains(ProjectExternalLinks.MaxLength.ToString("N0"), error);
    }
}
