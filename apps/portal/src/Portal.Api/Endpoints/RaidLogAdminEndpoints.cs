using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;
using Portal.Api.Dtos;
using Portal.Api.Services;
using SonAero.Platform.Security;

namespace Portal.Api.Endpoints;

public static class RaidLogAdminEndpoints
{
    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
        { "Risk", "Action", "Issue", "Decision" };
    private static readonly HashSet<string> Priorities = new(StringComparer.OrdinalIgnoreCase)
        { "Critical", "High", "Normal", "Low" };
    private static readonly TimeSpan WorkSessionTimeout = TimeSpan.FromMinutes(3);

    public static void MapRaidLogAdminEndpoints(this RouteGroupBuilder api)
    {
        var routes = api.MapGroup("/admin/raid-log").RequireAuthorization();
        routes.MapGet("", GetOverviewAsync);
        routes.MapPost("/groups", CreateGroupAsync);
        routes.MapPut("/groups/{id:int}", UpdateGroupAsync);
        routes.MapPost("/items", CreateItemAsync);
        routes.MapPut("/items/{id:int}", UpdateItemAsync);
        routes.MapPost("/items/{id:int}/completion", SetCompletionAsync);
        routes.MapPost("/items/{id:int}/notes", AddNoteAsync);
        routes.MapPost("/items/{id:int}/work/start", StartWorkAsync);
        routes.MapPost("/items/{id:int}/work/stop", StopWorkAsync);
        routes.MapPost("/work/heartbeat", HeartbeatWorkAsync);
    }

    private static async Task<IResult> GetOverviewAsync(
        PortalUserService users,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync(users, cancellationToken)) return AdministratorRequired();
        await CleanupStaleWorkSessionsAsync(db, cancellationToken);
        return Results.Ok(await BuildOverviewAsync(db, cancellationToken));
    }

    private static async Task<IResult> CreateGroupAsync(
        [FromBody] RaidLogGroupCreateDto dto,
        PortalUserService users,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var actor = await AdministratorAsync(users, cancellationToken);
        if (actor is null) return AdministratorRequired();
        var name = Clean(dto.Name);
        var normalizedName = NormalizeGroupName(name);
        var description = CleanOptional(dto.Description);
        if (name.Length is < 1 or > 120) return Invalid("Group name is required and must be 120 characters or fewer.");
        if (description?.Length > 500) return Invalid("Group description must be 500 characters or fewer.");
        if (await db.RaidLogGroups.AnyAsync(group => group.NormalizedName == normalizedName, cancellationToken))
            return Results.Conflict(new { detail = "A RAID Log group with that name already exists." });

        var now = DateTimeOffset.UtcNow;
        var maxOrder = await db.RaidLogGroups.Select(group => (int?)group.SortOrder).MaxAsync(cancellationToken) ?? -1;
        var group = new RaidLogGroupRecord
        {
            Name = name,
            NormalizedName = normalizedName,
            Description = description,
            SortOrder = maxOrder + 1,
            CreatedAt = now,
            CreatedBy = actor.AccountName,
            UpdatedAt = now,
            UpdatedBy = actor.AccountName,
            Version = 1,
        };
        db.RaidLogGroups.Add(group);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { return DuplicateGroup(); }
        return Results.Created($"/api/admin/raid-log/groups/{group.Id}", new { group.Id });
    }

    private static async Task<IResult> UpdateGroupAsync(
        int id,
        [FromBody] RaidLogGroupUpdateDto dto,
        PortalUserService users,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var actor = await AdministratorAsync(users, cancellationToken);
        if (actor is null) return AdministratorRequired();
        var group = await db.RaidLogGroups.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (group is null) return Results.NotFound();
        if (group.Version != dto.Version) return Stale();
        var name = Clean(dto.Name);
        var normalizedName = NormalizeGroupName(name);
        var description = CleanOptional(dto.Description);
        if (name.Length is < 1 or > 120) return Invalid("Group name is required and must be 120 characters or fewer.");
        if (description?.Length > 500) return Invalid("Group description must be 500 characters or fewer.");
        if (await db.RaidLogGroups.AnyAsync(candidate => candidate.Id != id && candidate.NormalizedName == normalizedName, cancellationToken))
            return Results.Conflict(new { detail = "A RAID Log group with that name already exists." });

        group.Name = name;
        group.NormalizedName = normalizedName;
        group.Description = description;
        group.SortOrder = Math.Max(0, dto.SortOrder);
        group.UpdatedAt = DateTimeOffset.UtcNow;
        group.UpdatedBy = actor.AccountName;
        group.Version++;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Stale(); }
        catch (DbUpdateException) { return DuplicateGroup(); }
        return Results.NoContent();
    }

    private static async Task<IResult> CreateItemAsync(
        [FromBody] RaidLogItemCreateDto dto,
        PortalUserService users,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var actor = await AdministratorAsync(users, cancellationToken);
        if (actor is null) return AdministratorRequired();
        var validation = await ValidateItemAsync(dto.GroupId, dto.Title, dto.Description, dto.Kind, dto.Priority, dto.AssignedToUserId, db, cancellationToken);
        if (validation.Error is not null) return Invalid(validation.Error);
        var now = DateTimeOffset.UtcNow;
        var item = new RaidLogItemRecord
        {
            GroupId = dto.GroupId,
            Title = validation.Title,
            Description = validation.Description,
            Kind = validation.Kind,
            Priority = validation.Priority,
            AssignedToUserId = dto.AssignedToUserId,
            CreatedAt = now,
            CreatedBy = actor.AccountName,
            UpdatedAt = now,
            UpdatedBy = actor.AccountName,
            Version = 1,
        };
        item.Activity.Add(Activity(item, "created", $"Created {item.Kind.ToLowerInvariant()}.", actor.AccountName, now));
        db.RaidLogItems.Add(item);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Stale(); }
        return Results.Created($"/api/admin/raid-log/items/{item.Id}", new { item.Id });
    }

    private static async Task<IResult> UpdateItemAsync(
        int id,
        [FromBody] RaidLogItemUpdateDto dto,
        PortalUserService users,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var actor = await AdministratorAsync(users, cancellationToken);
        if (actor is null) return AdministratorRequired();
        var item = await db.RaidLogItems.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null) return Results.NotFound();
        if (item.Version != dto.Version) return Stale();
        var validation = await ValidateItemAsync(dto.GroupId, dto.Title, dto.Description, dto.Kind, dto.Priority, dto.AssignedToUserId, db, cancellationToken);
        if (validation.Error is not null) return Invalid(validation.Error);

        var changes = new List<string>();
        if (item.GroupId != dto.GroupId) changes.Add("group");
        if (!string.Equals(item.Priority, validation.Priority, StringComparison.Ordinal)) changes.Add("priority");
        if (item.AssignedToUserId != dto.AssignedToUserId) changes.Add("assignment");
        if (!string.Equals(item.Title, validation.Title, StringComparison.Ordinal)
            || !string.Equals(item.Description, validation.Description, StringComparison.Ordinal)
            || !string.Equals(item.Kind, validation.Kind, StringComparison.Ordinal)) changes.Add("details");
        var now = DateTimeOffset.UtcNow;
        item.GroupId = dto.GroupId;
        item.Title = validation.Title;
        item.Description = validation.Description;
        item.Kind = validation.Kind;
        item.Priority = validation.Priority;
        item.AssignedToUserId = dto.AssignedToUserId;
        item.UpdatedAt = now;
        item.UpdatedBy = actor.AccountName;
        item.Version++;
        item.Activity.Add(Activity(item, "updated", changes.Count == 0 ? "Saved without field changes." : $"Updated {string.Join(", ", changes)}.", actor.AccountName, now));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Stale(); }
        return Results.NoContent();
    }

    internal static async Task<IResult> SetCompletionAsync(
        int id,
        [FromBody] RaidLogCompletionDto dto,
        PortalUserService users,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var actor = await AdministratorAsync(users, cancellationToken);
        if (actor is null) return AdministratorRequired();
        await CleanupStaleWorkSessionsAsync(db, cancellationToken);
        var item = await db.RaidLogItems.Include(candidate => candidate.Activity)
            .Include(candidate => candidate.WorkSessions)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null) return Results.NotFound();
        if (item.Version != dto.Version) return Stale();
        if ((item.CompletedAt is not null) == dto.Completed) return Results.NoContent();
        var now = DateTimeOffset.UtcNow;
        var activeSession = item.WorkSessions.SingleOrDefault(session => session.StoppedAt is null);
        if (dto.Completed && activeSession is not null)
        {
            EndWorkSession(activeSession, now, actor.AccountName, "Task completed.", "completed");
            item.Activity.Add(Activity(item, "work-stopped", "Stopped active work because the task was completed.", actor.AccountName, now));
        }
        item.CompletedAt = dto.Completed ? now : null;
        item.CompletedBy = dto.Completed ? actor.AccountName : null;
        item.UpdatedAt = now;
        item.UpdatedBy = actor.AccountName;
        item.Version++;
        item.Activity.Add(Activity(item, dto.Completed ? "completed" : "reopened", dto.Completed ? "Marked complete." : "Reopened.", actor.AccountName, now));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Stale(); }
        return Results.NoContent();
    }

    internal static async Task<IResult> StartWorkAsync(
        int id,
        [FromBody] RaidLogWorkStartDto dto,
        PortalUserService users,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var actor = await AdministratorAsync(users, cancellationToken);
        if (actor is null) return AdministratorRequired();
        var note = CleanOptional(dto.Note);
        if (note?.Length > 2000) return Invalid("Work note must be 2,000 characters or fewer.");

        await CleanupStaleWorkSessionsAsync(db, cancellationToken);
        var item = await db.RaidLogItems
            .Include(candidate => candidate.Activity)
            .Include(candidate => candidate.WorkSessions)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null) return Results.NotFound();
        if (item.Version != dto.Version) return Stale();
        if (item.CompletedAt is not null) return Results.Conflict(new { detail = "Reopen this RAID item before starting work." });

        var activeForItem = item.WorkSessions.SingleOrDefault(session => session.StoppedAt is null);
        if (activeForItem is not null)
        {
            var detail = string.Equals(activeForItem.StartedBy, actor.AccountName, StringComparison.OrdinalIgnoreCase)
                ? "You are already working on this RAID item."
                : $"{activeForItem.StartedBy} is already working on this RAID item.";
            return Results.Conflict(new { detail });
        }

        var actorUser = (await db.Users.Where(user => user.IsActive).ToListAsync(cancellationToken))
            .SingleOrDefault(user => WindowsAccountNames.Equals(user.AccountName, actor.AccountName));
        if (actorUser is null) return AdministratorRequired();

        var now = DateTimeOffset.UtcNow;
        var previousSession = await db.RaidLogWorkSessions
            .Include(session => session.Item).ThenInclude(previousItem => previousItem.Activity)
            .SingleOrDefaultAsync(session => session.StoppedAt == null && session.StartedBy == actor.AccountName, cancellationToken);
        if (previousSession is not null)
        {
            EndWorkSession(previousSession, now, actor.AccountName, "Switched to another RAID item.", "switched-task");
            previousSession.Item.UpdatedAt = now;
            previousSession.Item.UpdatedBy = actor.AccountName;
            previousSession.Item.Version++;
            previousSession.Item.Activity.Add(Activity(
                previousSession.Item,
                "work-stopped",
                "Stopped work after switching to another RAID item.",
                actor.AccountName,
                now));
        }

        if (item.AssignedToUserId != actorUser.Id)
        {
            item.AssignedToUserId = actorUser.Id;
            item.Activity.Add(Activity(item, "assigned", $"Picked up by {DisplayName(actorUser)}.", actor.AccountName, now));
        }

        var session = new RaidLogWorkSessionRecord
        {
            Item = item,
            StartedAt = now,
            StartedBy = actor.AccountName,
            StartNote = note,
            LastHeartbeatAt = now,
        };
        item.WorkSessions.Add(session);
        item.UpdatedAt = now;
        item.UpdatedBy = actor.AccountName;
        item.Version++;
        item.Activity.Add(Activity(item, "work-started", WorkSummary("Started work", note), actor.AccountName, now));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Stale(); }
        catch (DbUpdateException) { return Results.Conflict(new { detail = "This task or user already has active work. Refresh and try again." }); }
        return Results.Ok(new { workSessionId = session.Id });
    }

    internal static async Task<IResult> StopWorkAsync(
        int id,
        [FromBody] RaidLogWorkStopDto dto,
        PortalUserService users,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var actor = await AdministratorAsync(users, cancellationToken);
        if (actor is null) return AdministratorRequired();
        var note = CleanOptional(dto.Note);
        if (note?.Length > 2000) return Invalid("Progress note must be 2,000 characters or fewer.");

        await CleanupStaleWorkSessionsAsync(db, cancellationToken);
        var item = await db.RaidLogItems
            .Include(candidate => candidate.Activity)
            .Include(candidate => candidate.WorkSessions)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null) return Results.NotFound();
        if (item.Version != dto.Version) return Stale();
        var session = item.WorkSessions.SingleOrDefault(candidate => candidate.StoppedAt is null);
        if (session is null) return Results.Conflict(new { detail = "This RAID item does not have an active work session." });
        if (!string.Equals(session.StartedBy, actor.AccountName, StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { detail = "Only the person currently working on this item can stop their work session." });

        var now = DateTimeOffset.UtcNow;
        EndWorkSession(session, now, actor.AccountName, note, "manual");
        item.UpdatedAt = now;
        item.UpdatedBy = actor.AccountName;
        item.Version++;
        item.Activity.Add(Activity(item, "work-stopped", WorkSummary("Stopped work", note), actor.AccountName, now));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Stale(); }
        return Results.NoContent();
    }

    private static async Task<IResult> HeartbeatWorkAsync(
        PortalUserService users,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var actor = await AdministratorAsync(users, cancellationToken);
        if (actor is null) return AdministratorRequired();
        var now = DateTimeOffset.UtcNow;
        await db.RaidLogWorkSessions
            .Where(session => session.StoppedAt == null
                && session.StartedBy == actor.AccountName)
            .ExecuteUpdateAsync(update => update.SetProperty(session => session.LastHeartbeatAt, now), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> AddNoteAsync(
        int id,
        [FromBody] RaidLogNoteCreateDto dto,
        PortalUserService users,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var actor = await AdministratorAsync(users, cancellationToken);
        if (actor is null) return AdministratorRequired();
        var item = await db.RaidLogItems.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null) return Results.NotFound();
        var body = Clean(dto.Body);
        if (body.Length is < 1 or > 4000) return Invalid("Note is required and must be 4,000 characters or fewer.");
        var now = DateTimeOffset.UtcNow;
        item.Notes.Add(new RaidLogNoteRecord { Body = body, CreatedAt = now, CreatedBy = actor.AccountName });
        item.Activity.Add(Activity(item, "note-added", "Added a note.", actor.AccountName, now));
        item.UpdatedAt = now;
        item.UpdatedBy = actor.AccountName;
        item.Version++;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Stale(); }
        return Results.NoContent();
    }

    private static async Task<RaidLogOverviewDto> BuildOverviewAsync(PortalRoleDbContext db, CancellationToken cancellationToken)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken);
        var names = users.ToDictionary(user => user.AccountName, user => DisplayName(user), StringComparer.OrdinalIgnoreCase);
        var admins = users.Where(user => user.IsActive && string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase))
            .OrderBy(user => DisplayName(user), StringComparer.OrdinalIgnoreCase)
            .Select(user => new RaidLogAdminDto(user.Id, user.AccountName, DisplayName(user))).ToList();
        var groups = await db.RaidLogGroups.AsNoTracking().AsSplitQuery()
            .Include(group => group.Items).ThenInclude(item => item.AssignedToUser)
            .Include(group => group.Items).ThenInclude(item => item.Notes)
            .Include(group => group.Items).ThenInclude(item => item.Activity)
            .Include(group => group.Items).ThenInclude(item => item.WorkSessions)
            .OrderBy(group => group.SortOrder).ThenBy(group => group.Name)
            .ToListAsync(cancellationToken);
        return new RaidLogOverviewDto(generatedAt, admins, groups.Select(group => new RaidLogGroupDto(
            group.Id, group.Name, group.Description, group.SortOrder, group.Version,
            group.Items.Select(item => MapItem(item, names, generatedAt))
                .OrderBy(item => PriorityRank(item.Priority)).ThenBy(item => item.CompletedAt is not null).ThenByDescending(item => item.UpdatedAt).ToList())).ToList());
    }

    private static RaidLogItemDto MapItem(
        RaidLogItemRecord item,
        IReadOnlyDictionary<string, string> names,
        DateTimeOffset generatedAt)
    {
        var workSessions = item.WorkSessions
            .OrderByDescending(session => session.StartedAt)
            .Select(session => MapWorkSession(session, names, generatedAt))
            .ToList();
        return new RaidLogItemDto(
            item.Id, item.GroupId, item.Title, item.Description, item.Kind, item.Priority,
            item.AssignedToUserId, item.AssignedToUser is null ? null : DisplayName(item.AssignedToUser),
            item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy,
            item.CompletedAt, item.CompletedBy,
            item.CompletedBy is null ? null : ResolveName(names, item.CompletedBy),
            workSessions.Sum(session => session.DurationSeconds),
            workSessions.SingleOrDefault(session => session.StoppedAt is null), item.Version, workSessions,
            item.Notes.OrderByDescending(note => note.CreatedAt).Select(note => new RaidLogNoteDto(
                note.Id, note.Body, note.CreatedAt, note.CreatedBy, ResolveName(names, note.CreatedBy))).ToList(),
            item.Activity.OrderByDescending(activity => activity.OccurredAt).Select(activity => new RaidLogActivityDto(
                activity.Id, activity.Action, activity.Summary, activity.OccurredAt, activity.Actor, ResolveName(names, activity.Actor))).ToList());
    }

    private static RaidLogWorkSessionDto MapWorkSession(
        RaidLogWorkSessionRecord session,
        IReadOnlyDictionary<string, string> names,
        DateTimeOffset generatedAt)
    {
        var endedAt = session.StoppedAt ?? generatedAt;
        var duration = Math.Max(0, (long)Math.Floor((endedAt - session.StartedAt).TotalSeconds));
        return new RaidLogWorkSessionDto(
            session.Id, session.StartedAt, session.StartedBy, ResolveName(names, session.StartedBy),
            session.StartNote, session.LastHeartbeatAt, session.StoppedAt, session.StoppedBy,
            session.StoppedBy is null ? null : ResolveName(names, session.StoppedBy),
            session.StopNote, session.StopReason, duration);
    }

    private static async Task CleanupStaleWorkSessionsAsync(PortalRoleDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - WorkSessionTimeout;
        var openSessions = await db.RaidLogWorkSessions
            .Include(session => session.Item).ThenInclude(item => item.Activity)
            .Where(session => session.StoppedAt == null)
            .ToListAsync(cancellationToken);
        var staleSessions = openSessions.Where(session => session.LastHeartbeatAt < cutoff).ToList();
        if (staleSessions.Count == 0) return;

        foreach (var session in staleSessions)
        {
            var stoppedAt = session.LastHeartbeatAt < session.StartedAt ? session.StartedAt : session.LastHeartbeatAt;
            EndWorkSession(session, stoppedAt, "System", "Session ended after activity stopped.", "session-ended");
            session.Item.UpdatedAt = stoppedAt;
            session.Item.UpdatedBy = "System";
            session.Item.Version++;
            session.Item.Activity.Add(Activity(
                session.Item,
                "work-stopped",
                "Stopped work at the last confirmed activity after the user left or disconnected.",
                "System",
                stoppedAt));
        }

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
    }

    private static async Task<(string Title, string? Description, string Kind, string Priority, string? Error)> ValidateItemAsync(
        int groupId, string titleValue, string? descriptionValue, string kindValue, string priorityValue,
        int? assignedToUserId, PortalRoleDbContext db, CancellationToken cancellationToken)
    {
        var title = Clean(titleValue);
        var description = CleanOptional(descriptionValue);
        var kind = Canonical(Kinds, kindValue);
        var priority = Canonical(Priorities, priorityValue);
        if (!await db.RaidLogGroups.AnyAsync(group => group.Id == groupId, cancellationToken)) return (title, description, kind, priority, "Choose an existing RAID Log group.");
        if (title.Length is < 1 or > 240) return (title, description, kind, priority, "Title is required and must be 240 characters or fewer.");
        if (description?.Length > 4000) return (title, description, kind, priority, "Details must be 4,000 characters or fewer.");
        if (!Kinds.Contains(kind)) return (title, description, kind, priority, "Choose Risk, Action, Issue, or Decision.");
        if (!Priorities.Contains(priority)) return (title, description, kind, priority, "Choose Critical, High, Normal, or Low priority.");
        if (assignedToUserId is not null)
        {
            var assignee = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == assignedToUserId, cancellationToken);
            if (assignee is null || !assignee.IsActive || !string.Equals(assignee.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase))
                return (title, description, kind, priority, "Tasks can only be assigned to an active Arda administrator.");
        }
        return (title, description, kind, priority, null);
    }

    private static RaidLogActivityRecord Activity(RaidLogItemRecord item, string action, string summary, string actor, DateTimeOffset now) =>
        new() { Item = item, Action = action, Summary = summary, Actor = actor, OccurredAt = now };
    private static void EndWorkSession(RaidLogWorkSessionRecord session, DateTimeOffset stoppedAt, string stoppedBy, string? note, string reason)
    {
        session.LastHeartbeatAt = stoppedAt;
        session.StoppedAt = stoppedAt;
        session.StoppedBy = stoppedBy;
        session.StopNote = note;
        session.StopReason = reason;
    }
    private static string WorkSummary(string action, string? note)
    {
        var summary = note is null ? $"{action}." : $"{action}. {note}";
        return summary.Length <= 500 ? summary : summary[..497] + "...";
    }
    private static int PriorityRank(string priority) => priority switch { "Critical" => 0, "High" => 1, "Normal" => 2, _ => 3 };
    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
    private static string NormalizeGroupName(string value) => value.ToUpperInvariant();
    private static string? CleanOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Canonical(HashSet<string> values, string? value) => values.FirstOrDefault(candidate => string.Equals(candidate, Clean(value), StringComparison.OrdinalIgnoreCase)) ?? Clean(value);
    private static string DisplayName(PortalRoleRecord user) => string.IsNullOrWhiteSpace(user.DisplayName) ? user.AccountName : user.DisplayName;
    private static string ResolveName(IReadOnlyDictionary<string, string> names, string account) => names.TryGetValue(account, out var name) ? name : account;
    private static async Task<bool> IsAdministratorAsync(PortalUserService users, CancellationToken token) => await AdministratorAsync(users, token) is not null;
    private static async Task<Portal.Api.Dtos.MeDto?> AdministratorAsync(PortalUserService users, CancellationToken token)
    {
        var user = await users.CurrentAsync(token);
        return string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase) ? user : null;
    }
    private static IResult Invalid(string detail) => Results.BadRequest(new { detail });
    private static IResult Stale() => Results.Conflict(new { detail = "This RAID Log entry changed in another session. Refresh and try again." });
    private static IResult DuplicateGroup() => Results.Conflict(new { detail = "A RAID Log group with that name already exists." });
    private static IResult AdministratorRequired() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Administrator access required", detail: "The RAID Log is available only to Arda administrators.");
}
