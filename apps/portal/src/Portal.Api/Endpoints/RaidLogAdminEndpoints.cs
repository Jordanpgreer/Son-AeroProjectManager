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
    }

    private static async Task<IResult> GetOverviewAsync(
        PortalUserService users,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync(users, cancellationToken)) return AdministratorRequired();
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

    private static async Task<IResult> SetCompletionAsync(
        int id,
        [FromBody] RaidLogCompletionDto dto,
        PortalUserService users,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var actor = await AdministratorAsync(users, cancellationToken);
        if (actor is null) return AdministratorRequired();
        var item = await db.RaidLogItems.Include(candidate => candidate.Activity)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null) return Results.NotFound();
        if (item.Version != dto.Version) return Stale();
        if ((item.CompletedAt is not null) == dto.Completed) return Results.NoContent();
        var now = DateTimeOffset.UtcNow;
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
        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken);
        var names = users.ToDictionary(user => user.AccountName, user => DisplayName(user), StringComparer.OrdinalIgnoreCase);
        var admins = users.Where(user => user.IsActive && string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase))
            .OrderBy(user => DisplayName(user), StringComparer.OrdinalIgnoreCase)
            .Select(user => new RaidLogAdminDto(user.Id, user.AccountName, DisplayName(user))).ToList();
        var groups = await db.RaidLogGroups.AsNoTracking().AsSplitQuery()
            .Include(group => group.Items).ThenInclude(item => item.AssignedToUser)
            .Include(group => group.Items).ThenInclude(item => item.Notes)
            .Include(group => group.Items).ThenInclude(item => item.Activity)
            .OrderBy(group => group.SortOrder).ThenBy(group => group.Name)
            .ToListAsync(cancellationToken);
        return new RaidLogOverviewDto(admins, groups.Select(group => new RaidLogGroupDto(
            group.Id, group.Name, group.Description, group.SortOrder, group.Version,
            group.Items.Select(item => new RaidLogItemDto(
                item.Id, item.GroupId, item.Title, item.Description, item.Kind, item.Priority,
                item.AssignedToUserId, item.AssignedToUser is null ? null : DisplayName(item.AssignedToUser),
                item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy,
                item.CompletedAt, item.CompletedBy, item.Version,
                item.Notes.OrderByDescending(note => note.CreatedAt).Select(note => new RaidLogNoteDto(
                    note.Id, note.Body, note.CreatedAt, note.CreatedBy, ResolveName(names, note.CreatedBy))).ToList(),
                item.Activity.OrderByDescending(activity => activity.OccurredAt).Select(activity => new RaidLogActivityDto(
                    activity.Id, activity.Action, activity.Summary, activity.OccurredAt, activity.Actor, ResolveName(names, activity.Actor))).ToList()))
                .OrderBy(item => PriorityRank(item.Priority)).ThenBy(item => item.CompletedAt is not null).ThenByDescending(item => item.UpdatedAt).ToList())).ToList());
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
