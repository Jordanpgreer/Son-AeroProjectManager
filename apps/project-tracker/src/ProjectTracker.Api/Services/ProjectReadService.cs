using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Mapping;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public sealed class ProjectReadService(ProjectTrackerDbContext db, ProjectMetricsService metrics)
{
    public async Task<DashboardDto> DashboardAsync(CancellationToken cancellationToken = default)
    {
        var projects = await LoadProjectsAsync(cancellationToken);
        await RefreshForReadAsync(projects, cancellationToken);

        var summaries = projects
            .OrderBy(project => project.Status == ProjectStatus.Complete ? 1 : 0)
            .ThenBy(project => project.PriorityRank ?? int.MaxValue)
            .ThenBy(project => project.TargetDelivery)
            .ThenBy(project => project.ProgramName)
            .Select(ProjectDtoMapper.ToSummaryDto)
            .ToList();
        var active = projects.Where(project => project.Status != ProjectStatus.Complete).ToList();

        return new DashboardDto(
            active.Count,
            projects.Count(project => project.Status is ProjectStatus.OnTrack or ProjectStatus.Complete),
            projects.Count(project => project.Status == ProjectStatus.Behind),
            projects.Count == 0 ? 0m : Math.Round(projects.Average(project => project.Progress), 4),
            active.Where(project => project.TargetDelivery is not null).Select(project => project.TargetDelivery).Min(),
            summaries);
    }

    public async Task<IReadOnlyList<ProjectSummaryDto>> SummariesAsync(CancellationToken cancellationToken = default)
    {
        var projects = await LoadProjectsAsync(cancellationToken);
        await RefreshForReadAsync(projects, cancellationToken);
        return projects.OrderBy(project => project.ProgramName).Select(ProjectDtoMapper.ToSummaryDto).ToList();
    }

    public async Task<TrackerPreviewDto> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var projects = await LoadProjectsAsync(cancellationToken);
        await RefreshForReadAsync(projects, cancellationToken);

        var active = projects
            .Where(project => project.Status != ProjectStatus.Complete)
            .ToList();

        var rows = active
            .OrderBy(project => project.PriorityRank ?? int.MaxValue)
            .ThenByDescending(project => project.Progress)
            .Take(5)
            .Select(project => new TrackerPreviewRowDto(
                project.ProgramName,
                decimal.ToDouble(project.Progress),
                NormalizePreviewStatus(project.Status)))
            .ToList();

        return new TrackerPreviewDto(
            active.Count,
            active.Count(project => project.Status == ProjectStatus.OnTrack),
            active.Count(project => project.Status == ProjectStatus.Behind),
            active.Count == 0 ? 0d : active.Average(project => decimal.ToDouble(project.Progress)),
            rows);
    }

    public async Task<ProjectDetailDto?> DetailAsync(int id, CancellationToken cancellationToken = default)
    {
        var project = await ProjectQuery().FirstOrDefaultAsync(project => project.Id == id, cancellationToken);
        if (project is null)
        {
            return null;
        }

        await RefreshForReadAsync([project], cancellationToken);
        return ProjectDtoMapper.ToDetailDto(project);
    }

    public Task<ProjectVersionDto?> VersionAsync(int id, CancellationToken cancellationToken = default) =>
        db.Projects
            .AsNoTracking()
            .Where(project => project.Id == id)
            .Select(project => new ProjectVersionDto(project.Id, project.Version, project.UpdatedAt))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ProjectDetailDto>> CalendarAsync(CancellationToken cancellationToken = default)
    {
        var projects = await LoadProjectsAsync(cancellationToken);
        await RefreshForReadAsync(projects, cancellationToken);
        return projects.OrderBy(project => project.ProgramName).Select(ProjectDtoMapper.ToDetailDto).ToList();
    }

    private Task<List<Project>> LoadProjectsAsync(CancellationToken cancellationToken) =>
        ProjectQuery().ToListAsync(cancellationToken);

    private IQueryable<Project> ProjectQuery() => db.Projects
        .AsNoTracking()
        .Include(project => project.Tasks)
        .ThenInclude(task => task.OvertimeDays);

    private async Task RefreshForReadAsync(IReadOnlyCollection<Project> projects, CancellationToken cancellationToken)
    {
        var settings = await db.ScheduleSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? new ScheduleSettings();
        var holidays = (await db.Holidays.AsNoTracking().Select(holiday => holiday.Date).ToListAsync(cancellationToken)).ToHashSet();
        var calendar = new ScheduleCalendar(settings.GetWorkingDays(), holidays);
        foreach (var project in projects)
        {
            metrics.RefreshProject(project, calendar, DateOnly.FromDateTime(DateTime.Today), updateVersions: false);
        }

        NormalizePrioritiesForRead(projects);
    }

    private static void NormalizePrioritiesForRead(IEnumerable<Project> projects)
    {
        var all = projects.ToList();
        var active = all
            .Where(project => project.Status != ProjectStatus.Complete)
            .OrderBy(project => project.PriorityRank ?? int.MaxValue)
            .ThenBy(project => project.Status == ProjectStatus.Behind ? 0 : 1)
            .ThenBy(project => project.TargetDelivery)
            .ThenBy(project => project.ProgramName)
            .ToList();

        for (var index = 0; index < active.Count; index++)
        {
            active[index].PriorityRank = index + 1;
        }

        foreach (var project in all.Where(project => project.Status == ProjectStatus.Complete))
        {
            project.PriorityRank = null;
        }
    }

    private static string NormalizePreviewStatus(ProjectStatus status) => status switch
    {
        ProjectStatus.OnTrack => "onTrack",
        ProjectStatus.Behind => "behind",
        ProjectStatus.Complete => "complete",
        _ => "notStarted"
    };
}
