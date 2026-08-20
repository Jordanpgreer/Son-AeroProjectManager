using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Configuration;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Endpoints;
using static ProjectTracker.Api.Mapping.ProjectDtoMapper;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using ProjectTracker.Api.Services.Import;
using ProjectTracker.Api.Services.Reports;
using SonAero.Platform.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUserService>();
builder.Services.AddScoped<ProjectTrackerAccessPreviewService>();
builder.Services.AddScoped<ProjectAuditService>();
builder.Services.AddScoped<MentionNotificationService>();
builder.Services.AddScoped<NotificationReadService>();
builder.Services.AddScoped<OperationScheduleReminderService>();
builder.Services.AddScoped<PushSubscriptionService>();
builder.Services.AddOptions<WebPushOptions>()
    .Bind(builder.Configuration.GetSection(WebPushOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<WebPushOptions>, WebPushOptionsValidator>();
builder.Services.AddSingleton<IPushNotificationQueue, PushNotificationQueue>();
builder.Services.AddSingleton<IWebPushSender, WebPushSender>();
builder.Services.AddHostedService<PushNotificationWorker>();
builder.Services.AddHostedService<OperationScheduleReminderWorker>();
builder.Services.AddScoped<AccessControlSeeder>();
builder.Services.AddScoped<ModuleAccessService>();
builder.Services.AddSingleton<ScheduleCalculator>();
builder.Services.AddScoped<ProjectMetricsService>();
builder.Services.AddScoped<ProjectReadService>();
builder.Services.AddScoped<WorkbookImportService>();
builder.Services.AddScoped<ControlledWorkbookImportService>();
builder.Services.AddScoped<WorkCenterWorkbookImportService>();
builder.Services.AddSingleton<ControlledImportReviewStore>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHubCors(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var authMode = builder.Configuration["Authentication:Mode"] ?? (builder.Environment.IsDevelopment() ? "Development" : "Windows");
if (string.Equals(authMode, "Windows", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
}
else
{
    builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, _ => { });
}
builder.Services.AddScoped<IClaimsTransformation, RoleClaimsTransformation>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ProjectTrackerAccessAuthorization.PolicyName, ProjectTrackerAccessAuthorization.ConfigurePolicy);
    options.AddPolicy("ProjectCreate", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ProjectCreate));
    options.AddPolicy("ProjectPriority", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ProjectReorderPriority));
    options.AddPolicy("ProjectComplete", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ProjectComplete));
    options.AddPolicy("ProjectReopen", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ProjectReopen));
    options.AddPolicy("ProjectArchive", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ProjectArchive));
    options.AddPolicy("ProjectActivityView", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ProjectTrackerPermissions.ProjectActivityView));
    options.AddPolicy("TaskCreate", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.TaskCreate));
    options.AddPolicy("TaskDelete", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.TaskDelete));
    options.AddPolicy("ManageCalendar", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.SettingsWorkCalendarManage));
    options.AddPolicy("ManageHolidays", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.SettingsHolidaysManage));
    options.AddPolicy("ManageWorkCenters", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.SettingsWorkCentersManage));
    options.AddPolicy(WorkCenterImportEndpoints.AuthorizationPolicy, policy =>
        policy.RequireClaim(ApplicationClaimTypes.Permission, ProjectTrackerPermissions.WorkCentersImport));
    options.AddPolicy("ManageImports", policy => policy
        .RequireClaim(ApplicationClaimTypes.Group, ApplicationGroups.Administrators)
        .RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ImportManage));
    options.AddPolicy(
        AccessOverviewAuthorization.PolicyName,
        policy => policy.RequireAssertion(context => AccessOverviewAuthorization.IsAllowed(context.User)));
    options.AddPolicy("ManageUsers", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.AccessManageUsers));
    options.AddPolicy("ManageGroups", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.AccessManageGroups));
    options.AddPolicy("RestoreArchived", policy => policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ArchivedRestore));
    options.AddPolicy(
        ArchivedProjectEndpoints.PermanentDeletePolicyName,
        ArchivedProjectEndpoints.ConfigurePermanentDeletePolicy);
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

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
        }
        return Task.CompletedTask;
    });
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseCors(HubCorsPolicy.Name);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseMiddleware<AccessPreviewMiddleware>();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (DbUpdateConcurrencyException) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new ConcurrencyConflictDto(
            "ConcurrencyConflict",
            "This record was changed by another user. Reload the latest version before saving again.",
            "Record",
            0));
    }
});

await InitializeDatabaseAsync(app);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapAccessPreviewEndpoints();

var api = app.MapGroup("/api").RequireAuthorization(ProjectTrackerAccessAuthorization.PolicyName);
api.MapProjectReadEndpoints();
api.MapArchivedProjectEndpoints();
api.MapUserEndpoints();
api.MapModuleAccessEndpoints();
api.MapNotificationEndpoints();
api.MapPushNotificationEndpoints();
api.MapReportEndpoints();
api.MapImportEndpoints();
api.MapWorkCenterImportEndpoints();

api.MapGet("/projects/{id:int}/messages", async (int id, int? afterId, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    if (!await db.Projects.AnyAsync(project => project.Id == id, cancellationToken))
    {
        return Results.NotFound();
    }

    var query = db.ProjectMessages.Where(message => message.ProjectId == id);
    if (afterId is > 0)
    {
        var recent = await query
            .Where(message => message.Id > afterId.Value)
            .OrderBy(message => message.Id)
            .Take(200)
            .Select(message => new ProjectMessageDto(message.Id, message.ProjectId, message.AuthorAccountName, message.AuthorDisplayName, message.Body, message.CreatedAt))
            .ToListAsync(cancellationToken);
        return Results.Ok(recent);
    }

    var messages = await query
        .OrderByDescending(message => message.Id)
        .Take(200)
        .Select(message => new ProjectMessageDto(message.Id, message.ProjectId, message.AuthorAccountName, message.AuthorDisplayName, message.Body, message.CreatedAt))
        .ToListAsync(cancellationToken);
    messages.Reverse();
    return Results.Ok(messages);
});

api.MapGet("/projects/{id:int}/activity", async (int id, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    if (!await db.Projects.AnyAsync(project => project.Id == id, cancellationToken))
    {
        return Results.NotFound();
    }

    var entries = await db.ProjectAuditEntries
        .Where(entry => entry.ProjectId == id)
        .OrderByDescending(entry => entry.Id)
        .Take(300)
        .AsNoTracking()
        .ToListAsync(cancellationToken);
    return Results.Ok(entries.Select(ToAuditEntryDto).ToList());
}).RequireAuthorization("ProjectActivityView");

api.MapPost("/projects/{id:int}/messages", async (int id, ProjectMessageCreateDto dto, ProjectTrackerDbContext db, CurrentUserService currentUser, MentionNotificationService notifications, CancellationToken cancellationToken) =>
{
    var project = await db.Projects.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }

    var body = dto.Body?.Trim();
    if (string.IsNullOrWhiteSpace(body))
    {
        return Results.BadRequest("Message text is required.");
    }
    if (body.Length > 2000)
    {
        return Results.BadRequest("Messages cannot exceed 2,000 characters.");
    }

    var message = new ProjectMessage
    {
        ProjectId = id,
        AuthorAccountName = currentUser.AccountName,
        AuthorDisplayName = currentUser.DisplayName,
        Body = body
    };
    db.ProjectMessages.Add(message);
    var mentionNotifications = await notifications.AddForProjectMessageAsync(
        db,
        message,
        project.ProgramName,
        currentUser.AccountName,
        currentUser.DisplayName,
        cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    notifications.DispatchAfterPersistence(mentionNotifications);
    return Results.Created($"/api/projects/{id}/messages/{message.Id}", ToMessageDto(message));
});

api.MapGet("/users/mentions", async (ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    var users = await db.Users
        .OrderBy(user => user.DisplayName)
        .ThenBy(user => user.AccountName)
        .Where(user => user.IsActive)
        .ToListAsync(cancellationToken);
    return users.Select(user => new MentionableUserDto(user.AccountName, user.DisplayName, MentionNotificationService.MentionHandle(user.AccountName))).ToList();
});

api.MapPost("/projects", async (ProjectCreateDto dto, ProjectTrackerDbContext db, CurrentUserService currentUser, ProjectMetricsService metrics, ProjectAuditService audit, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(dto.ProgramName))
    {
        return Results.BadRequest("Program name is required.");
    }

    var programName = dto.ProgramName.Trim();
    if (await db.Projects.AnyAsync(project => project.ProgramName == programName, cancellationToken))
    {
        return Results.Conflict("A project with this part number already exists.");
    }

    if ((!string.IsNullOrWhiteSpace(dto.SalesOrderUrl) || !string.IsNullOrWhiteSpace(dto.JobUrl))
        && !currentUser.HasPermission(ProjectTrackerPermissions.ProjectEditExternalLinks))
    {
        return Results.Forbid();
    }
    if (!ProjectExternalLinks.TryNormalize(dto.SalesOrderUrl, "Sales order URL", out var salesOrderUrl, out var salesOrderUrlError))
    {
        return Results.BadRequest(salesOrderUrlError);
    }
    if (!ProjectExternalLinks.TryNormalize(dto.JobUrl, "Job URL", out var jobUrl, out var jobUrlError))
    {
        return Results.BadRequest(jobUrlError);
    }

    var nextPriority = (await db.Projects
        .Where(project => project.Status != ProjectStatus.Complete)
        .Select(project => project.PriorityRank)
        .MaxAsync(cancellationToken) ?? 0) + 1;

    var project = new Project
    {
        ProgramName = programName,
        ProgramManager = Clean(dto.ProgramManager),
        Engineer = Clean(dto.Engineer),
        CustomerName = Clean(dto.CustomerName),
        SalesOrderNumber = Clean(dto.SalesOrderNumber),
        SalesOrderUrl = salesOrderUrl,
        JobNumber = Clean(dto.JobNumber),
        JobUrl = jobUrl,
        ProgramStart = dto.ProgramStart,
        PriorityRank = nextPriority
    };

    if (dto.TemplateProjectId is not null)
    {
        var template = await db.Projects.Include(source => source.Tasks)
            .FirstOrDefaultAsync(source => source.Id == dto.TemplateProjectId.Value, cancellationToken);
        if (template is null)
        {
            return Results.BadRequest("The selected operation template no longer exists.");
        }

        foreach (var source in template.Tasks.OrderBy(task => task.Sequence))
        {
            project.Tasks.Add(new ProjectTask
            {
                Sequence = source.Sequence,
                ExternalTaskId = source.ExternalTaskId,
                Title = source.Title,
                Phase = source.Phase,
                WorkStation = source.WorkStation,
                DependencyTaskId = null,
                EstimatedDuration = source.EstimatedDuration,
                ActualDuration = source.ActualDuration,
                Notes = source.Notes
            });
        }
    }

    db.Projects.Add(project);
    if (project.Tasks.Count > 0)
    {
        await metrics.RefreshProjectAsync(db, project, cancellationToken, recalculateDates: true);
    }
    audit.Record(
        db,
        project,
        "ProjectCreated",
        $"Created project {project.ProgramName}",
        ProjectAuditService.CaptureProject(project)
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .Select(field => new ProjectAuditChange(field.Key, null, field.Value))
            .ToList());
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/projects/{project.Id}", ToDetailDto(project));
}).RequireAuthorization("ProjectCreate");

api.MapPut("/projects/{id:int}", async (int id, ProjectUpsertDto dto, ProjectTrackerDbContext db, CurrentUserService currentUser, ProjectMetricsService metrics, ProjectAuditService audit, CancellationToken cancellationToken) =>
{
    var project = await db.Projects.Include(project => project.Tasks).ThenInclude(task => task.OvertimeDays).FirstOrDefaultAsync(project => project.Id == id, cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }

    if (IsArchived(project))
    {
        return Results.Conflict("Completed projects are read-only. Make the project active before editing.");
    }
    if (dto.Version != project.Version)
    {
        return ConcurrencyConflict("Project", project.Id);
    }
    if (!ProjectExternalLinks.TryNormalize(dto.SalesOrderUrl, "Sales order URL", out var salesOrderUrl, out var salesOrderUrlError))
    {
        return Results.BadRequest(salesOrderUrlError);
    }
    if (!ProjectExternalLinks.TryNormalize(dto.JobUrl, "Job URL", out var jobUrl, out var jobUrlError))
    {
        return Results.BadRequest(jobUrlError);
    }
    var deniedProjectPermission = FindDeniedProjectPermission(project, dto, salesOrderUrl, jobUrl, currentUser);
    if (deniedProjectPermission is not null)
    {
        return Results.Forbid();
    }

    var before = ProjectAuditService.CaptureProject(project);
    ApplyProjectDto(project, dto, salesOrderUrl, jobUrl);
    project.Version++;
    await metrics.RefreshProjectAsync(db, project, cancellationToken);
    var changes = ProjectAuditService.Diff(before, ProjectAuditService.CaptureProject(project));
    if (changes.Count > 0)
    {
        audit.Record(db, project, "ProjectUpdated", "Updated project details", changes);
    }
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(ToDetailDto(project));
}).RequireAuthorization(ProjectTrackerAccessAuthorization.PolicyName);

api.MapPost("/projects/{id:int}/complete", async (int id, ProjectActionDto dto, ProjectTrackerDbContext db, ProjectMetricsService metrics, ProjectAuditService audit, CancellationToken cancellationToken) =>
{
    var project = await db.Projects.Include(project => project.Tasks).ThenInclude(task => task.OvertimeDays).FirstOrDefaultAsync(project => project.Id == id, cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }
    if (dto.Version != project.Version)
    {
        return ConcurrencyConflict("Project", project.Id);
    }

    var before = ProjectAuditService.CaptureProject(project);
    foreach (var task in project.Tasks)
    {
        task.PercentComplete = 1m;
        task.PercentCompleteManual = true;
        task.UpdatedAt = DateTimeOffset.UtcNow;
        task.Version++;
    }

    await metrics.RefreshProjectAsync(db, project, cancellationToken);

    project.CompletedOn = DateOnly.FromDateTime(DateTime.Today);
    project.PriorityRank = null;
    project.Progress = 1m;
    project.Status = ProjectStatus.Complete;
    project.CurrentTask = "Program Complete";
    project.UpdatedAt = DateTimeOffset.UtcNow;
    project.Version++;

    var remainingProjects = await db.Projects
        .Where(candidate => candidate.Id != project.Id)
        .ToListAsync(cancellationToken);
    var previousPriorities = remainingProjects.ToDictionary(candidate => candidate.Id, candidate => candidate.PriorityRank);
    NormalizeProjectPriorities([project, .. remainingProjects]);
    BumpPriorityVersions(remainingProjects, previousPriorities);
    audit.Record(
        db,
        project,
        "ProjectCompleted",
        "Marked project complete",
        ProjectAuditService.Diff(before, ProjectAuditService.CaptureProject(project)));
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(ToDetailDto(project));
}).RequireAuthorization("ProjectComplete");

api.MapPost("/projects/{id:int}/reopen", async (int id, ProjectActionDto dto, ProjectTrackerDbContext db, ProjectMetricsService metrics, ProjectAuditService audit, CancellationToken cancellationToken) =>
{
    var project = await db.Projects.Include(project => project.Tasks).ThenInclude(task => task.OvertimeDays).FirstOrDefaultAsync(project => project.Id == id, cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }
    if (dto.Version != project.Version)
    {
        return ConcurrencyConflict("Project", project.Id);
    }

    var before = ProjectAuditService.CaptureProject(project);
    project.CompletedOn = null;
    project.PriorityRank = (await db.Projects
        .Where(candidate => candidate.Id != project.Id && candidate.Status != ProjectStatus.Complete)
        .Select(candidate => candidate.PriorityRank)
        .MaxAsync(cancellationToken) ?? 0) + 1;
    var finalTask = project.Tasks
        .Where(task => !string.IsNullOrWhiteSpace(task.Title))
        .OrderBy(task => task.Sequence)
        .LastOrDefault()
        ?? project.Tasks.OrderBy(task => task.Sequence).LastOrDefault();
    if (finalTask is not null)
    {
        finalTask.PercentComplete = 0m;
        finalTask.PercentCompleteManual = true;
        finalTask.UpdatedAt = DateTimeOffset.UtcNow;
        finalTask.Version++;
    }

    project.Version++;
    await metrics.RefreshProjectAsync(db, project, cancellationToken);
    var otherProjects = await db.Projects
        .Where(candidate => candidate.Id != project.Id)
        .ToListAsync(cancellationToken);
    var previousPriorities = otherProjects.ToDictionary(candidate => candidate.Id, candidate => candidate.PriorityRank);
    NormalizeProjectPriorities([project, .. otherProjects]);
    BumpPriorityVersions(otherProjects, previousPriorities);
    audit.Record(
        db,
        project,
        "ProjectReopened",
        "Made project active",
        ProjectAuditService.Diff(before, ProjectAuditService.CaptureProject(project)));
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(ToDetailDto(project));
}).RequireAuthorization("ProjectReopen");

api.MapPut("/projects/{id:int}/priority", async (int id, ProjectPriorityDto dto, ProjectTrackerDbContext db, ProjectAuditService audit, CancellationToken cancellationToken) =>
{
    var projects = await db.Projects.ToListAsync(cancellationToken);
    var project = projects.FirstOrDefault(candidate => candidate.Id == id);
    if (project is null)
    {
        return Results.NotFound();
    }
    if (IsArchived(project))
    {
        return Results.Conflict("Completed projects do not have an active priority.");
    }
    if (dto.Version != project.Version)
    {
        return ConcurrencyConflict("Project", project.Id);
    }

    var previousPriorities = projects.ToDictionary(candidate => candidate.Id, candidate => candidate.PriorityRank);
    NormalizeProjectPriorities(projects);
    var active = projects
        .Where(candidate => candidate.Status != ProjectStatus.Complete && candidate.Id != id)
        .OrderBy(candidate => candidate.PriorityRank)
        .ToList();
    var targetIndex = Math.Clamp(dto.PriorityRank - 1, 0, active.Count);
    active.Insert(targetIndex, project);
    for (var index = 0; index < active.Count; index++)
    {
        active[index].PriorityRank = index + 1;
    }

    foreach (var changedProject in active.Where(candidate => previousPriorities[candidate.Id] != candidate.PriorityRank))
    {
        var oldRank = previousPriorities[changedProject.Id];
        changedProject.Version++;
        changedProject.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(
            db,
            changedProject,
            "PriorityChanged",
            changedProject.Id == id ? "Changed project priority" : "Priority adjusted after queue reorder",
            [new ProjectAuditChange("Priority", oldRank is null ? null : $"P{oldRank}", $"P{changedProject.PriorityRank}")]);
    }

    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("ProjectPriority");

api.MapDelete("/projects/{id:int}", async (
    int id,
    long version,
    ProjectTrackerDbContext db,
    CurrentUserService currentUser,
    ProjectAuditService audit,
    CancellationToken cancellationToken) =>
{
    var project = await db.Projects.FindAsync([id], cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }
    if (version != project.Version)
    {
        return ConcurrencyConflict("Project", project.Id);
    }

    audit.Record(
        db,
        project,
        "ProjectArchived",
        "Archived project",
        [new ProjectAuditChange("Archived", null, "Yes")]);
    project.DeletedAt = DateTimeOffset.UtcNow;
    project.DeletedByAccountName = currentUser.AccountName;
    project.DeletedByDisplayName = currentUser.DisplayName;
    project.PriorityRank = null;
    project.Version++;
    project.UpdatedAt = DateTimeOffset.UtcNow;
    var remainingProjects = await db.Projects
        .Where(candidate => candidate.Id != id)
        .ToListAsync(cancellationToken);
    var previousPriorities = remainingProjects.ToDictionary(candidate => candidate.Id, candidate => candidate.PriorityRank);
    NormalizeProjectPriorities(remainingProjects);
    BumpPriorityVersions(remainingProjects, previousPriorities);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("ProjectArchive");

api.MapPost("/projects/{projectId:int}/tasks", async (int projectId, TaskUpsertDto dto, ProjectTrackerDbContext db, CurrentUserService currentUser, ProjectMetricsService metrics, ProjectAuditService audit, MentionNotificationService notifications, CancellationToken cancellationToken) =>
{
    var project = await db.Projects.Include(project => project.Tasks).ThenInclude(task => task.OvertimeDays).FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }

    if (IsArchived(project))
    {
        return Results.Conflict("Completed projects are read-only. Make the project active before editing.");
    }
    if (dto.ProjectVersion != project.Version)
    {
        return ConcurrencyConflict("Project", project.Id);
    }
    if (FindDeniedTaskPermission(null, dto, currentUser) is not null)
    {
        return Results.Forbid();
    }

    var previousSequences = project.Tasks.ToDictionary(candidate => candidate.Id, candidate => candidate.Sequence);
    var task = ApplyTaskDto(new ProjectTask { ProjectId = projectId, Project = project }, dto);
    project.Tasks.Add(task);
    var desiredPosition = dto.Sequence > 0 ? dto.Sequence : project.Tasks.Count;
    ResequenceTasks(project, task, desiredPosition);
    BumpSequenceVersions(project.Tasks, previousSequences, task.Id);
    if (ValidateProjectDependencies(project) is { } dependencyError)
    {
        return Results.BadRequest(dependencyError);
    }
    project.Version++;
    project.UpdatedAt = DateTimeOffset.UtcNow;
    await EnsurePhaseAsync(db, task.Phase, cancellationToken);
    await metrics.RefreshProjectAsync(db, project, cancellationToken, recalculateDates: true);
    audit.Record(
        db,
        project,
        "OperationAdded",
        $"Added operation {task.Sequence}: {task.Title}",
        ProjectAuditService.CaptureTask(task)
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .Select(field => new ProjectAuditChange(field.Key, null, field.Value))
            .ToList());
    IReadOnlyList<UserNotification> mentionNotifications = [];
    if (!string.IsNullOrWhiteSpace(task.Notes))
    {
        mentionNotifications = await notifications.AddForOperationNoteAsync(
            db,
            task,
            project.ProgramName,
            task.Notes,
            null,
            currentUser.AccountName,
            currentUser.DisplayName,
            cancellationToken);
    }
    await db.SaveChangesAsync(cancellationToken);
    notifications.DispatchAfterPersistence(mentionNotifications);
    return Results.Created($"/api/projects/{projectId}", ToTaskDto(task));
}).RequireAuthorization("TaskCreate");

api.MapPut("/tasks/{taskId:int}", async (int taskId, TaskUpsertDto dto, ProjectTrackerDbContext db, CurrentUserService currentUser, ProjectMetricsService metrics, ProjectAuditService audit, MentionNotificationService notifications, CancellationToken cancellationToken) =>
{
    var task = await db.Tasks
        .Include(task => task.OvertimeDays)
        .Include(task => task.Project).ThenInclude(project => project.Tasks).ThenInclude(projectTask => projectTask.OvertimeDays)
        .FirstOrDefaultAsync(task => task.Id == taskId, cancellationToken);
    if (task is null)
    {
        return Results.NotFound();
    }

    if (IsArchived(task.Project))
    {
        return Results.Conflict("Completed projects are read-only. Make the project active before editing.");
    }
    if (dto.Version != task.Version)
    {
        return ConcurrencyConflict("Operation", task.Id);
    }
    if (dto.ProjectVersion != task.Project.Version)
    {
        return ConcurrencyConflict("Project", task.Project.Id);
    }
    if (FindDeniedTaskPermission(task, dto, currentUser) is not null)
    {
        return Results.Forbid();
    }

    var before = ProjectAuditService.CaptureTask(task);
    var previousNote = task.Notes;
    var previousSequences = task.Project.Tasks.ToDictionary(candidate => candidate.Id, candidate => candidate.Sequence);
    ApplyTaskDto(task, dto);
    task.Version++;
    ResequenceTasks(task.Project, task, dto.Sequence);
    BumpSequenceVersions(task.Project.Tasks, previousSequences, task.Id);
    if (ValidateProjectDependencies(task.Project) is { } dependencyError)
    {
        return Results.BadRequest(dependencyError);
    }
    task.Project.Version++;
    task.Project.UpdatedAt = DateTimeOffset.UtcNow;
    await EnsurePhaseAsync(db, task.Phase, cancellationToken);
    await metrics.RefreshProjectAsync(db, task.Project, cancellationToken, recalculateDates: true);
    var changes = ProjectAuditService.Diff(before, ProjectAuditService.CaptureTask(task));
    if (changes.Count > 0)
    {
        audit.Record(db, task.Project, "OperationUpdated", $"Updated operation {task.Sequence}: {task.Title}", changes, task.Id);
    }
    IReadOnlyList<UserNotification> mentionNotifications = [];
    if (!string.Equals(previousNote, task.Notes, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(task.Notes))
    {
        mentionNotifications = await notifications.AddForOperationNoteAsync(
            db,
            task,
            task.Project.ProgramName,
            task.Notes,
            previousNote,
            currentUser.AccountName,
            currentUser.DisplayName,
            cancellationToken);
    }
    await db.SaveChangesAsync(cancellationToken);
    notifications.DispatchAfterPersistence(mentionNotifications);
    return Results.Ok(ToTaskDto(task));
}).RequireAuthorization(ProjectTrackerAccessAuthorization.PolicyName);

api.MapDelete("/tasks/{taskId:int}", async (int taskId, long version, long projectVersion, bool? detachDependents, ProjectTrackerDbContext db, ProjectMetricsService metrics, ProjectAuditService audit, CancellationToken cancellationToken) =>
{
    var task = await db.Tasks
        .Include(task => task.Project).ThenInclude(project => project.Tasks).ThenInclude(projectTask => projectTask.OvertimeDays)
        .FirstOrDefaultAsync(task => task.Id == taskId, cancellationToken);
    if (task is null)
    {
        return Results.NotFound();
    }

    if (IsArchived(task.Project))
    {
        return Results.Conflict("Completed projects are read-only. Make the project active before editing.");
    }
    if (version != task.Version)
    {
        return ConcurrencyConflict("Operation", task.Id);
    }
    if (projectVersion != task.Project.Version)
    {
        return ConcurrencyConflict("Project", task.Project.Id);
    }

    var project = task.Project;
    var dependents = project.Tasks
        .Where(candidate => candidate.DependencyTaskId == task.Id)
        .OrderBy(candidate => candidate.Sequence)
        .ToList();
    if (dependents.Count > 0 && detachDependents != true)
    {
        return Results.Conflict(new OperationDependencyConflictDto(
            "OperationHasDependents",
            $"This operation is used as a dependency by {dependents.Count} later operation{(dependents.Count == 1 ? string.Empty : "s")}.",
            task.Id,
            dependents.Select(candidate => new OperationDependentDto(candidate.Id, candidate.Sequence, candidate.Title)).ToList()));
    }

    var previousSequences = project.Tasks.ToDictionary(candidate => candidate.Id, candidate => candidate.Sequence);
    var deletedSequence = task.Sequence;
    var deletedTitle = task.Title;
    var deletedValues = ProjectAuditService.CaptureTask(task);
    foreach (var dependent in dependents)
    {
        dependent.DependencyTaskId = null;
        dependent.Version++;
        dependent.UpdatedAt = DateTimeOffset.UtcNow;
    }
    project.Tasks.Remove(task);
    db.Tasks.Remove(task);
    RenumberTasks(project);
    BumpSequenceVersions(project.Tasks, previousSequences);
    project.Version++;
    project.UpdatedAt = DateTimeOffset.UtcNow;
    await metrics.RefreshProjectAsync(db, project, cancellationToken, recalculateDates: true);
    audit.Record(
        db,
        project,
        "OperationDeleted",
        $"Deleted operation {deletedSequence}: {deletedTitle}",
        deletedValues
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .Select(field => new ProjectAuditChange(field.Key, field.Value, null))
            .ToList(),
        taskId);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("TaskDelete");

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
    await db.SaveChangesAsync(cancellationToken);
    await RefreshAllProjectsAsync(db, metrics, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/holidays/{holiday.Id}", new HolidayDto(holiday.Id, holiday.Date, holiday.Name));
}).RequireAuthorization("ManageHolidays");

api.MapPut("/holidays/{id:int}", async (int id, HolidayUpsertDto dto, ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken) =>
{
    var holiday = await db.Holidays.FindAsync([id], cancellationToken);
    if (holiday is null)
    {
        return Results.NotFound();
    }

    holiday.Date = dto.Date;
    holiday.Name = dto.Name.Trim();
    await db.SaveChangesAsync(cancellationToken);
    await RefreshAllProjectsAsync(db, metrics, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new HolidayDto(holiday.Id, holiday.Date, holiday.Name));
}).RequireAuthorization("ManageHolidays");

api.MapDelete("/holidays/{id:int}", async (int id, ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken) =>
{
    var holiday = await db.Holidays.FindAsync([id], cancellationToken);
    if (holiday is null)
    {
        return Results.NotFound();
    }

    db.Holidays.Remove(holiday);
    await db.SaveChangesAsync(cancellationToken);
    await RefreshAllProjectsAsync(db, metrics, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("ManageHolidays");

api.MapGet("/work-centers", async (ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    return await db.WorkCenters.OrderBy(workCenter => workCenter.Name)
        .Select(workCenter => new WorkCenterDto(workCenter.Id, workCenter.Name))
        .ToListAsync(cancellationToken);
});

api.MapPost("/work-centers", async (WorkCenterUpsertDto dto, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    var name = dto.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
        throw new BadHttpRequestException("Work center name is required.");
    }

    var workCenter = new WorkCenter { Name = name };
    db.WorkCenters.Add(workCenter);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/work-centers/{workCenter.Id}", new WorkCenterDto(workCenter.Id, workCenter.Name));
}).RequireAuthorization("ManageWorkCenters");

api.MapPut("/work-centers/{id:int}", async (int id, WorkCenterUpsertDto dto, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    var workCenter = await db.WorkCenters.FindAsync([id], cancellationToken);
    if (workCenter is null)
    {
        return Results.NotFound();
    }

    var name = dto.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
        throw new BadHttpRequestException("Work center name is required.");
    }

    workCenter.Name = name;
    workCenter.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new WorkCenterDto(workCenter.Id, workCenter.Name));
}).RequireAuthorization("ManageWorkCenters");

api.MapDelete("/work-centers/{id:int}", async (int id, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    var workCenter = await db.WorkCenters.FindAsync([id], cancellationToken);
    if (workCenter is null)
    {
        return Results.NotFound();
    }

    db.WorkCenters.Remove(workCenter);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("ManageWorkCenters");

api.MapGet("/settings/work-calendar", async (ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
{
    var settings = await GetOrCreateScheduleSettingsAsync(db, cancellationToken);
    return new ScheduleSettingsDto(settings.GetWorkingDays().OrderBy(day => ((int)day + 6) % 7).ToList(), settings.UpdatedAt);
});

api.MapPut("/settings/work-calendar", async (ScheduleSettingsUpsertDto dto, ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken) =>
{
    var days = dto.WorkingDays.Distinct().ToList();
    if (days.Count == 0)
    {
        return Results.BadRequest("Select at least one company workday.");
    }

    var settings = await GetOrCreateScheduleSettingsAsync(db, cancellationToken);
    settings.WorkingDaysMask = ScheduleSettings.ToMask(days);
    settings.UpdatedAt = DateTimeOffset.UtcNow;
    await RefreshAllProjectsAsync(db, metrics, cancellationToken, recalculateDates: true);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new ScheduleSettingsDto(settings.GetWorkingDays().OrderBy(day => ((int)day + 6) % 7).ToList(), settings.UpdatedAt));
}).RequireAuthorization("ManageCalendar");

app.MapFallbackToFile("index.html");

app.Run();

static void ApplyProjectDto(Project project, ProjectUpsertDto dto, string? salesOrderUrl, string? jobUrl)
{
    if (string.IsNullOrWhiteSpace(dto.ProgramName))
    {
        throw new BadHttpRequestException("Program name is required.");
    }

    project.ProgramName = dto.ProgramName.Trim();
    project.ProgramManager = Clean(dto.ProgramManager);
    project.Engineer = Clean(dto.Engineer);
    project.CustomerName = Clean(dto.CustomerName);
    project.SalesOrderNumber = Clean(dto.SalesOrderNumber);
    project.SalesOrderUrl = salesOrderUrl;
    project.JobNumber = Clean(dto.JobNumber);
    project.JobUrl = jobUrl;
    ProjectImportCompletion.Refresh(project);
    project.UpdatedAt = DateTimeOffset.UtcNow;
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
    task.DependencyTaskId = dto.DependencyTaskId;
    task.StartDate = dto.StartDate;
    task.StartDateLocked = dto.StartDateLocked;
    task.OriginalStartDate = dto.OriginalStartDate;
    task.EndDate = dto.EndDate;
    task.OriginalEndDate = dto.OriginalEndDate;
    task.EstimatedDuration = dto.EstimatedDuration;
    task.ActualDuration = dto.ActualDuration;
    task.PercentComplete = Math.Clamp(dto.PercentComplete, 0m, 1m);
    task.PercentCompleteManual = dto.PercentCompleteManual;
    var notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
    if (!string.Equals(task.Notes, notes, StringComparison.Ordinal))
    {
        task.NoteUpdatedAt = notes is null ? null : DateTimeOffset.UtcNow;
    }
    task.Notes = notes;
    task.OvertimeDays.Clear();
    foreach (var overtime in dto.OvertimeDays?.GroupBy(day => day.Date).Select(group => group.First()) ?? [])
    {
        task.OvertimeDays.Add(new TaskOvertimeDay
        {
            Date = overtime.Date,
            Note = Clean(overtime.Note)
        });
    }
    task.UpdatedAt = DateTimeOffset.UtcNow;
    return task;
}

static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

static IResult ConcurrencyConflict(string resourceType, int resourceId) => Results.Conflict(
    new ConcurrencyConflictDto(
        "ConcurrencyConflict",
        $"This {resourceType.ToLowerInvariant()} was changed by another user. Reload the latest version before saving again.",
        resourceType,
        resourceId));

static void BumpSequenceVersions(
    IEnumerable<ProjectTask> tasks,
    IReadOnlyDictionary<int, int> previousSequences,
    int? alreadyUpdatedTaskId = null)
{
    foreach (var task in tasks.Where(task =>
                 task.Id != alreadyUpdatedTaskId
                 && previousSequences.TryGetValue(task.Id, out var previousSequence)
                 && previousSequence != task.Sequence))
    {
        task.Version++;
        task.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

static void BumpPriorityVersions(
    IEnumerable<Project> projects,
    IReadOnlyDictionary<int, int?> previousPriorities)
{
    foreach (var project in projects.Where(project =>
                 previousPriorities.TryGetValue(project.Id, out var previousPriority)
                 && previousPriority != project.PriorityRank))
    {
        project.Version++;
        project.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

static bool IsArchived(Project project) => project.CompletedOn is not null || project.Status == ProjectStatus.Complete;

static void NormalizeProjectPriorities(IReadOnlyCollection<Project> projects)
{
    var active = projects
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

    foreach (var project in projects.Where(project => project.Status == ProjectStatus.Complete))
    {
        project.PriorityRank = null;
    }
}

static string? ValidateTaskDependency(Project project, ProjectTask task)
{
    if (task.DependencyTaskId is null)
    {
        return null;
    }

    var dependency = project.Tasks.FirstOrDefault(candidate => candidate.Id == task.DependencyTaskId.Value);
    if (dependency is null)
    {
        return "The selected dependency does not belong to this project or no longer exists.";
    }

    if (dependency.Id == task.Id)
    {
        return "An operation cannot depend on itself.";
    }

    return dependency.Sequence >= task.Sequence
        ? "An operation can only depend on an earlier operation in the schedule."
        : null;
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

static async Task RefreshAllProjectsAsync(ProjectTrackerDbContext db, ProjectMetricsService metrics, CancellationToken cancellationToken, bool recalculateDates = true)
{
    var projects = await db.Projects.Include(project => project.Tasks).ThenInclude(task => task.OvertimeDays).ToListAsync(cancellationToken);
    var calendar = await LoadScheduleCalendarAsync(db, cancellationToken);
    foreach (var project in projects.Where(project => project.Status != ProjectStatus.Complete))
    {
        metrics.RefreshProject(project, calendar, DateOnly.FromDateTime(DateTime.Today), recalculateDates);
    }
}

static async Task<ScheduleCalendar> LoadScheduleCalendarAsync(ProjectTrackerDbContext db, CancellationToken cancellationToken)
{
    var settings = await GetOrCreateScheduleSettingsAsync(db, cancellationToken);
    var holidays = (await db.Holidays.Select(holiday => holiday.Date).ToListAsync(cancellationToken)).ToHashSet();
    return new ScheduleCalendar(settings.GetWorkingDays(), holidays);
}

static async Task<ScheduleSettings> GetOrCreateScheduleSettingsAsync(ProjectTrackerDbContext db, CancellationToken cancellationToken)
{
    var settings = await db.ScheduleSettings.FindAsync([ScheduleSettings.SingletonId], cancellationToken);
    if (settings is not null)
    {
        return settings;
    }

    settings = new ScheduleSettings();
    db.ScheduleSettings.Add(settings);
    return settings;
}

static async Task InitializeDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ProjectTrackerDbContext>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var provider = configuration["Database:Provider"] ?? "SqlServer";
    var autoMigrate = configuration.GetValue("Database:AutoMigrate", true);
    var isSqlite = string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase);

    if (isSqlite)
    {
        await SqliteCompatibility.RepairLegacySchemaAsync(db, cancellationToken: default);
    }

    if (autoMigrate && string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        await db.Database.MigrateAsync();
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
        if (isSqlite)
        {
            await SqliteCompatibility.EnsureBooleanColumnAsync(db, "Tasks", "StartDateLocked", cancellationToken: default);
            await SqliteCompatibility.EnsureBooleanColumnAsync(db, "Tasks", "PercentCompleteManual", cancellationToken: default);
            await SqliteCompatibility.EnsureNullableIntegerColumnAsync(db, "Tasks", "DependencyTaskId", cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "Tasks", "NoteUpdatedAt", cancellationToken: default);
            await SqliteCompatibility.EnsureLongColumnAsync(db, "Projects", "Version", cancellationToken: default);
            await SqliteCompatibility.EnsureLongColumnAsync(db, "Tasks", "Version", cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "Projects", "CustomerName", cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "Projects", "SalesOrderNumber", cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "Projects", "SalesOrderUrl", cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "Projects", "JobNumber", cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "Projects", "JobUrl", cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "Projects", "CompletedOn", cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "Projects", "DeletedAt", cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "Projects", "DeletedByAccountName", cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "Projects", "DeletedByDisplayName", cancellationToken: default);
            await SqliteCompatibility.EnsureNullableIntegerColumnAsync(db, "Projects", "PriorityRank", cancellationToken: default);
            await SqliteCompatibility.EnsureBooleanColumnAsync(db, "Projects", "ImportNeedsCompletion", cancellationToken: default);
            await SqliteCompatibility.EnsureBooleanColumnAsync(db, "Users", "IsActive", cancellationToken: default);
            await SqliteCompatibility.EnsureLegacyTablesAsync(db, cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "UserNotifications", "ScheduledDate", cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "UserNotifications", "SnoozedUntil", cancellationToken: default);
            await SqliteCompatibility.EnsureTextColumnAsync(db, "UserNotifications", "RespondedAt", cancellationToken: default);
            await SqliteCompatibility.EnsureOperationScheduleReminderIndexAsync(db, cancellationToken: default);
            await SqliteCompatibility.EnsureAccessControlTablesAsync(db, cancellationToken: default);
            await SqliteCompatibility.EnsureLocalPermissionSeedAsync(db, cancellationToken: default);
        }
    }

    var accessSeeder = scope.ServiceProvider.GetRequiredService<AccessControlSeeder>();
    await accessSeeder.SeedAsync(db, configuration);
    await ProjectNoteService.BackfillUpdatedAtAsync(db, cancellationToken: default);
    await BackfillCompletedDatesAsync(db, cancellationToken: default);
    NormalizeProjectPriorities(await db.Projects.ToListAsync());

    await GetOrCreateScheduleSettingsAsync(db, cancellationToken: default);
    await db.SaveChangesAsync();

    var autoImport = configuration.GetValue("Import:AutoImportOnEmpty", app.Environment.IsDevelopment());
    if (autoImport && !await db.Projects.AnyAsync())
    {
        var importer = scope.ServiceProvider.GetRequiredService<WorkbookImportService>();
        var workbookPath = WorkbookPathResolver.Resolve(null, configuration, app.Environment);
        if (File.Exists(workbookPath))
        {
            await importer.ImportAsync(db, workbookPath, replaceExisting: true);
        }
    }

    if (app.Environment.IsDevelopment() && configuration.GetValue("DemoData:Enabled", false))
    {
        var notifications = scope.ServiceProvider.GetRequiredService<MentionNotificationService>();
        await DemoDataSeeder.SeedAsync(db, notifications, cancellationToken: default);
    }

    await SeedWorkCentersFromTasksAsync(db, cancellationToken: default);
}

static async Task BackfillCompletedDatesAsync(ProjectTrackerDbContext db, CancellationToken cancellationToken)
{
    var completedProjects = await db.Projects
        .Include(project => project.Tasks)
        .Where(project => project.Status == ProjectStatus.Complete && project.CompletedOn == null)
        .ToListAsync(cancellationToken);

    foreach (var project in completedProjects)
    {
        project.CompletedOn = project.Tasks
            .Select(task => task.EndDate)
            .Where(date => date is not null)
            .Max()
            ?? DateOnly.FromDateTime(project.UpdatedAt.LocalDateTime);
    }

    if (completedProjects.Count > 0)
    {
        await db.SaveChangesAsync(cancellationToken);
    }
}

static string? FindDeniedProjectPermission(
    Project project,
    ProjectUpsertDto dto,
    string? salesOrderUrl,
    string? jobUrl,
    CurrentUserService currentUser)
{
    if (!string.Equals(project.ProgramName, dto.ProgramName?.Trim(), StringComparison.Ordinal)
        && !currentUser.HasPermission(ApplicationPermissions.ProjectEditProgramName))
    {
        return ApplicationPermissions.ProjectEditProgramName;
    }

    if (!string.Equals(project.ProgramManager, Clean(dto.ProgramManager), StringComparison.Ordinal)
        && !currentUser.HasPermission(ApplicationPermissions.ProjectEditProgramManager))
    {
        return ApplicationPermissions.ProjectEditProgramManager;
    }

    if (!string.Equals(project.Engineer, Clean(dto.Engineer), StringComparison.Ordinal)
        && !currentUser.HasPermission(ApplicationPermissions.ProjectEditEngineer))
    {
        return ApplicationPermissions.ProjectEditEngineer;
    }

    if (!string.Equals(project.CustomerName, Clean(dto.CustomerName), StringComparison.Ordinal)
        && !currentUser.HasPermission(ApplicationPermissions.ProjectEditCustomerName))
    {
        return ApplicationPermissions.ProjectEditCustomerName;
    }

    if (!string.Equals(project.SalesOrderNumber, Clean(dto.SalesOrderNumber), StringComparison.Ordinal)
        && !currentUser.HasPermission(ApplicationPermissions.ProjectEditSalesOrderNumber))
    {
        return ApplicationPermissions.ProjectEditSalesOrderNumber;
    }

    if (!string.Equals(project.JobNumber, Clean(dto.JobNumber), StringComparison.Ordinal)
        && !currentUser.HasPermission(ProjectTrackerPermissions.ProjectEditJobNumber))
    {
        return ProjectTrackerPermissions.ProjectEditJobNumber;
    }

    var deniedExternalLinksPermission = ProjectExternalLinks.FindDeniedEditPermission(
        project,
        salesOrderUrl,
        jobUrl,
        currentUser.HasPermission);
    if (deniedExternalLinksPermission is not null)
    {
        return deniedExternalLinksPermission;
    }

    return null;
}

static string? ValidateProjectDependencies(Project project)
{
    foreach (var task in project.Tasks.OrderBy(candidate => candidate.Sequence))
    {
        if (ValidateTaskDependency(project, task) is { } error)
        {
            return $"Operation {task.Sequence} ({task.Title}) has an invalid dependency. {error}";
        }
    }

    return null;
}

static string? FindDeniedTaskPermission(ProjectTask? task, TaskUpsertDto dto, CurrentUserService currentUser)
{
    if (task is null)
    {
        return currentUser.HasPermission(ApplicationPermissions.TaskCreate) ? null : ApplicationPermissions.TaskCreate;
    }

    if (!string.Equals(task.Title, dto.Title?.Trim(), StringComparison.Ordinal) && !currentUser.HasPermission(ApplicationPermissions.TaskEditTitle)) return ApplicationPermissions.TaskEditTitle;
    if (!string.Equals(task.WorkStation, Clean(dto.WorkStation), StringComparison.Ordinal) && !currentUser.HasPermission(ApplicationPermissions.TaskEditWorkStation)) return ApplicationPermissions.TaskEditWorkStation;
    if (task.DependencyTaskId != dto.DependencyTaskId && !currentUser.HasPermission(ApplicationPermissions.TaskEditDependency)) return ApplicationPermissions.TaskEditDependency;
    if (task.StartDateLocked != dto.StartDateLocked && !currentUser.HasPermission(ApplicationPermissions.TaskEditStartDateLocked)) return ApplicationPermissions.TaskEditStartDateLocked;
    if (task.StartDate != dto.StartDate && !currentUser.HasPermission(ApplicationPermissions.TaskEditStartDate)) return ApplicationPermissions.TaskEditStartDate;
    if (task.EndDate != dto.EndDate && !currentUser.HasPermission(ApplicationPermissions.TaskEditEndDate)) return ApplicationPermissions.TaskEditEndDate;
    if (task.OriginalStartDate != dto.OriginalStartDate && !currentUser.HasPermission(ApplicationPermissions.TaskEditOriginalStartDate)) return ApplicationPermissions.TaskEditOriginalStartDate;
    if (task.OriginalEndDate != dto.OriginalEndDate && !currentUser.HasPermission(ApplicationPermissions.TaskEditOriginalEndDate)) return ApplicationPermissions.TaskEditOriginalEndDate;
    if (task.EstimatedDuration != dto.EstimatedDuration && !currentUser.HasPermission(ApplicationPermissions.TaskEditEstimatedDuration)) return ApplicationPermissions.TaskEditEstimatedDuration;
    if (task.ActualDuration != dto.ActualDuration && !currentUser.HasPermission(ApplicationPermissions.TaskEditActualDuration)) return ApplicationPermissions.TaskEditActualDuration;
    if (task.Sequence != dto.Sequence && !currentUser.HasPermission(ApplicationPermissions.TaskReorder)) return ApplicationPermissions.TaskReorder;
    if ((task.PercentComplete != Math.Clamp(dto.PercentComplete, 0m, 1m) || task.PercentCompleteManual != dto.PercentCompleteManual)
        && !currentUser.HasPermission(ApplicationPermissions.TaskEditPercentComplete)) return ApplicationPermissions.TaskEditPercentComplete;
    if (!string.Equals(task.Notes, Clean(dto.Notes), StringComparison.Ordinal) && !currentUser.HasPermission(ApplicationPermissions.TaskEditNotes)) return ApplicationPermissions.TaskEditNotes;
    if (OvertimeDaysChanged(task, dto) && !currentUser.HasPermission(ApplicationPermissions.TaskEditOvertimeDays)) return ApplicationPermissions.TaskEditOvertimeDays;

    return null;
}

static bool OvertimeDaysChanged(ProjectTask task, TaskUpsertDto dto)
{
    var current = task.OvertimeDays
        .Select(day => $"{day.Date:yyyy-MM-dd}|{Clean(day.Note) ?? string.Empty}")
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
    var proposed = (dto.OvertimeDays ?? [])
        .Select(day => $"{day.Date:yyyy-MM-dd}|{Clean(day.Note) ?? string.Empty}")
        .Distinct(StringComparer.Ordinal)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
    return !current.SequenceEqual(proposed, StringComparer.Ordinal);
}

static async Task SeedWorkCentersFromTasksAsync(ProjectTrackerDbContext db, CancellationToken cancellationToken)
{
    var existing = await db.WorkCenters.Select(workCenter => workCenter.Name).ToListAsync(cancellationToken);
    var known = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var names = await db.Tasks
        .Where(task => task.WorkStation != null && task.WorkStation != "")
        .Select(task => task.WorkStation!)
        .Distinct()
        .ToListAsync(cancellationToken);

    foreach (var name in names.Select(name => name.Trim()).Where(name => name.Length > 0 && !known.Contains(name)))
    {
        db.WorkCenters.Add(new WorkCenter { Name = name });
        known.Add(name);
    }

    await db.SaveChangesAsync(cancellationToken);
}

public partial class Program;
