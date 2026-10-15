using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Models;

namespace QualityAssurance.Api.Services;

public sealed class QualityTeamService(
    QualityAssuranceDbContext db,
    IQualityAssuranceAccessStore accessStore)
{
    public async Task<QualityTeamAdminDto> GetAdminAsync(CancellationToken cancellationToken)
    {
        var teams = await db.Teams
            .AsNoTracking()
            .Include(team => team.Members)
            .OrderByDescending(team => team.IsActive)
            .ThenBy(team => team.Name)
            .ToListAsync(cancellationToken);
        var users = await accessStore.GetUsersAsync(cancellationToken: cancellationToken);
        return new QualityTeamAdminDto(
            teams.Select(ToDto).ToList(),
            users.Select(user => new QualityTeamDirectoryUserDto(
                user.Id,
                user.AccountName,
                user.DisplayName)).ToList());
    }

    public async Task<QualityTeamDto> CreateAsync(
        QualityTeamCreateDto dto,
        QualityAssuranceAccessProfile actor,
        CancellationToken cancellationToken)
    {
        var name = NormalizeName(dto.Name);
        await EnsureNameAvailableAsync(name, null, cancellationToken);
        var users = await ValidateMembersAsync(dto.MemberIds, dto.ManagerIds, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var team = new QualityTeam
        {
            Name = name,
            Description = NormalizeDescription(dto.Description),
            IsActive = true,
            Version = 1,
            CreatedAt = now,
            CreatedBy = actor.AccountName,
            UpdatedAt = now,
            UpdatedBy = actor.AccountName
        };
        ApplyMembers(team, users, dto.ManagerIds, actor.AccountName, now);
        db.Teams.Add(team);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(team);
    }

    public async Task<QualityTeamDto?> UpdateAsync(
        int id,
        QualityTeamUpdateDto dto,
        QualityAssuranceAccessProfile actor,
        CancellationToken cancellationToken)
    {
        var team = await db.Teams
            .Include(candidate => candidate.Members)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (team is null) return null;
        PrepareVersion(team, dto.Version);
        var name = NormalizeName(dto.Name);
        await EnsureNameAvailableAsync(name, id, cancellationToken);
        var users = await ValidateMembersAsync(dto.MemberIds, dto.ManagerIds, cancellationToken);
        var nextUserIds = users.Keys.ToHashSet();
        var removedUserIds = team.Members
            .Select(member => member.AppUserId)
            .Where(userId => !nextUserIds.Contains(userId))
            .ToList();
        if (removedUserIds.Count > 0)
        {
            var assignedCount = await db.Shipments.AsNoTracking().CountAsync(shipment =>
                !shipment.IsShipped
                && shipment.AssignedGroupId == team.Id
                && shipment.AssignedUserId.HasValue
                && removedUserIds.Contains(shipment.AssignedUserId.Value), cancellationToken);
            if (assignedCount > 0)
                throw new ArgumentException(
                    $"Reassign {assignedCount} open shipment(s) before removing those team members.");
        }

        var now = DateTimeOffset.UtcNow;
        team.Name = name;
        team.Description = NormalizeDescription(dto.Description);
        team.IsActive = dto.IsActive;
        team.UpdatedAt = now;
        team.UpdatedBy = actor.AccountName;
        team.Version++;
        ApplyMembers(team, users, dto.ManagerIds, actor.AccountName, now);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(team);
    }

    public async Task<QualityTeamDto?> DisableAsync(
        int id,
        QualityAssuranceAccessProfile actor,
        CancellationToken cancellationToken)
    {
        var team = await db.Teams
            .Include(candidate => candidate.Members)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (team is null) return null;
        if (team.IsActive)
        {
            team.IsActive = false;
            team.Version++;
            team.UpdatedAt = DateTimeOffset.UtcNow;
            team.UpdatedBy = actor.AccountName;
            await db.SaveChangesAsync(cancellationToken);
        }
        return ToDto(team);
    }

    public async Task<QualityAssignmentOptionsDto> GetAssignmentOptionsAsync(
        CancellationToken cancellationToken)
    {
        var teams = await db.Teams
            .AsNoTracking()
            .Where(team => team.IsActive)
            .OrderBy(team => team.Name)
            .Select(team => new QualityDirectoryGroupDto(
                team.Id,
                team.Name,
                team.Description,
                team.Members.Count))
            .ToListAsync(cancellationToken);
        var teamIds = teams.Select(team => team.Id).ToHashSet();
        var memberships = await db.TeamMembers
            .AsNoTracking()
            .Where(member => teamIds.Contains(member.QualityTeamId))
            .ToListAsync(cancellationToken);
        var activeUsers = (await accessStore.GetUsersAsync(cancellationToken: cancellationToken))
            .ToDictionary(user => user.Id);
        var users = memberships
            .Where(member => activeUsers.ContainsKey(member.AppUserId))
            .GroupBy(member => member.AppUserId)
            .Select(group =>
            {
                var user = activeUsers[group.Key];
                return new QualityDirectoryUserDto(
                    user.Id,
                    user.AccountName,
                    user.DisplayName,
                    group.Select(member => member.QualityTeamId).Distinct().OrderBy(id => id).ToList());
            })
            .OrderBy(user => user.DisplayName)
            .ThenBy(user => user.AccountName)
            .ToList();
        return new QualityAssignmentOptionsDto(teams, users);
    }

    public async Task<(IReadOnlyList<QualityDirectoryGroup> Teams, IReadOnlyList<QualityDirectoryUser> Users)>
        GetOperationalDirectoryAsync(CancellationToken cancellationToken)
    {
        var options = await GetAssignmentOptionsAsync(cancellationToken);
        return (
            options.Groups.Select(team => new QualityDirectoryGroup(
                team.Id,
                team.Name,
                team.Description,
                team.ActiveUserCount)).ToList(),
            options.Users.Select(user => new QualityDirectoryUser(
                user.Id,
                user.AccountName,
                user.DisplayName,
                user.GroupIds)).ToList());
    }

    public async Task<QualityTeamContext> GetContextAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (userId <= 0) return QualityTeamContext.Empty;
        var memberships = await db.TeamMembers
            .AsNoTracking()
            .Where(member => member.AppUserId == userId)
            .Select(member => new { member.QualityTeamId, member.IsManager })
            .ToListAsync(cancellationToken);
        return new QualityTeamContext(
            memberships.Select(member => member.QualityTeamId).ToHashSet(),
            memberships.Where(member => member.IsManager)
                .Select(member => member.QualityTeamId)
                .ToHashSet());
    }

    public async Task<QualityTeam?> FindActiveTeamAsync(
        int id,
        CancellationToken cancellationToken) =>
        await db.Teams
            .AsNoTracking()
            .Include(team => team.Members)
            .SingleOrDefaultAsync(team => team.Id == id && team.IsActive, cancellationToken);

    public async Task<QualityTeam?> FindActiveTeamByNameAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var normalized = name.Trim().ToUpper();
        return await db.Teams
            .AsNoTracking()
            .Include(team => team.Members)
            .SingleOrDefaultAsync(team => team.IsActive && team.Name.ToUpper() == normalized, cancellationToken);
    }

    public async Task<IReadOnlyList<QualityTeamMember>> GetActiveMembersAsync(
        int teamId,
        CancellationToken cancellationToken)
    {
        var activeUserIds = (await accessStore.GetUsersAsync(cancellationToken: cancellationToken))
            .Select(user => user.Id)
            .ToHashSet();
        return await db.TeamMembers
            .AsNoTracking()
            .Where(member => member.QualityTeamId == teamId && activeUserIds.Contains(member.AppUserId))
            .OrderBy(member => member.DisplayName)
            .ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<int, QualityDirectoryUser>> ValidateMembersAsync(
        IReadOnlyCollection<int>? memberIds,
        IReadOnlyCollection<int>? managerIds,
        CancellationToken cancellationToken)
    {
        var members = (memberIds ?? [])
            .Distinct()
            .ToHashSet();
        var managers = (managerIds ?? [])
            .Distinct()
            .ToHashSet();
        if (members.Any(id => id <= 0) || managers.Any(id => id <= 0))
            throw new ArgumentException("Team user IDs must be positive.");
        if (members.Count > 500)
            throw new ArgumentException("A Quality team cannot contain more than 500 users.");
        if (!managers.IsSubsetOf(members))
            throw new ArgumentException("Every team manager must also be included as a team member.");

        var activeUsers = (await accessStore.GetUsersAsync(cancellationToken: cancellationToken))
            .Where(user => members.Contains(user.Id))
            .ToDictionary(user => user.Id);
        var missing = members.Where(id => !activeUsers.ContainsKey(id)).OrderBy(id => id).ToList();
        if (missing.Count > 0)
            throw new ArgumentException(
                $"Only active registered users can be added to a Quality team. Invalid user ID(s): {string.Join(", ", missing)}.");
        return activeUsers;
    }

    private static void ApplyMembers(
        QualityTeam team,
        IReadOnlyDictionary<int, QualityDirectoryUser> users,
        IReadOnlyCollection<int>? managerIds,
        string actorAccountName,
        DateTimeOffset now)
    {
        var managers = (managerIds ?? []).ToHashSet();
        foreach (var existing in team.Members
                     .Where(member => !users.ContainsKey(member.AppUserId))
                     .ToList())
            team.Members.Remove(existing);

        foreach (var user in users.Values)
        {
            var member = team.Members.SingleOrDefault(candidate => candidate.AppUserId == user.Id);
            if (member is null)
            {
                member = new QualityTeamMember
                {
                    AppUserId = user.Id,
                    AddedAt = now,
                    AddedBy = actorAccountName
                };
                team.Members.Add(member);
            }
            member.AccountName = user.AccountName;
            member.DisplayName = user.DisplayName;
            member.IsManager = managers.Contains(user.Id);
        }
    }

    private async Task EnsureNameAvailableAsync(
        string name,
        int? exceptId,
        CancellationToken cancellationToken)
    {
        var normalized = name.ToUpper();
        if (await db.Teams.AsNoTracking().AnyAsync(team =>
                (!exceptId.HasValue || team.Id != exceptId.Value)
                && team.Name.ToUpper() == normalized, cancellationToken))
            throw new ArgumentException("A Quality team with this name already exists.");
    }

    private static string NormalizeName(string? value)
    {
        var name = value?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 120)
            throw new ArgumentException("Team name must be between 2 and 120 characters.");
        return name;
    }

    private static string? NormalizeDescription(string? value)
    {
        var description = value?.Trim();
        if (string.IsNullOrWhiteSpace(description)) return null;
        if (description.Length > 500)
            throw new ArgumentException("Team description cannot exceed 500 characters.");
        return description;
    }

    private static void PrepareVersion(QualityTeam team, long version)
    {
        if (team.Version != version)
            throw new DbUpdateConcurrencyException("The Quality team changed. Reload before saving.");
        team.Version = version;
    }

    private static QualityTeamDto ToDto(QualityTeam team) => new(
        team.Id,
        team.Name,
        team.Description,
        team.IsActive,
        team.Version,
        team.Members
            .OrderByDescending(member => member.IsManager)
            .ThenBy(member => member.DisplayName)
            .Select(member => new QualityTeamMemberDto(
                member.AppUserId,
                member.AccountName,
                member.DisplayName,
                member.IsManager))
            .ToList());
}
