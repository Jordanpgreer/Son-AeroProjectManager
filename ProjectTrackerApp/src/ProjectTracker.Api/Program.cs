using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using ProjectTracker.Api.Services.Import;
using ProjectTracker.Api.Services.Reports;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUserService>();
builder.Services.AddSingleton<ScheduleCalculator>();
builder.Services.AddScoped<ProjectMetricsService>();
builder.Services.AddScoped<WorkbookImportService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var authMode = builder.Configuration["Authentication:Mode"] ?? (builder.Environment.IsDevelopment() ? "Development" : "Windows");
if (string.Equals(authMode, "Windows", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
    builder.Services.AddTransient<IClaimsTransformation, RoleClaimsTransformation>();
}
else
{
    builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, _ => { });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CanView", policy => policy.RequireRole("Viewer", "Editor", "Admin"));
    options.AddPolicy("CanEdit", policy => policy.RequireRole("Editor", "Admin"));
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

builder.Services.AddDbContext<ProjectTrackerDbContext>((serviceProvider, options) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var provider = configuration["Database:Provider"] ?? "SqlServer";
    if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlite(configuration.GetConnectionString("Sqlite"));
    }
    else
    {
        options.UseSqlServer(configuration.GetConnectionString("SqlServer"));
    }
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

await InitializeDatabaseAsync(app);

var api = app.MapGroup("/api").RequireAuthorization("CanView");

api.MapGet("/me", async (CurrentUserService currentUser, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    var user = await db.Users.FirstOrDefaultAsync(user => user.AccountName == currentUser.AccountName, cancellationToken);
    if (user is null)
    {
        user = new AppUser
        {
            AccountName = currentUser.AccountName,
            DisplayName = currentUser.DisplayName,
            Role = currentUser.Role
        };
        db.Users.Add(user);
    }

    user.LastSeenAt = DateTimeOffset.UtcNow;
    user.Role = currentUser.Role;
    await db.SaveChangesAsync(cancellationToken);
    return new UserDto(currentUser.AccountName, currentUser.DisplayName, currentUser.Role, currentUser.CanEdit, currentUser.IsAdmin);
});

api.MapGet("/dashboard", async (ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken) =>
{
    var projects = await db.Projects.Include(project => project.Tasks).ToListAsync(cancellationToken);
    var holidays = (await db.Holidays.Select(holiday => holiday.Date).ToListAsync(cancellationToken)).ToHashSet();
    foreach (var project in projects)
    {
        metrics.RefreshProject(project, holidays, DateOnly.FromDateTime(DateTime.Today));
    }
    await db.SaveChangesAsync(cancellationToken);

    var summaries = projects
        .OrderBy(project => project.Status == ProjectStatus.Behind ? 0 : 1)
        .ThenBy(project => project.TargetDelivery)
        .ThenBy(project => project.ProgramName)
        .Select(ToSummaryDto)
        .ToList();

    var activeProjects = projects.Count(project => project.Status != ProjectStatus.Complete);
    var dto = new DashboardDto(
        activeProjects,
        projects.Count(project => project.Status is ProjectStatus.OnTrack or ProjectStatus.Complete),
        projects.Count(project => project.Status == ProjectStatus.Behind),
        projects.Count == 0 ? 0m : Math.Round(projects.Average(project => project.Progress), 4),
        projects.Where(project => project.TargetDelivery is not null && project.Status != ProjectStatus.Complete).Select(project => project.TargetDelivery).Min(),
        summaries);

    return dto;
});

api.MapGet("/projects", async (ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    var projects = await db.Projects.Include(project => project.Tasks).OrderBy(project => project.ProgramName).ToListAsync(cancellationToken);
    return projects.Select(ToSummaryDto);
});

api.MapGet("/projects/{id:int}", async (int id, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    var project = await db.Projects.Include(project => project.Tasks).FirstOrDefaultAsync(project => project.Id == id, cancellationToken);
    return project is null ? Results.NotFound() : Results.Ok(ToDetailDto(project));
});

api.MapGet("/calendar", async (ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    var projects = await db.Projects.Include(project => project.Tasks).OrderBy(project => project.ProgramName).ToListAsync(cancellationToken);
    return projects.Select(ToDetailDto);
});

api.MapPost("/projects", async (ProjectUpsertDto dto, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(dto.ProgramName))
    {
        return Results.BadRequest("Program name is required.");
    }

    var project = new Project
    {
        ProgramName = dto.ProgramName.Trim(),
        ProgramManager = string.IsNullOrWhiteSpace(dto.ProgramManager) ? null : dto.ProgramManager.Trim()
    };
    db.Projects.Add(project);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/projects/{project.Id}", ToDetailDto(project));
}).RequireAuthorization("CanEdit");

api.MapPut("/projects/{id:int}", async (int id, ProjectUpsertDto dto, ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken) =>
{
    var project = await db.Projects.Include(project => project.Tasks).FirstOrDefaultAsync(project => project.Id == id, cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }

    project.ProgramName = dto.ProgramName.Trim();
    project.ProgramManager = string.IsNullOrWhiteSpace(dto.ProgramManager) ? null : dto.ProgramManager.Trim();
    await metrics.RefreshProjectAsync(db, project, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(ToDetailDto(project));
}).RequireAuthorization("CanEdit");

api.MapDelete("/projects/{id:int}", async (int id, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    var project = await db.Projects.FindAsync([id], cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }

    db.Projects.Remove(project);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("CanEdit");

api.MapPost("/projects/{projectId:int}/tasks", async (int projectId, TaskUpsertDto dto, ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken) =>
{
    var project = await db.Projects.Include(project => project.Tasks).FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }

    var task = ApplyTaskDto(new ProjectTask { ProjectId = projectId }, dto);
    project.Tasks.Add(task);
    var desiredPosition = dto.Sequence > 0 ? dto.Sequence : project.Tasks.Count;
    ResequenceTasks(project, task, desiredPosition);
    await EnsurePhaseAsync(db, task.Phase, cancellationToken);
    await metrics.RefreshProjectAsync(db, project, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/projects/{projectId}", ToTaskDto(task));
}).RequireAuthorization("CanEdit");

api.MapPut("/tasks/{taskId:int}", async (int taskId, TaskUpsertDto dto, ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken) =>
{
    var task = await db.Tasks.Include(task => task.Project).ThenInclude(project => project.Tasks).FirstOrDefaultAsync(task => task.Id == taskId, cancellationToken);
    if (task is null)
    {
        return Results.NotFound();
    }

    ApplyTaskDto(task, dto);
    ResequenceTasks(task.Project, task, dto.Sequence);
    await EnsurePhaseAsync(db, task.Phase, cancellationToken);
    await metrics.RefreshProjectAsync(db, task.Project, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(ToTaskDto(task));
}).RequireAuthorization("CanEdit");

api.MapDelete("/tasks/{taskId:int}", async (int taskId, ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken) =>
{
    var task = await db.Tasks.Include(task => task.Project).ThenInclude(project => project.Tasks).FirstOrDefaultAsync(task => task.Id == taskId, cancellationToken);
    if (task is null)
    {
        return Results.NotFound();
    }

    var project = task.Project;
    project.Tasks.Remove(task);
    db.Tasks.Remove(task);
    RenumberTasks(project);
    await metrics.RefreshProjectAsync(db, project, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("CanEdit");

api.MapGet("/holidays", async (ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    return await db.Holidays.OrderBy(holiday => holiday.Date)
        .Select(holiday => new HolidayDto(holiday.Id, holiday.Date, holiday.Name))
        .ToListAsync(cancellationToken);
});

api.MapPost("/holidays", async (HolidayUpsertDto dto, ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken) =>
{
    var holiday = new Holiday { Date = dto.Date, Name = dto.Name.Trim() };
    db.Holidays.Add(holiday);
    await RefreshAllProjectsAsync(db, metrics, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/holidays/{holiday.Id}", new HolidayDto(holiday.Id, holiday.Date, holiday.Name));
}).RequireAuthorization("CanEdit");

api.MapPut("/holidays/{id:int}", async (int id, HolidayUpsertDto dto, ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken) =>
{
    var holiday = await db.Holidays.FindAsync([id], cancellationToken);
    if (holiday is null)
    {
        return Results.NotFound();
    }

    holiday.Date = dto.Date;
    holiday.Name = dto.Name.Trim();
    await RefreshAllProjectsAsync(db, metrics, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new HolidayDto(holiday.Id, holiday.Date, holiday.Name));
}).RequireAuthorization("CanEdit");

api.MapDelete("/holidays/{id:int}", async (int id, ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken) =>
{
    var holiday = await db.Holidays.FindAsync([id], cancellationToken);
    if (holiday is null)
    {
        return Results.NotFound();
    }

    db.Holidays.Remove(holiday);
    await RefreshAllProjectsAsync(db, metrics, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("CanEdit");

api.MapPost("/import/workbook", async (ImportWorkbookRequest request, IConfiguration configuration, IWebHostEnvironment env, ProjectTrackerDbContext db, WorkbookImportService importer, CancellationToken cancellationToken) =>
{
    var path = ResolveWorkbookPath(request.Path, configuration, env);
    var result = await importer.ImportAsync(db, path, request.ReplaceExisting, cancellationToken);
    return Results.Ok(result);
}).RequireAuthorization("AdminOnly");

api.MapGet("/reports/portfolio.xlsx", async (ReportService reports, CancellationToken cancellationToken) =>
{
    var report = await reports.PortfolioExcelAsync(cancellationToken);
    return Results.File(report.Content, report.ContentType, report.FileName);
});

api.MapGet("/reports/portfolio.pdf", async (ReportService reports, CancellationToken cancellationToken) =>
{
    var report = await reports.PortfolioPdfAsync(cancellationToken);
    return Results.File(report.Content, report.ContentType, report.FileName);
});

api.MapGet("/reports/projects/{id:int}.xlsx", async (int id, ReportService reports, CancellationToken cancellationToken) =>
{
    try
    {
        var report = await reports.ProjectExcelAsync(id, cancellationToken);
        return Results.File(report.Content, report.ContentType, report.FileName);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

api.MapGet("/reports/projects/{id:int}.pdf", async (int id, ReportService reports, CancellationToken cancellationToken) =>
{
    try
    {
        var report = await reports.ProjectPdfAsync(id, cancellationToken);
        return Results.File(report.Content, report.ContentType, report.FileName);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapFallbackToFile("index.html");

app.Run();

static ProjectSummaryDto ToSummaryDto(Project project)
{
    var today = DateOnly.FromDateTime(DateTime.Today);
    var daysLeft = project.TargetDelivery is null ? (int?)null : project.TargetDelivery.Value.DayNumber - today.DayNumber;
    return new ProjectSummaryDto(
        project.Id,
        project.ProgramName,
        project.ProgramManager,
        project.CurrentTask,
        project.Progress,
        project.TargetDelivery,
        daysLeft,
        project.Status,
        project.Tasks.Count,
        project.Tasks.Count(task => task.Status == TaskScheduleStatus.Behind));
}

static ProjectDetailDto ToDetailDto(Project project)
{
    return new ProjectDetailDto(
        project.Id,
        project.ProgramName,
        project.ProgramManager,
        project.CurrentTask,
        project.ProgramStart,
        project.TargetDelivery,
        project.Progress,
        project.Status,
        project.Tasks.OrderBy(task => task.Sequence).Select(ToTaskDto).ToList());
}

static ProjectTaskDto ToTaskDto(ProjectTask task)
{
    return new ProjectTaskDto(
        task.Id,
        task.ProjectId,
        task.Sequence,
        task.ExternalTaskId,
        task.Title,
        task.Phase,
        task.WorkStation,
        task.StartDate,
        task.OriginalStartDate,
        task.EndDate,
        task.OriginalEndDate,
        task.EstimatedDuration,
        task.ActualDuration,
        task.PercentComplete,
        task.Status,
        task.Notes);
}

static ProjectTask ApplyTaskDto(ProjectTask task, TaskUpsertDto dto)
{
    if (string.IsNullOrWhiteSpace(dto.Title))
    {
        throw new BadHttpRequestException("Task title is required.");
    }

    task.Sequence = dto.Sequence;
    task.ExternalTaskId = string.IsNullOrWhiteSpace(dto.ExternalTaskId) ? null : dto.ExternalTaskId.Trim();
    task.Title = dto.Title.Trim();
    task.Phase = string.IsNullOrWhiteSpace(dto.Phase) ? null : dto.Phase.Trim();
    task.WorkStation = string.IsNullOrWhiteSpace(dto.WorkStation) ? null : dto.WorkStation.Trim();
    task.StartDate = dto.StartDate;
    task.OriginalStartDate = dto.OriginalStartDate;
    task.EndDate = dto.EndDate;
    task.OriginalEndDate = dto.OriginalEndDate;
    task.EstimatedDuration = dto.EstimatedDuration;
    task.ActualDuration = dto.ActualDuration;
    task.PercentComplete = Math.Clamp(dto.PercentComplete, 0m, 1m);
    task.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
    task.UpdatedAt = DateTimeOffset.UtcNow;
    return task;
}

// Steps are numbered 1..N by position. "Step Order" is the desired position; moving a step
// renumbers every step's Sequence (and ExternalTaskId, which is the same value) to stay 1..N.
static void ResequenceTasks(Project project, ProjectTask moved, int desiredPosition)
{
    var ordered = project.Tasks
        .Where(task => !ReferenceEquals(task, moved))
        .OrderBy(task => task.Sequence)
        .ToList();

    var index = Math.Clamp(desiredPosition - 1, 0, ordered.Count);
    ordered.Insert(index, moved);
    ApplyPositions(ordered);
}

static void RenumberTasks(Project project)
{
    ApplyPositions(project.Tasks.OrderBy(task => task.Sequence).ToList());
}

static void ApplyPositions(IReadOnlyList<ProjectTask> ordered)
{
    for (var position = 0; position < ordered.Count; position++)
    {
        ordered[position].Sequence = position + 1;
        ordered[position].ExternalTaskId = (position + 1).ToString();
    }
}

static async Task EnsurePhaseAsync(ProjectTrackerDbContext db, string? phaseName, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(phaseName))
    {
        return;
    }

    if (!await db.Phases.AnyAsync(phase => phase.Name == phaseName, cancellationToken))
    {
        var sortOrder = await db.Phases.Select(phase => (int?)phase.SortOrder).MaxAsync(cancellationToken) ?? 0;
        db.Phases.Add(new Phase { Name = phaseName, SortOrder = sortOrder + 10 });
    }
}

static async Task RefreshAllProjectsAsync(ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken)
{
    var projects = await db.Projects.Include(project => project.Tasks).ToListAsync(cancellationToken);
    var holidays = (await db.Holidays.Select(holiday => holiday.Date).ToListAsync(cancellationToken)).ToHashSet();
    foreach (var project in projects)
    {
        metrics.RefreshProject(project, holidays, DateOnly.FromDateTime(DateTime.Today));
    }
}

static string ResolveWorkbookPath(string? requestedPath, IConfiguration configuration, IWebHostEnvironment env)
{
    var path = string.IsNullOrWhiteSpace(requestedPath) ? configuration["Import:DefaultWorkbookPath"] : requestedPath;
    if (string.IsNullOrWhiteSpace(path))
    {
        throw new InvalidOperationException("No workbook path was provided.");
    }

    return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(env.ContentRootPath, path));
}

static async Task InitializeDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ProjectTrackerDbContext>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var provider = configuration["Database:Provider"] ?? "SqlServer";
    var autoMigrate = configuration.GetValue("Database:AutoMigrate", true);

    if (autoMigrate && string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        await db.Database.MigrateAsync();
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }

    var autoImport = configuration.GetValue("Import:AutoImportOnEmpty", app.Environment.IsDevelopment());
    if (autoImport && !await db.Projects.AnyAsync())
    {
        var importer = scope.ServiceProvider.GetRequiredService<WorkbookImportService>();
        var workbookPath = ResolveWorkbookPath(null, configuration, app.Environment);
        if (File.Exists(workbookPath))
        {
            await importer.ImportAsync(db, workbookPath, replaceExisting: true);
        }
    }
}

public partial class Program;
