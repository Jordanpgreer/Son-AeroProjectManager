using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Models;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed class QualityShipmentService(
    QualityAssuranceDbContext db,
    IQualityAssuranceAccessStore accessStore,
    QualityAssignmentService assignments,
    QualityTeamService teams,
    QualityLegacyAssignmentReconciler legacyAssignments,
    IConfiguration? configuration = null,
    IQualityShipmentSyncService? integrationSync = null,
    QualityWorkflowService? workflows = null)
{
    private QualityWorkflowService Workflows => workflows ?? new QualityWorkflowService(db, accessStore, teams, configuration);
    public async Task<QualityShipmentDto?> GetAsync(
        int id,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken = default)
    {
        await legacyAssignments.ReconcileAsync(cancellationToken);
        var shipment = await db.Shipments
            .AsNoTracking()
            .Include(candidate => candidate.Parts)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (shipment is null) return null;
        EnsureRecordAccess(shipment, access, await teams.GetContextAsync(access.UserId, cancellationToken));
        return ToDto(shipment, access);
    }

    public async Task<QualityShipmentListDto> ListAsync(
        QualityAssuranceAccessProfile access,
        string? status,
        string? scope,
        string? sort,
        string? direction,
        string? search,
        string? shipmentStatus,
        IReadOnlyCollection<string>? customer,
        string? assignee,
        CancellationToken cancellationToken)
    {
        await legacyAssignments.ReconcileAsync(cancellationToken);
        var normalizedStatus = NormalizeStatus(status);
        var teamContext = await teams.GetContextAsync(access.UserId, cancellationToken);
        var normalizedScope = NormalizeScope(access, teamContext, scope);
        var normalizedSort = NormalizeSort(access, sort);
        var normalizedDirection = NormalizeDirection(direction);

        var query = ApplyVisibility(db.Shipments.AsNoTracking().Include(shipment => shipment.Parts), access, normalizedScope, teamContext);
        query = normalizedStatus switch
        {
            "shipped" => query.Where(shipment => shipment.IsShipped),
            "all" => query,
            _ => query.Where(shipment => !shipment.IsShipped)
        };
        query = ApplySearch(query, access, search);
        query = ApplyFilters(query, access, shipmentStatus, customer, assignee);
        var total = await query.CountAsync(cancellationToken);
        var shipments = RequiresClientSortForSqlite(normalizedSort)
            && string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal)
                ? ApplyClientSort(await query.ToListAsync(cancellationToken), normalizedSort, normalizedDirection)
                    .Take(500)
                    .ToList()
                : await ApplySort(query, normalizedSort, normalizedDirection, access)
                    .Take(500)
                    .ToListAsync(cancellationToken);
        return new QualityShipmentListDto(
            shipments.Select(shipment => ToDto(shipment, access)).ToList(),
            total,
            normalizedStatus,
            normalizedScope,
            normalizedSort,
            normalizedDirection,
            RestrictEditableFields(access, await Workflows.GetRestrictedActionsAsync(access, cancellationToken)));
    }

    public async Task<IReadOnlyList<QualityShipmentDto>> ExportRowsAsync(
        QualityAssuranceAccessProfile access,
        string? status,
        string? scope,
        string? sort,
        string? direction,
        string? search,
        string? shipmentStatus,
        IReadOnlyCollection<string>? customer,
        string? assignee,
        CancellationToken cancellationToken)
    {
        await legacyAssignments.ReconcileAsync(cancellationToken);
        var normalizedStatus = NormalizeStatus(status);
        var teamContext = await teams.GetContextAsync(access.UserId, cancellationToken);
        var normalizedScope = NormalizeScope(access, teamContext, scope);
        var query = ApplyVisibility(db.Shipments.AsNoTracking().Include(shipment => shipment.Parts), access, normalizedScope, teamContext);
        query = normalizedStatus switch
        {
            "shipped" => query.Where(shipment => shipment.IsShipped),
            "all" => query,
            _ => query.Where(shipment => !shipment.IsShipped)
        };
        query = ApplySearch(query, access, search);
        query = ApplyFilters(query, access, shipmentStatus, customer, assignee);
        var normalizedSort = NormalizeSort(access, sort);
        var normalizedDirection = NormalizeDirection(direction);
        var rows = RequiresClientSortForSqlite(normalizedSort)
            && string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal)
                ? ApplyClientSort(await query.ToListAsync(cancellationToken), normalizedSort, normalizedDirection).ToList()
                : await ApplySort(query, normalizedSort, normalizedDirection, access).ToListAsync(cancellationToken);
        return rows
            .Select(shipment => ToDto(shipment, access))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> CustomerOptionsAsync(
        QualityAssuranceAccessProfile access,
        string? status,
        string? scope,
        CancellationToken cancellationToken)
    {
        if (!access.HasPermission(QualityAssurancePermissions.CustomerView))
            return [];

        var normalizedStatus = NormalizeStatus(status);
        var teamContext = await teams.GetContextAsync(access.UserId, cancellationToken);
        var normalizedScope = NormalizeScope(access, teamContext, scope);
        var query = ApplyVisibility(db.Shipments.AsNoTracking(), access, normalizedScope, teamContext);
        query = normalizedStatus switch
        {
            "shipped" => query.Where(shipment => shipment.IsShipped),
            "all" => query,
            _ => query.Where(shipment => !shipment.IsShipped)
        };

        var values = await query
            .Select(shipment => shipment.Customer)
            .Where(customer => customer != "")
            .ToListAsync(cancellationToken);
        return values
            .Select(customer => customer.Trim())
            .Where(customer => customer.Length > 0)
            .OrderBy(customer => customer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(customer => customer, StringComparer.Ordinal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<QualityDashboardDto> DashboardAsync(
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken) =>
        DashboardAsync(access, "open", 1, 12, null, null, cancellationToken);

    public async Task<QualityDashboardDto> DashboardAsync(
        QualityAssuranceAccessProfile access,
        string? queue,
        int? page,
        int? pageSize,
        int? teamId,
        int? userId,
        CancellationToken cancellationToken)
    {
        await legacyAssignments.ReconcileAsync(cancellationToken);
        var restrictedActions = await Workflows.GetRestrictedActionsAsync(access, cancellationToken);
        var teamContext = await teams.GetContextAsync(access.UserId, cancellationToken);
        var canViewAll = access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll);
        var canViewTeam = canViewAll || teamContext.ManagedTeamIds.Count > 0;
        var canReviewUnassigned = CanReviewUnassigned(access);
        var workflowAllowsAssignment = !restrictedActions.Contains("assignment-changed");
        var canAssignGroup = workflowAllowsAssignment
            && (access.HasPermission(QualityAssurancePermissions.AssignmentGroup)
                || teamContext.ManagedTeamIds.Count > 0);
        var canAssignUser = workflowAllowsAssignment
            && (access.HasPermission(QualityAssurancePermissions.AssignmentUser)
                || teamContext.ManagedTeamIds.Count > 0);
        var canViewAssignment = access.HasPermission(QualityAssurancePermissions.AssignmentView);
        var canAssign = canViewAssignment && (canAssignGroup || canAssignUser);
        var canViewDollarValue = access.HasPermission(QualityAssurancePermissions.DollarValueView);
        var memberTeamIds = teamContext.TeamIds.ToList();
        var managedTeamIds = teamContext.ManagedTeamIds.ToList();

        IQueryable<QualityShipment> visibleQuery = db.Shipments
            .AsNoTracking()
            .Include(shipment => shipment.Parts);
        if (!canViewAll)
        {
            visibleQuery = visibleQuery.Where(shipment =>
                (shipment.IsOperationalTeamAssignment && shipment.AssignedUserId == access.UserId)
                || (shipment.IsOperationalTeamAssignment
                    && !shipment.AssignedUserId.HasValue
                    && shipment.AssignedGroupId.HasValue
                    && memberTeamIds.Contains(shipment.AssignedGroupId.Value))
                || (shipment.IsOperationalTeamAssignment
                    && shipment.AssignedGroupId.HasValue
                    && managedTeamIds.Contains(shipment.AssignedGroupId.Value))
                || (canReviewUnassigned && (!shipment.IsOperationalTeamAssignment || !shipment.AssignedGroupId.HasValue)));
        }
        var all = await visibleQuery.ToListAsync(cancellationToken);
        var reviewQueue = all.Where(shipment =>
            (shipment.IsOperationalTeamAssignment && shipment.AssignedUserId == access.UserId)
            || (shipment.IsOperationalTeamAssignment
                && !shipment.AssignedUserId.HasValue
                && shipment.AssignedGroupId.HasValue
                && teamContext.IsMember(shipment.AssignedGroupId.Value))
            || (canReviewUnassigned && (!shipment.IsOperationalTeamAssignment || !shipment.AssignedGroupId.HasValue)))
            .ToList();
        var unassigned = canReviewUnassigned
            ? all.Where(shipment => !shipment.IsOperationalTeamAssignment || !shipment.AssignedGroupId.HasValue).ToList()
            : [];
        var groupQueue = all.Where(shipment => shipment.IsOperationalTeamAssignment
                && shipment.AssignedGroupId.HasValue
                && !shipment.AssignedUserId.HasValue
                && teamContext.IsMember(shipment.AssignedGroupId.Value))
            .ToList();

        var team = new List<QualityPersonQueueDto>();
        if (canViewTeam)
        {
            var options = await teams.GetAssignmentOptionsAsync(cancellationToken);
            var permittedTeamIds = canViewAll
                ? options.Groups.Select(group => group.Id).ToHashSet()
                : teamContext.ManagedTeamIds;
            team = options.Users
                .Where(user => user.GroupIds.Any(permittedTeamIds.Contains))
                .Select(user =>
                {
                    var personShipments = all.Where(shipment => shipment.IsOperationalTeamAssignment
                        && shipment.AssignedUserId == user.Id
                        && shipment.AssignedGroupId.HasValue
                        && permittedTeamIds.Contains(shipment.AssignedGroupId.Value)).ToList();
                    return new QualityPersonQueueDto(
                        user.Id,
                        user.DisplayName,
                        user.AccountName,
                        Metrics(personShipments, canViewDollarValue),
                        personShipments.Where(shipment => !shipment.IsShipped)
                            .OrderBy(shipment => shipment.ShipDate ?? DateOnly.MaxValue)
                            .ThenBy(shipment => shipment.QaArrivalDate ?? DateOnly.MaxValue)
                            .ThenBy(shipment => shipment.Id)
                            .Select(shipment => ToDto(shipment, access))
                            .ToList());
                })
                .OrderByDescending(user => user.Metrics.Overdue)
                .ThenByDescending(user => user.Metrics.Open)
                .ThenBy(user => user.DisplayName)
                .ToList();
        }

        var normalizedQueue = NormalizeDashboardQueue(queue);
        var normalizedPage = Math.Max(1, page ?? 1);
        var normalizedPageSize = Math.Clamp(pageSize ?? 12, 1, 100);
        IQueryable<QualityShipment> selectedQuery = visibleQuery;
        switch (normalizedQueue)
        {
            case "completed":
                selectedQuery = ReviewQueueQuery(selectedQuery, access, teamContext, canReviewUnassigned)
                    .Where(shipment => shipment.IsShipped);
                break;
            case "overdue":
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                selectedQuery = ReviewQueueQuery(selectedQuery, access, teamContext, canReviewUnassigned)
                    .Where(shipment => !shipment.IsShipped && shipment.ShipDate.HasValue && shipment.ShipDate < today);
                break;
            case "unassigned":
                if (!canReviewUnassigned) throw new UnauthorizedAccessException("You cannot review unassigned shipments.");
                selectedQuery = selectedQuery.Where(shipment =>
                    !shipment.IsOperationalTeamAssignment || !shipment.AssignedGroupId.HasValue);
                break;
            case "team":
                if (!teamId.HasValue || (!canViewAll && !teamContext.IsManager(teamId.Value)))
                    throw new UnauthorizedAccessException("You cannot view this Quality team queue.");
                selectedQuery = selectedQuery.Where(shipment => shipment.IsOperationalTeamAssignment
                    && shipment.AssignedGroupId == teamId.Value);
                break;
            case "person":
                if (!teamId.HasValue || !userId.HasValue
                    || (!canViewAll && !teamContext.IsManager(teamId.Value)))
                    throw new UnauthorizedAccessException("You cannot view this Quality team member queue.");
                selectedQuery = selectedQuery.Where(shipment => shipment.IsOperationalTeamAssignment
                    && shipment.AssignedGroupId == teamId.Value
                    && shipment.AssignedUserId == userId.Value);
                break;
            default:
                selectedQuery = ReviewQueueQuery(selectedQuery, access, teamContext, canReviewUnassigned)
                    .Where(shipment => !shipment.IsShipped);
                break;
        }
        var queueTotal = await selectedQuery.CountAsync(cancellationToken);
        selectedQuery = normalizedQueue == "completed"
            ? selectedQuery.OrderByDescending(shipment => shipment.ShippedAt).ThenByDescending(shipment => shipment.Id)
            : selectedQuery.OrderBy(shipment => shipment.ShipDate == null)
                .ThenBy(shipment => shipment.ShipDate)
                .ThenBy(shipment => shipment.QaArrivalDate)
                .ThenBy(shipment => shipment.Id);
        var selectedShipments = await selectedQuery
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(cancellationToken);
        var selectedDtos = selectedShipments.Select(shipment => ToDto(shipment, access)).ToList();

        return new QualityDashboardDto(
            Metrics(reviewQueue, canViewDollarValue),
            selectedDtos,
            normalizedQueue == "completed" ? selectedDtos : [],
            team,
            Metrics(groupQueue, canViewDollarValue),
            groupQueue.Where(shipment => !shipment.IsShipped)
                .OrderBy(shipment => shipment.ShipDate ?? DateOnly.MaxValue)
                .ThenBy(shipment => shipment.Id)
                .Select(shipment => ToDto(shipment, access))
                .ToList(),
            Metrics(unassigned, canViewDollarValue),
            normalizedQueue == "unassigned" ? selectedDtos : [],
            canReviewUnassigned,
            canViewTeam,
            canViewAssignment,
            canAssign,
            canAssignGroup,
            canAssignUser,
            canViewDollarValue,
            RestrictEditableFields(access, restrictedActions),
            normalizedQueue,
            normalizedPage,
            normalizedPageSize,
            queueTotal);
    }

    public async Task<QualityShipmentDto> CreateAsync(
        QualityShipmentCreateDto dto,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        if (!access.HasPermission(QualityAssurancePermissions.ShipmentCreate))
            throw new UnauthorizedAccessException("You do not have permission to create shipping records.");
        if (dto.CreationRequestId == Guid.Empty)
            throw new ArgumentException("Creation request ID cannot be empty.");

        var shipperNumber = ResolveShipperNumber(dto.SalesOrderNumber, dto.ShipperNumber);
        if (dto.CreationRequestId.HasValue)
        {
            var replay = await db.Shipments
                .Include(candidate => candidate.Parts)
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.CreationRequestId == dto.CreationRequestId,
                    cancellationToken);
            if (replay is not null)
            {
                if (!string.Equals(replay.CreatedByAccountName, access.AccountName, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("This creation request belongs to another user.");
                if (!string.Equals(replay.SalesOrderNumber, shipperNumber, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("This creation request was already used for a different shipping record.");
                return ToDto(replay, access);
            }
        }

        var normalizedParts = NormalizeParts(dto.Parts, dto.PartNumber, dto.Quantity, dto.DollarValue);
        var missingQuantity = normalizedParts.FirstOrDefault(part => !part.Quantity.HasValue);
        if (missingQuantity is not null)
            throw new ArgumentException($"Quantity on line {missingQuantity.DisplayOrder + 1} is required.");
        var now = DateTimeOffset.UtcNow;
        var shipment = new QualityShipment
        {
            Status = QualityShipmentStatusRules.NormalizeUserStatus(dto.Status ?? "WIP"),
            SalesOrderNumber = shipperNumber,
            ShipperNumber = shipperNumber,
            CreationRequestId = dto.CreationRequestId,
            QaArrivalDate = dto.QaArrivalDate ?? DateOnly.FromDateTime(DateTime.Today),
            PartNumber = normalizedParts[0].PartNumber,
            PurchaseOrderNumber = Required(dto.PurchaseOrderNumber, "PO number", 160),
            Customer = Required(dto.Customer, "Customer", 240),
            TaskType = Required(dto.TaskType ?? "General", "Task type", 120),
            Quantity = normalizedParts.Sum(part => (decimal?)(part.Quantity ?? 0)),
            DollarValue = normalizedParts.Sum(part => part.TotalValue ?? 0),
            ShipDate = dto.ShipDate,
            HoldReason = Text(dto.HoldReason, null, 4000),
            SourceRequestedDate = dto.SourceRequestedDate,
            NextAction = Text(dto.NextAction, null, 2000),
            Comments = Text(dto.Comments, null, 8000),
            LastWorkedAt = now,
            CreatedAt = now,
            CreatedByAccountName = access.AccountName,
            CreatedByDisplayName = access.DisplayName,
            UpdatedAt = now,
            UpdatedByAccountName = access.AccountName,
            UpdatedByDisplayName = access.DisplayName,
            Version = 1
        };
        ApplyParts(shipment, normalizedParts);
        var rule = await assignments.ApplyFirstMatchingRuleAsync(shipment, cancellationToken);
        var assignmentApplied = rule is not null;
        if (!string.IsNullOrWhiteSpace(shipment.Comments))
        {
            shipment.CommentThread.Add(new QualityShipmentComment
            {
                Body = shipment.Comments,
                AuthorUserId = access.UserId,
                AuthorAccountName = access.AccountName,
                AuthorDisplayName = access.DisplayName,
                CreatedAt = now
            });
        }
        if (rule is null)
        {
            var teamContext = await teams.GetContextAsync(access.UserId, cancellationToken);
            foreach (var teamId in teamContext.TeamIds.OrderBy(id => id))
            {
                var team = await teams.FindActiveTeamAsync(teamId, cancellationToken);
                var member = team?.Members.SingleOrDefault(candidate => candidate.AppUserId == access.UserId);
                if (team is null || member is null) continue;
                shipment.AssignedGroupId = team.Id;
                shipment.AssignedGroupName = team.Name;
                shipment.AssignedUserId = member.AppUserId;
                shipment.AssignedAccountName = member.AccountName;
                shipment.AssignedDisplayName = member.DisplayName;
                shipment.IsOperationalTeamAssignment = true;
                assignmentApplied = true;
                break;
            }
        }
        if (shipment.AssignedUserId.HasValue)
            shipment.Status = QualityShipmentStatusRules.ForAssignedGroup(shipment.AssignedGroupName)
                ?? shipment.Status;
        await Workflows.ApplyAsync("shipment-created", shipment, access, cancellationToken);
        assignmentApplied |= shipment.AssignedGroupId.HasValue;
        db.Shipments.Add(shipment);
        AddAudit(shipment, access, "Created", "salesOrderNumber", null, shipment.SalesOrderNumber, now);
        AddAudit(
            shipment,
            access,
            assignmentApplied ? "AutoAssigned" : "AssignmentPending",
            "assignment",
            null,
            AssignmentLabel(shipment),
            now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (dto.CreationRequestId.HasValue)
        {
            db.ChangeTracker.Clear();
            var replay = await db.Shipments
                .Include(candidate => candidate.Parts)
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.CreationRequestId == dto.CreationRequestId,
                    CancellationToken.None);
            if (replay is null) throw;
            if (!string.Equals(replay.CreatedByAccountName, access.AccountName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(replay.SalesOrderNumber, shipperNumber, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("This creation request was already used for a different shipping record.");
            return ToDto(replay, access);
        }
        if (integrationSync is not null)
        {
            try
            {
                await integrationSync.TrySyncShipmentAsync(shipment.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The local record is already committed. Let the normal reconciliation path
                // retry external synchronization without encouraging a duplicate create.
            }
        }
        return ToDto(shipment, access);
    }

    public async Task<QualityShipmentDto?> PatchAsync(
        int id,
        QualityShipmentPatchDto dto,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments
            .Include(candidate => candidate.Parts)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (shipment is null) return null;
        EnsureRecordAccess(shipment, access, await teams.GetContextAsync(access.UserId, cancellationToken));
        PrepareVersion(shipment, dto.Version);
        var now = DateTimeOffset.UtcNow;
        var changedRoutingInput = false;
        var shipperChanges = dto.Changes
            .Where(change => change.Key.Equals("salesOrderNumber", StringComparison.OrdinalIgnoreCase)
                || change.Key.Equals("shipperNumber", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var changedShipperNumber = shipperChanges.Count > 0;
        if (changedShipperNumber)
        {
            EnsureShipperEditable(access, shipperChanges[0].Value);
            var salesOrderValue = shipperChanges
                .FirstOrDefault(change => change.Key.Equals("salesOrderNumber", StringComparison.OrdinalIgnoreCase));
            var aliasValue = shipperChanges
                .FirstOrDefault(change => change.Key.Equals("shipperNumber", StringComparison.OrdinalIgnoreCase));
            var resolved = ResolveShipperNumber(
                salesOrderValue.Key is null ? null : ReadString(salesOrderValue.Value),
                aliasValue.Key is null ? null : ReadString(aliasValue.Value));
            Change(shipment, "salesOrderNumber", shipment.SalesOrderNumber, resolved, next => shipment.SalesOrderNumber = next, access, now);
            shipment.ShipperNumber = resolved;
        }
        foreach (var change in dto.Changes)
        {
            if (change.Key.Equals("salesOrderNumber", StringComparison.OrdinalIgnoreCase)
                || change.Key.Equals("shipperNumber", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(change.Key, "parts", StringComparison.OrdinalIgnoreCase))
            {
                EnsurePartsEditable(access);
                ApplyPartsChange(shipment, change.Value, access, now);
                continue;
            }
            var field = QualityFieldAccess.Find(change.Key);
            if (field.EditPermission is null || !access.HasPermission(field.EditPermission))
                throw new UnauthorizedAccessException($"You do not have permission to edit {field.Label}.");
            changedRoutingInput |= field.Key is "customer" or "taskType";
            ApplyChange(shipment, field.Key, change.Value, access, now);
        }
        if (!db.ChangeTracker.HasChanges()) return ToDto(shipment, access);
        if (changedRoutingInput && !shipment.IsOperationalTeamAssignment)
            await assignments.ApplyFirstMatchingRuleAsync(shipment, cancellationToken);
        await Workflows.ApplyAsync("shipment-updated", shipment, access, cancellationToken);
        shipment.LastWorkedAt = now;
        shipment.UpdatedAt = now;
        shipment.UpdatedByAccountName = access.AccountName;
        shipment.UpdatedByDisplayName = access.DisplayName;
        shipment.Version++;
        await db.SaveChangesAsync(cancellationToken);
        if (integrationSync is not null && changedShipperNumber)
            await integrationSync.TrySyncShipmentAsync(shipment.Id, cancellationToken);
        return ToDto(shipment, access);
    }

    public async Task<QualityShipmentDto?> AssignAsync(
        int id,
        QualityShipmentAssignmentDto dto,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments
            .Include(candidate => candidate.Parts)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (shipment is null) return null;
        var teamContext = await teams.GetContextAsync(access.UserId, cancellationToken);
        EnsureRecordAccess(shipment, access, teamContext);
        PrepareVersion(shipment, dto.Version);
        var team = dto.GroupId.HasValue
            ? await teams.FindActiveTeamAsync(dto.GroupId.Value, cancellationToken)
                ?? throw new ArgumentException("Select an active Quality team.")
            : null;
        var managesTargetTeam = team is not null && teamContext.IsManager(team.Id);
        var managesCurrentTeam = shipment.IsOperationalTeamAssignment
            && shipment.AssignedGroupId.HasValue
            && teamContext.IsManager(shipment.AssignedGroupId.Value);
        var confirmsLegacyAssignment = !string.IsNullOrWhiteSpace(shipment.LegacyAssigneeTag)
            && dto.GroupId == (shipment.IsOperationalTeamAssignment ? shipment.AssignedGroupId : null)
            && dto.UserId == (shipment.IsOperationalTeamAssignment ? shipment.AssignedUserId : null);
        if (confirmsLegacyAssignment
            && !access.HasPermission(QualityAssurancePermissions.AssignmentGroup)
            && !access.HasPermission(QualityAssurancePermissions.AssignmentUser)
            && !managesTargetTeam
            && !managesCurrentTeam)
            throw new UnauthorizedAccessException("You do not have permission to confirm legacy assignments.");
        if ((dto.GroupId != (shipment.IsOperationalTeamAssignment ? shipment.AssignedGroupId : null))
            && !access.HasPermission(QualityAssurancePermissions.AssignmentGroup)
            && !managesTargetTeam
            && !managesCurrentTeam)
            throw new UnauthorizedAccessException("You do not have permission to move this shipment between Quality teams.");
        if ((dto.UserId != (shipment.IsOperationalTeamAssignment ? shipment.AssignedUserId : null))
            && !access.HasPermission(QualityAssurancePermissions.AssignmentUser)
            && !managesTargetTeam)
            throw new UnauthorizedAccessException("You do not have permission to assign users in this Quality team.");

        QualityTeamMember? user = null;
        if (dto.UserId.HasValue)
        {
            if (team is null) throw new ArgumentException("Select a Quality team before assigning a user.");
            user = (await teams.GetActiveMembersAsync(team.Id, cancellationToken))
                .SingleOrDefault(candidate => candidate.AppUserId == dto.UserId.Value)
                ?? throw new ArgumentException("The selected user must be active and belong to the selected Quality team.");
        }

        if (shipment.IsOperationalTeamAssignment
            && dto.GroupId == shipment.AssignedGroupId
            && dto.UserId == shipment.AssignedUserId
            && string.IsNullOrWhiteSpace(shipment.LegacyAssigneeTag))
            return ToDto(shipment, access);

        var old = !string.IsNullOrWhiteSpace(shipment.LegacyAssigneeTag)
            ? $"Legacy tag: {shipment.LegacyAssigneeTag}"
            : AssignmentValue(shipment);
        var oldAction = shipment.NextAction;
        var oldStatus = shipment.Status;
        shipment.AssignedGroupId = team?.Id;
        shipment.AssignedGroupName = team?.Name;
        shipment.AssignedUserId = user?.AppUserId;
        shipment.AssignedAccountName = user?.AccountName;
        shipment.AssignedDisplayName = user?.DisplayName;
        shipment.IsOperationalTeamAssignment = team is not null;
        shipment.LegacyAssigneeTag = null;
        if (user is not null) shipment.NextAction = user.DisplayName;
        var assignmentStatus = user is null ? null : QualityShipmentStatusRules.ForAssignedGroup(team?.Name);
        if (assignmentStatus is not null) shipment.Status = assignmentStatus;
        var now = DateTimeOffset.UtcNow;
        shipment.LastWorkedAt = now;
        shipment.UpdatedAt = now;
        shipment.UpdatedByAccountName = access.AccountName;
        shipment.UpdatedByDisplayName = access.DisplayName;
        shipment.Version++;
        await Workflows.ApplyAsync("assignment-changed", shipment, access, cancellationToken);
        AddAudit(shipment, access, "Assigned", "assignment", old, AssignmentValue(shipment), now);
        if (oldAction != shipment.NextAction)
            AddAudit(shipment, access, "Updated", "nextAction", oldAction, shipment.NextAction, now);
        if (oldStatus != shipment.Status)
            AddAudit(shipment, access, "Updated", "status", oldStatus, shipment.Status, now);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(shipment, access);
    }

    public async Task<QualityShipmentDto?> MarkShippedAsync(
        int id,
        long version,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments
            .Include(candidate => candidate.Parts)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (shipment is null) return null;
        EnsureRecordAccess(shipment, access, await teams.GetContextAsync(access.UserId, cancellationToken));
        PrepareVersion(shipment, version);
        if (shipment.IsShipped) return ToDto(shipment, access);
        var now = DateTimeOffset.UtcNow;
        var oldStatus = shipment.Status;
        shipment.IsShipped = true;
        shipment.Status = "Shipped";
        shipment.ShippedAt = now;
        shipment.ShippedByAccountName = access.AccountName;
        shipment.ShippedByDisplayName = access.DisplayName;
        shipment.LastWorkedAt = now;
        shipment.UpdatedAt = now;
        shipment.UpdatedByAccountName = access.AccountName;
        shipment.UpdatedByDisplayName = access.DisplayName;
        shipment.Version++;
        AddAudit(shipment, access, "Shipped", "status", oldStatus, "Shipped", now);
        await Workflows.ApplyAsync("shipment-shipped", shipment, access, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(shipment, access);
    }

    public async Task<QualityShipmentDto?> MarkQaCompleteAsync(
        int id,
        long version,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments
            .Include(candidate => candidate.Parts)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (shipment is null) return null;
        EnsureRecordAccess(shipment, access, await teams.GetContextAsync(access.UserId, cancellationToken));
        PrepareVersion(shipment, version);
        if (shipment.IsShipped)
            throw new ArgumentException("A shipped record cannot be returned to the Shipper queue.");

        var now = DateTimeOffset.UtcNow;
        var oldStatus = shipment.Status;
        var oldAssignment = AssignmentValue(shipment);
        shipment.Status = "Ready to Ship";
        if (!await Workflows.ApplyAsync("qa-completed", shipment, access, cancellationToken))
        {
            var configuredShippingGroupName = configuration?["QualityWorkflow:ShippingGroupName"]?.Trim();
            var shippingGroupName = string.IsNullOrWhiteSpace(configuredShippingGroupName)
                || string.Equals(configuredShippingGroupName, "Shipping", StringComparison.OrdinalIgnoreCase)
                    ? ApplicationGroups.Shipper : configuredShippingGroupName;
            if (string.Equals(shipment.AssignedGroupName, shippingGroupName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"This record is already in the {shippingGroupName} queue.");
            var shippingGroup = await teams.FindActiveTeamByNameAsync(shippingGroupName, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"The {shippingGroupName} Quality team must be active before QA Complete can route work to it.");
            shipment.AssignedGroupId = shippingGroup.Id;
            shipment.AssignedGroupName = shippingGroup.Name;
            shipment.AssignedUserId = null;
            shipment.AssignedAccountName = null;
            shipment.AssignedDisplayName = null;
            shipment.IsOperationalTeamAssignment = true;
            shipment.NextAction = shippingGroup.Name;
        }
        shipment.LastWorkedAt = now;
        shipment.UpdatedAt = now;
        shipment.UpdatedByAccountName = access.AccountName;
        shipment.UpdatedByDisplayName = access.DisplayName;
        shipment.Version++;
        AddAudit(shipment, access, "QaCompleted", "status", oldStatus, shipment.Status, now);
        AddAudit(shipment, access, "Assigned", "assignment", oldAssignment, AssignmentValue(shipment), now);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(shipment, access);
    }

    public async Task<IReadOnlyList<QualityShipmentAuditDto>?> AuditAsync(
        int id,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (shipment is null) return null;
        EnsureRecordAccess(shipment, access, await teams.GetContextAsync(access.UserId, cancellationToken));
        var entries = await db.ShipmentAuditEntries
            .AsNoTracking()
            .Where(entry => entry.ShipmentId == id)
            .ToListAsync(cancellationToken);
        return entries
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Select(entry => new QualityShipmentAuditDto(
                entry.Id,
                entry.EventType,
                entry.FieldName,
                CanViewAuditValue(access, entry.FieldName) ? entry.OldValue : null,
                CanViewAuditValue(access, entry.FieldName) ? entry.NewValue : null,
                entry.AccountName,
                entry.DisplayName,
                entry.OccurredAt))
            .ToList();
    }

    private static bool CanViewAuditValue(QualityAssuranceAccessProfile access, string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return false;
        if (fieldName.Equals("assignment", StringComparison.OrdinalIgnoreCase))
            return access.HasPermission(QualityAssurancePermissions.AssignmentView);
        if (fieldName.Equals("parts", StringComparison.OrdinalIgnoreCase))
            return access.HasPermission(QualityAssurancePermissions.PartNumberView)
                && access.HasPermission(QualityAssurancePermissions.QuantityView)
                && access.HasPermission(QualityAssurancePermissions.DollarValueView);

        var field = QualityFieldAccess.All.FirstOrDefault(candidate =>
            candidate.Key.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
        return field is not null && access.HasPermission(field.ViewPermission);
    }

    private IQueryable<QualityShipment> ApplyVisibility(
        IQueryable<QualityShipment> query,
        QualityAssuranceAccessProfile access,
        string scope,
        QualityTeamContext teamContext)
    {
        if (scope == "all" && access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll)) return query;
        var memberTeamIds = teamContext.TeamIds.ToList();
        var managedTeamIds = teamContext.ManagedTeamIds.ToList();
        if (scope == "team" && managedTeamIds.Count > 0)
        {
            return query.Where(shipment =>
                (shipment.IsOperationalTeamAssignment && shipment.AssignedUserId == access.UserId)
                || (shipment.IsOperationalTeamAssignment
                    && !shipment.AssignedUserId.HasValue
                    && shipment.AssignedGroupId.HasValue
                    && memberTeamIds.Contains(shipment.AssignedGroupId.Value))
                || (shipment.IsOperationalTeamAssignment
                    && shipment.AssignedGroupId.HasValue
                    && managedTeamIds.Contains(shipment.AssignedGroupId.Value))
                || (CanReviewUnassigned(access)
                    && (!shipment.IsOperationalTeamAssignment || !shipment.AssignedGroupId.HasValue)));
        }
        if (CanReviewUnassigned(access))
        {
            return query.Where(shipment =>
                (shipment.IsOperationalTeamAssignment && shipment.AssignedUserId == access.UserId)
                || (shipment.IsOperationalTeamAssignment
                    && !shipment.AssignedUserId.HasValue
                    && shipment.AssignedGroupId.HasValue
                    && memberTeamIds.Contains(shipment.AssignedGroupId.Value))
                || (!shipment.IsOperationalTeamAssignment || !shipment.AssignedGroupId.HasValue));
        }
        return query.Where(shipment =>
            (shipment.IsOperationalTeamAssignment && shipment.AssignedUserId == access.UserId)
            || (shipment.IsOperationalTeamAssignment
                && !shipment.AssignedUserId.HasValue
                && shipment.AssignedGroupId.HasValue
                && memberTeamIds.Contains(shipment.AssignedGroupId.Value)));
    }

    private static IQueryable<QualityShipment> ApplySearch(
        IQueryable<QualityShipment> query,
        QualityAssuranceAccessProfile access,
        string? search)
    {
        var value = search?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return query;
        var normalized = value.ToLowerInvariant();
        var canSalesOrder = access.HasPermission(QualityAssurancePermissions.SalesOrderView);
        var canPart = access.HasPermission(QualityAssurancePermissions.PartNumberView);
        var canPo = access.HasPermission(QualityAssurancePermissions.PurchaseOrderView);
        var canCustomer = access.HasPermission(QualityAssurancePermissions.CustomerView);
        var canType = access.HasPermission(QualityAssurancePermissions.TaskTypeView);
        var canAction = access.HasPermission(QualityAssurancePermissions.ActionView);
        var canAssignment = access.HasPermission(QualityAssurancePermissions.AssignmentView);
        var canComments = access.HasPermission(QualityAssurancePermissions.CommentsView);
        return query.Where(shipment =>
            (canSalesOrder && shipment.SalesOrderNumber.ToLower().Contains(normalized))
            || (canSalesOrder && shipment.ShipperNumber != null && shipment.ShipperNumber.ToLower().Contains(normalized))
            || (canPart && (shipment.PartNumber.ToLower().Contains(normalized)
                || shipment.Parts.Any(part => part.PartNumber.ToLower().Contains(normalized))))
            || (canPo && shipment.PurchaseOrderNumber != null && shipment.PurchaseOrderNumber.ToLower().Contains(normalized))
            || (canCustomer && shipment.Customer.ToLower().Contains(normalized))
            || (canType && shipment.TaskType.ToLower().Contains(normalized))
            || (canAction && shipment.NextAction != null && shipment.NextAction.ToLower().Contains(normalized))
            || (canAssignment && shipment.AssignedDisplayName != null && shipment.AssignedDisplayName.ToLower().Contains(normalized))
            || (canAssignment && shipment.AssignedGroupName != null && shipment.AssignedGroupName.ToLower().Contains(normalized))
            || (canComments && shipment.Comments != null && shipment.Comments.ToLower().Contains(normalized)));
    }

    private static IQueryable<QualityShipment> ApplyFilters(
        IQueryable<QualityShipment> query,
        QualityAssuranceAccessProfile access,
        string? shipmentStatus,
        IReadOnlyCollection<string>? customer,
        string? assignee)
    {
        var normalizedShipmentStatus = shipmentStatus?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedShipmentStatus)
            && access.HasPermission(QualityAssurancePermissions.StatusView))
        {
            if (normalizedShipmentStatus is "past due" or "past-due" or "__past_due__")
            {
                if (access.HasPermission(QualityAssurancePermissions.ShipDateView))
                {
                    var today = DateOnly.FromDateTime(DateTime.UtcNow);
                    query = query.Where(shipment => !shipment.IsShipped
                        && shipment.ShipDate.HasValue
                        && shipment.ShipDate < today);
                }
            }
            else
                query = query.Where(shipment => shipment.Status.ToLower() == normalizedShipmentStatus);
        }

        var normalizedCustomers = customer?
            .Select(value => value?.Trim().ToLowerInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(25)
            .ToArray() ?? [];
        if (normalizedCustomers.Length > 0
            && access.HasPermission(QualityAssurancePermissions.CustomerView))
        {
            var shipment = Expression.Parameter(typeof(QualityShipment), "shipment");
            var customerProperty = Expression.Property(shipment, nameof(QualityShipment.Customer));
            var loweredCustomer = Expression.Call(
                customerProperty,
                typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!);
            var containsMethod = typeof(string).GetMethod(
                nameof(string.Contains),
                [typeof(string)])!;
            Expression? customerMatches = null;
            foreach (var normalizedCustomer in normalizedCustomers)
            {
                var contains = Expression.Call(
                    loweredCustomer,
                    containsMethod,
                    Expression.Constant(normalizedCustomer));
                customerMatches = customerMatches is null
                    ? contains
                    : Expression.OrElse(customerMatches, contains);
            }
            query = query.Where(Expression.Lambda<Func<QualityShipment, bool>>(
                customerMatches!, shipment));
        }

        var normalizedAssignee = assignee?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedAssignee)
            || !access.HasPermission(QualityAssurancePermissions.AssignmentView)) return query;
        if (normalizedAssignee == "unassigned")
            return query.Where(shipment => !shipment.IsOperationalTeamAssignment || !shipment.AssignedGroupId.HasValue);
        if (normalizedAssignee.StartsWith("user:", StringComparison.Ordinal)
            && int.TryParse(normalizedAssignee[5..], out var userId))
            return query.Where(shipment => shipment.AssignedUserId == userId);
        if (normalizedAssignee.StartsWith("group:", StringComparison.Ordinal)
            && int.TryParse(normalizedAssignee[6..], out var groupId))
            return query.Where(shipment => shipment.IsOperationalTeamAssignment
                && shipment.AssignedGroupId == groupId
                && !shipment.AssignedUserId.HasValue);
        return query;
    }

    private static IOrderedQueryable<QualityShipment> ApplySort(
        IQueryable<QualityShipment> query,
        string sort,
        string direction,
        QualityAssuranceAccessProfile access)
    {
        var descending = direction == "desc";
        var canViewAction = access.HasPermission(QualityAssurancePermissions.ActionView);
        var canViewAssignment = access.HasPermission(QualityAssurancePermissions.AssignmentView);
        IOrderedQueryable<QualityShipment> ordered = sort switch
        {
            "status" => descending ? query.OrderByDescending(shipment => shipment.Status) : query.OrderBy(shipment => shipment.Status),
            "sales-order" => descending ? query.OrderByDescending(shipment => shipment.SalesOrderNumber) : query.OrderBy(shipment => shipment.SalesOrderNumber),
            "part-number" => descending ? query.OrderByDescending(shipment => shipment.PartNumber) : query.OrderBy(shipment => shipment.PartNumber),
            "purchase-order" => descending ? query.OrderByDescending(shipment => shipment.PurchaseOrderNumber) : query.OrderBy(shipment => shipment.PurchaseOrderNumber),
            "customer" => descending ? query.OrderByDescending(shipment => shipment.Customer) : query.OrderBy(shipment => shipment.Customer),
            "quantity" => descending ? query.OrderByDescending(shipment => shipment.Quantity) : query.OrderBy(shipment => shipment.Quantity),
            "dollar-value" => descending ? query.OrderByDescending(shipment => shipment.DollarValue) : query.OrderBy(shipment => shipment.DollarValue),
            "ship-date" => descending
                ? query.OrderBy(shipment => shipment.ShipDate == null).ThenByDescending(shipment => shipment.ShipDate)
                : query.OrderBy(shipment => shipment.ShipDate == null).ThenBy(shipment => shipment.ShipDate),
            "hold-reason" => descending ? query.OrderByDescending(shipment => shipment.HoldReason) : query.OrderBy(shipment => shipment.HoldReason),
            "source-scheduled" => descending ? query.OrderByDescending(shipment => shipment.SourceRequestedDate) : query.OrderBy(shipment => shipment.SourceRequestedDate),
            "action" when canViewAction && canViewAssignment => descending
                ? query.OrderByDescending(shipment => shipment.AssignedDisplayName ?? shipment.AssignedGroupName ?? shipment.NextAction)
                : query.OrderBy(shipment => shipment.AssignedDisplayName ?? shipment.AssignedGroupName ?? shipment.NextAction),
            "action" when canViewAssignment => descending
                ? query.OrderByDescending(shipment => shipment.AssignedDisplayName ?? shipment.AssignedGroupName)
                : query.OrderBy(shipment => shipment.AssignedDisplayName ?? shipment.AssignedGroupName),
            "action" => descending
                ? query.OrderByDescending(shipment => shipment.NextAction)
                : query.OrderBy(shipment => shipment.NextAction),
            "last-worked" => descending ? query.OrderByDescending(shipment => shipment.LastWorkedAt) : query.OrderBy(shipment => shipment.LastWorkedAt),
            "comments" => descending ? query.OrderByDescending(shipment => shipment.Comments) : query.OrderBy(shipment => shipment.Comments),
            "queue-age" => descending ? query.OrderBy(shipment => shipment.CreatedAt) : query.OrderByDescending(shipment => shipment.CreatedAt),
            _ => descending
                ? query.OrderBy(shipment => shipment.QaArrivalDate == null).ThenByDescending(shipment => shipment.QaArrivalDate)
                : query.OrderBy(shipment => shipment.QaArrivalDate == null).ThenBy(shipment => shipment.QaArrivalDate)
        };
        return ordered.ThenBy(shipment => shipment.Id);
    }

    private static bool RequiresClientSortForSqlite(string sort) =>
        sort is "quantity" or "dollar-value" or "last-worked" or "queue-age";

    private static IOrderedEnumerable<QualityShipment> ApplyClientSort(
        IEnumerable<QualityShipment> shipments,
        string sort,
        string direction)
    {
        var descending = direction == "desc";
        IOrderedEnumerable<QualityShipment> ordered = sort switch
        {
            "quantity" => descending
                ? shipments.OrderByDescending(shipment => shipment.Quantity)
                : shipments.OrderBy(shipment => shipment.Quantity),
            "dollar-value" => descending
                ? shipments.OrderByDescending(shipment => shipment.DollarValue)
                : shipments.OrderBy(shipment => shipment.DollarValue),
            "last-worked" => descending
                ? shipments.OrderByDescending(shipment => shipment.LastWorkedAt)
                : shipments.OrderBy(shipment => shipment.LastWorkedAt),
            "queue-age" => descending
                ? shipments.OrderBy(shipment => shipment.CreatedAt)
                : shipments.OrderByDescending(shipment => shipment.CreatedAt),
            _ => shipments.OrderBy(shipment => shipment.Id)
        };
        return ordered.ThenBy(shipment => shipment.Id);
    }

    private static string NormalizeStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "shipped" or "closed" => "shipped",
        "all" => "all",
        _ => "open"
    };

    private static string NormalizeSort(QualityAssuranceAccessProfile access, string? sort)
    {
        var normalized = sort?.Trim().ToLowerInvariant() switch
        {
            "status" => "status",
            "sales-order" or "salesordernumber" => "sales-order",
            "part-number" or "partnumber" => "part-number",
            "purchase-order" or "purchaseordernumber" => "purchase-order",
            "customer" => "customer",
            "quantity" => "quantity",
            "dollar-value" or "dollarvalue" => "dollar-value",
            "ship-date" or "shipdate" => "ship-date",
            "hold-reason" or "holdreason" => "hold-reason",
            "source-scheduled" or "sourcerequesteddate" => "source-scheduled",
            "action" => "action",
            "last-worked" or "lastworkedat" => "last-worked",
            "comments" => "comments",
            "queue-age" or "queueage" => "queue-age",
            _ => "qa-arrival"
        };
        if (CanViewSort(access, normalized)) return normalized;
        return access.HasPermission(QualityAssurancePermissions.QaArrivalDateView)
            ? "qa-arrival"
            : "queue-age";
    }

    private static bool CanViewSort(QualityAssuranceAccessProfile access, string sort) => sort switch
    {
        "status" => access.HasPermission(QualityAssurancePermissions.StatusView),
        "sales-order" => access.HasPermission(QualityAssurancePermissions.SalesOrderView),
        "part-number" => access.HasPermission(QualityAssurancePermissions.PartNumberView),
        "purchase-order" => access.HasPermission(QualityAssurancePermissions.PurchaseOrderView),
        "customer" => access.HasPermission(QualityAssurancePermissions.CustomerView),
        "quantity" => access.HasPermission(QualityAssurancePermissions.QuantityView),
        "dollar-value" => access.HasPermission(QualityAssurancePermissions.DollarValueView),
        "ship-date" => access.HasPermission(QualityAssurancePermissions.ShipDateView),
        "hold-reason" => access.HasPermission(QualityAssurancePermissions.HoldReasonView),
        "source-scheduled" => access.HasPermission(QualityAssurancePermissions.SourceRequestedDateView),
        "action" => access.HasPermission(QualityAssurancePermissions.ActionView)
            || access.HasPermission(QualityAssurancePermissions.AssignmentView),
        "last-worked" => access.HasPermission(QualityAssurancePermissions.LastWorkedView),
        "comments" => access.HasPermission(QualityAssurancePermissions.CommentsView),
        "qa-arrival" => access.HasPermission(QualityAssurancePermissions.QaArrivalDateView),
        "queue-age" => true,
        _ => false
    };

    private static string NormalizeDirection(string? direction) =>
        string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";

    private static bool CanReviewUnassigned(QualityAssuranceAccessProfile access) =>
        access.HasPermission(QualityAssurancePermissions.ManagerReview);

    private static IQueryable<QualityShipment> ReviewQueueQuery(
        IQueryable<QualityShipment> query,
        QualityAssuranceAccessProfile access,
        QualityTeamContext teamContext,
        bool canReviewUnassigned)
    {
        var memberTeamIds = teamContext.TeamIds.ToList();
        return query.Where(shipment =>
            (shipment.IsOperationalTeamAssignment && shipment.AssignedUserId == access.UserId)
            || (shipment.IsOperationalTeamAssignment
                && !shipment.AssignedUserId.HasValue
                && shipment.AssignedGroupId.HasValue
                && memberTeamIds.Contains(shipment.AssignedGroupId.Value))
            || (canReviewUnassigned && (!shipment.IsOperationalTeamAssignment || !shipment.AssignedGroupId.HasValue)));
    }

    private static string NormalizeDashboardQueue(string? queue) =>
        queue?.Trim().ToLowerInvariant() switch
        {
            "overdue" => "overdue",
            "completed" => "completed",
            "unassigned" => "unassigned",
            "team" => "team",
            "person" => "person",
            _ => "open"
        };

    private static string NormalizeScope(
        QualityAssuranceAccessProfile access,
        QualityTeamContext teamContext,
        string? scope)
    {
        if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase)
            && access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll)) return "all";
        if (string.Equals(scope, "team", StringComparison.OrdinalIgnoreCase)
            && (teamContext.ManagedTeamIds.Count > 0
                || access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll))) return "team";
        return "mine";
    }

    private static void EnsureRecordAccess(
        QualityShipment shipment,
        QualityAssuranceAccessProfile access,
        QualityTeamContext teamContext)
    {
        if (access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll)) return;
        if (shipment.IsOperationalTeamAssignment && shipment.AssignedUserId == access.UserId) return;
        if (shipment.IsOperationalTeamAssignment
            && !shipment.AssignedUserId.HasValue
            && shipment.AssignedGroupId.HasValue
            && teamContext.IsMember(shipment.AssignedGroupId.Value)) return;
        if ((!shipment.IsOperationalTeamAssignment || !shipment.AssignedGroupId.HasValue)
            && CanReviewUnassigned(access)) return;
        if (shipment.IsOperationalTeamAssignment
            && shipment.AssignedGroupId.HasValue
            && teamContext.IsManager(shipment.AssignedGroupId.Value)) return;
        throw new UnauthorizedAccessException("This shipment is not in your permitted queue.");
    }

    private static void EnsureEditable(QualityAssuranceAccessProfile access, string fieldKey, object? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text)) return;
        var field = QualityFieldAccess.Find(fieldKey);
        if (field.EditPermission is null || !access.HasPermission(field.EditPermission))
            throw new UnauthorizedAccessException($"You do not have permission to set {field.Label}.");
    }

    private static void EnsurePartsEditable(
        QualityAssuranceAccessProfile access,
        IReadOnlyCollection<QualityShipmentPartInputDto>? parts = null)
    {
        if (!access.HasPermission(QualityAssurancePermissions.PartNumberEdit))
            throw new UnauthorizedAccessException("You do not have permission to edit part numbers.");
        if (parts?.Any(part => part.Quantity.HasValue) == true
            && !access.HasPermission(QualityAssurancePermissions.QuantityEdit))
            throw new UnauthorizedAccessException("You do not have permission to edit quantities.");
        if (parts?.Any(part => part.UnitPrice.HasValue) == true
            && !access.HasPermission(QualityAssurancePermissions.DollarValueEdit))
            throw new UnauthorizedAccessException("You do not have permission to edit unit prices.");
    }

    private void ApplyPartsChange(
        QualityShipment shipment,
        JsonElement value,
        QualityAssuranceAccessProfile access,
        DateTimeOffset now)
    {
        IReadOnlyList<QualityShipmentPartInputDto> inputs;
        try
        {
            inputs = JsonSerializer.Deserialize<List<QualityShipmentPartInputDto>>(
                value.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Shipment parts are not valid.", exception);
        }

        EnsurePartsEditable(access, inputs);
        var normalized = NormalizeParts(inputs, null, null, null);
        var oldValue = FormatParts(shipment.Parts);
        ApplyParts(shipment, normalized);
        AddAudit(shipment, access, "FieldChanged", "parts", oldValue, FormatParts(shipment.Parts), now);
    }

    private void ApplyParts(
        QualityShipment shipment,
        IReadOnlyList<NormalizedShipmentPart> parts)
    {
        var existing = shipment.Parts.Where(part => part.Id != 0).ToList();
        if (existing.Count > 0) db.ShipmentParts.RemoveRange(existing);
        shipment.Parts.Clear();
        foreach (var part in parts)
        {
            var previous = part.Id.HasValue
                ? existing.FirstOrDefault(candidate => candidate.Id == part.Id.Value)
                : null;
            shipment.Parts.Add(new QualityShipmentPart
            {
                PartNumber = part.PartNumber,
                Quantity = part.Quantity,
                UnitPrice = part.UnitPrice,
                TotalValue = part.TotalValue,
                ExternalItemId = previous?.ExternalItemId,
                LineItemNumber = part.LineItemNumber ?? previous?.LineItemNumber,
                DisplayOrder = part.DisplayOrder
            });
        }

        shipment.PartNumber = parts[0].PartNumber;
        shipment.Quantity = parts.Any(part => part.Quantity.HasValue)
            ? parts.Sum(part => (decimal?)(part.Quantity ?? 0))
            : null;
        shipment.DollarValue = parts.Any(part => part.TotalValue.HasValue)
            ? parts.Sum(part => part.TotalValue ?? 0)
            : null;
    }

    private static IReadOnlyList<NormalizedShipmentPart> NormalizeParts(
        IReadOnlyList<QualityShipmentPartInputDto>? parts,
        string? legacyPartNumber,
        decimal? legacyQuantity,
        decimal? legacyTotalValue)
    {
        if (parts is null or { Count: 0 })
        {
            var partNumber = Required(legacyPartNumber, "Part number", 160);
            var quantity = NonNegativeWhole(legacyQuantity, "Quantity");
            var totalValue = NonNegative(legacyTotalValue, "Dollar value");
            decimal? unitPrice = quantity > 0 && totalValue.HasValue
                ? decimal.Round(totalValue.Value / quantity.Value, 2, MidpointRounding.AwayFromZero)
                : null;
            return [new NormalizedShipmentPart(partNumber, quantity, unitPrice, totalValue, 0, null, null)];
        }

        if (parts.Count > 100)
            throw new ArgumentException("A shipping record cannot contain more than 100 part lines.");

        var normalized = parts.Select((part, index) =>
        {
            var partNumber = Required(part.PartNumber, $"Part number on line {index + 1}", 160);
            if (part.Quantity < 0) throw new ArgumentException($"Quantity on line {index + 1} cannot be negative.");
            var unitPrice = NonNegative(part.UnitPrice, $"Unit price on line {index + 1}");
            var lineItemNumber = Text(part.LineItemNumber, null, 80);
            decimal? totalValue = part.Quantity.HasValue && unitPrice.HasValue
                ? decimal.Round(part.Quantity.Value * unitPrice.Value, 2, MidpointRounding.AwayFromZero)
                : null;
            return new NormalizedShipmentPart(
                partNumber,
                part.Quantity,
                unitPrice,
                totalValue,
                index,
                part.Id,
                lineItemNumber);
        }).ToList();

        var duplicate = normalized
            .GroupBy(part => $"{part.LineItemNumber ?? "manual"}|{part.PartNumber}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Part number '{duplicate.First().PartNumber}' is listed more than once for the same line item.");
        return normalized;
    }

    private static string FormatParts(IEnumerable<QualityShipmentPart> parts) => string.Join(
        "; ",
        parts.OrderBy(part => part.DisplayOrder).Select(part =>
            $"Line {part.LineItemNumber ?? "-"} | {part.PartNumber} | Qty {part.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "Not set"} | Unit {part.UnitPrice?.ToString("0.00", CultureInfo.InvariantCulture) ?? "Not set"}"));

    private sealed record NormalizedShipmentPart(
        string PartNumber,
        int? Quantity,
        decimal? UnitPrice,
        decimal? TotalValue,
        int DisplayOrder,
        int? Id,
        string? LineItemNumber);

    private void ApplyChange(
        QualityShipment shipment,
        string key,
        JsonElement value,
        QualityAssuranceAccessProfile access,
        DateTimeOffset now)
    {
        switch (key)
        {
            case "status": Change(shipment, key, shipment.Status, QualityShipmentStatusRules.NormalizeUserStatus(ReadString(value)), next => shipment.Status = next, access, now); break;
            case "salesOrderNumber": throw new InvalidOperationException("Shipper number changes must be normalized before field updates are applied.");
            case "qaArrivalDate": Change(shipment, key, shipment.QaArrivalDate, RequiredDate(ReadDate(value), "Shipment arrival date"), next => shipment.QaArrivalDate = next, access, now); break;
            case "partNumber": Change(shipment, key, shipment.PartNumber, Required(ReadString(value), "Part number", 160), next => shipment.PartNumber = next, access, now); break;
            case "purchaseOrderNumber": Change(shipment, key, shipment.PurchaseOrderNumber, Required(ReadString(value), "PO number", 160), next => shipment.PurchaseOrderNumber = next, access, now); break;
            case "customer": Change(shipment, key, shipment.Customer, Required(ReadString(value), "Customer", 240), next => shipment.Customer = next, access, now); break;
            case "taskType": Change(shipment, key, shipment.TaskType, Required(ReadString(value), "Task type", 120), next => shipment.TaskType = next, access, now); break;
            case "quantity": Change(shipment, key, shipment.Quantity, NonNegativeWhole(ReadDecimal(value), "Quantity"), next => shipment.Quantity = next, access, now); break;
            case "dollarValue": Change(shipment, key, shipment.DollarValue, NonNegative(ReadDecimal(value), "Dollar value"), next => shipment.DollarValue = next, access, now); break;
            case "shipDate": Change(shipment, key, shipment.ShipDate, RequiredDate(ReadDate(value), "Ship by date"), next => shipment.ShipDate = next, access, now); break;
            case "holdReason": Change(shipment, key, shipment.HoldReason, Text(ReadString(value), null, 4000), next => shipment.HoldReason = next, access, now); break;
            case "sourceRequestedDate": Change(shipment, key, shipment.SourceRequestedDate, ReadDate(value), next => shipment.SourceRequestedDate = next, access, now); break;
            case "nextAction": Change(shipment, key, shipment.NextAction, Text(ReadString(value), null, 2000), next => shipment.NextAction = next, access, now); break;
            case "comments": throw new ArgumentException("Add comments through the shipment conversation so its history is preserved.");
            default: throw new ArgumentException($"Field '{key}' is not editable.");
        }
    }

    private void Change<T>(
        QualityShipment shipment,
        string key,
        T oldValue,
        T newValue,
        Action<T> setter,
        QualityAssuranceAccessProfile access,
        DateTimeOffset now)
    {
        if (EqualityComparer<T>.Default.Equals(oldValue, newValue)) return;
        setter(newValue);
        AddAudit(shipment, access, "FieldChanged", key, Format(oldValue), Format(newValue), now);
    }

    private static void PrepareVersion(QualityShipment shipment, long version)
    {
        if (shipment.Version != version)
            throw new DbUpdateConcurrencyException("This shipment changed. Reload before saving.");
    }

    private void AddAudit(
        QualityShipment shipment,
        QualityAssuranceAccessProfile access,
        string eventType,
        string? field,
        string? oldValue,
        string? newValue,
        DateTimeOffset occurredAt)
    {
        shipment.AuditEntries.Add(new QualityShipmentAuditEntry
        {
            EventType = eventType,
            FieldName = field,
            OldValue = oldValue,
            NewValue = newValue,
            AccountName = access.AccountName,
            DisplayName = access.DisplayName,
            OccurredAt = occurredAt
        });
    }

    private static IReadOnlyList<QualityFieldAccessDto> RestrictEditableFields(
        QualityAssuranceAccessProfile access, IReadOnlyList<string> restrictedActions) =>
        QualityFieldAccess.For(access).Select(field => field.Key != "comments" && restrictedActions.Contains("shipment-updated")
            ? field with { CanEdit = false } : field).ToList();

    private static QualityShipmentDto ToDto(QualityShipment shipment, QualityAssuranceAccessProfile access)
    {
        var canAssignment = access.HasPermission(QualityAssurancePermissions.AssignmentView);
        var canShipDate = access.HasPermission(QualityAssurancePermissions.ShipDateView);
        var canPart = access.HasPermission(QualityAssurancePermissions.PartNumberView);
        var canQuantity = access.HasPermission(QualityAssurancePermissions.QuantityView);
        var canValue = access.HasPermission(QualityAssurancePermissions.DollarValueView);
        var parts = canPart
            ? shipment.Parts
                .OrderBy(part => part.DisplayOrder)
                .ThenBy(part => part.Id)
                .Select(part => new QualityShipmentPartDto(
                    part.Id,
                    part.PartNumber,
                    canQuantity ? part.Quantity : null,
                    canValue ? part.UnitPrice : null,
                    canValue ? part.TotalValue : null,
                    part.LineItemNumber,
                    part.DisplayOrder))
                .ToList()
            : [];
        var partSummary = parts.Count > 0
            ? string.Join(", ", parts.Select(part => part.PartNumber))
            : shipment.PartNumber;
        var quantity = shipment.Parts.Count > 0
            ? shipment.Parts.Where(part => part.Quantity.HasValue).Sum(part => part.Quantity)
            : shipment.Quantity;
        var dollarValue = shipment.Parts.Count > 0
            ? shipment.Parts.Where(part => part.TotalValue.HasValue).Sum(part => part.TotalValue)
            : shipment.DollarValue;
        return new QualityShipmentDto(
            shipment.Id,
            shipment.Version,
            Visible(access, QualityAssurancePermissions.StatusView, shipment.Status),
            Visible(access, QualityAssurancePermissions.SalesOrderView, shipment.SalesOrderNumber),
            Visible(access, QualityAssurancePermissions.ShipperNumberView, shipment.ShipperNumber),
            Visible(access, QualityAssurancePermissions.QaArrivalDateView, shipment.QaArrivalDate),
            canPart ? partSummary : null,
            parts,
            Visible(access, QualityAssurancePermissions.PurchaseOrderView, shipment.PurchaseOrderNumber),
            Visible(access, QualityAssurancePermissions.CustomerView, shipment.Customer),
            Visible(access, QualityAssurancePermissions.TaskTypeView, shipment.TaskType),
            canQuantity ? quantity : null,
            canValue ? dollarValue : null,
            Visible(access, QualityAssurancePermissions.ShipDateView, shipment.ShipDate),
            Visible(access, QualityAssurancePermissions.HoldReasonView, shipment.HoldReason),
            Visible(access, QualityAssurancePermissions.SourceRequestedDateView, shipment.SourceRequestedDate),
            Visible(
                access,
                QualityAssurancePermissions.ActionView,
                shipment.IsOperationalTeamAssignment ? shipment.NextAction : "Needs assignment"),
            Visible(access, QualityAssurancePermissions.LastWorkedView, shipment.LastWorkedAt),
            Visible(access, QualityAssurancePermissions.CommentsView, shipment.Comments),
            canAssignment && shipment.IsOperationalTeamAssignment ? shipment.AssignedGroupId : null,
            canAssignment && shipment.IsOperationalTeamAssignment ? shipment.AssignedGroupName : null,
            canAssignment && shipment.IsOperationalTeamAssignment ? shipment.AssignedUserId : null,
            canAssignment && shipment.IsOperationalTeamAssignment ? shipment.AssignedDisplayName : null,
            shipment.IsShipped,
            canShipDate ? DueState(shipment) : "Hidden",
            shipment.CreatedAt,
            shipment.UpdatedAt,
            shipment.ShippedAt,
            shipment.ExternalShipmentUrl,
            shipment.ExternalShipmentStatus,
            shipment.ExternalSyncProvider,
            shipment.ExternalSyncError,
            shipment.ExternalSyncedAt,
            CompletionDays(shipment));
    }

    private static T? Visible<T>(QualityAssuranceAccessProfile access, string permission, T? value) =>
        access.HasPermission(permission) ? value : default;

    private static string DueState(QualityShipment shipment)
    {
        if (shipment.IsShipped) return "Shipped";
        if (!shipment.ShipDate.HasValue) return "No date";
        var days = shipment.ShipDate.Value.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber;
        return days < 0 ? "Past due" : days == 0 ? "Due today" : days <= 3 ? "Due soon" : "On track";
    }

    private static double? CompletionDays(QualityShipment shipment)
    {
        if (!shipment.IsShipped || !shipment.ShippedAt.HasValue || !shipment.QaArrivalDate.HasValue)
            return null;
        var shippedDate = DateOnly.FromDateTime(shipment.ShippedAt.Value.UtcDateTime);
        return Math.Max(0, shippedDate.DayNumber - shipment.QaArrivalDate.Value.DayNumber);
    }

    private static QualityQueueMetricsDto Metrics(
        IEnumerable<QualityShipment> source,
        bool includeDollarValues)
    {
        var shipments = source.ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shipped = shipments.Where(shipment => shipment.IsShipped && shipment.ShippedAt.HasValue).ToList();
        var yearStart = new DateTimeOffset(today.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var quarterStartMonth = ((today.Month - 1) / 3 * 3) + 1;
        var quarterStart = new DateTimeOffset(today.Year, quarterStartMonth, 1, 0, 0, 0, TimeSpan.Zero);
        return new QualityQueueMetricsDto(
            shipments.Count(shipment => !shipment.IsShipped),
            shipments.Count(shipment => !shipment.IsShipped && shipment.ShipDate.HasValue && shipment.ShipDate < today),
            shipped.Count,
            shipped.Count == 0
                ? null
                : shipped.Average(shipment =>
                {
                    var startedAt = shipment.QaArrivalDate.HasValue
                        ? new DateTimeOffset(
                            shipment.QaArrivalDate.Value.ToDateTime(TimeOnly.MinValue),
                            TimeSpan.Zero)
                        : shipment.CreatedAt;
                    return Math.Max(0, (shipment.ShippedAt!.Value - startedAt).TotalHours);
                }),
            includeDollarValues
                ? shipments.Where(shipment => !shipment.IsShipped).Sum(shipment => shipment.DollarValue ?? 0)
                : null,
            includeDollarValues
                ? shipped.Sum(shipment => shipment.DollarValue ?? 0)
                : null,
            includeDollarValues
                ? shipped.Where(shipment => shipment.ShippedAt >= yearStart)
                    .Sum(shipment => shipment.DollarValue ?? 0)
                : null,
            includeDollarValues
                ? shipped.Where(shipment => shipment.ShippedAt >= quarterStart)
                    .Sum(shipment => shipment.DollarValue ?? 0)
                : null);
    }

    private static string AssignmentLabel(QualityShipment shipment) =>
        string.Join(" / ", new[] { shipment.AssignedGroupName, shipment.AssignedDisplayName }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string AssignmentValue(QualityShipment shipment)
    {
        var label = AssignmentLabel(shipment);
        return string.IsNullOrWhiteSpace(label) ? "Unassigned" : label;
    }

    private static string Required(string? value, string label, int maxLength) =>
        Text(value, null, maxLength) ?? throw new ArgumentException($"{label} is required.");

    private static string ResolveShipperNumber(string? salesOrderNumber, string? shipperNumber)
    {
        var canonical = Text(salesOrderNumber, null, 80);
        var alias = Text(shipperNumber, null, 80);
        if (canonical is not null && alias is not null
            && !canonical.Equals(alias, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Sales order number and shipper number must match when both are provided.");
        return canonical ?? alias ?? throw new ArgumentException("Shipper number is required.");
    }

    private static void EnsureShipperEditable(QualityAssuranceAccessProfile access, object? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text)) return;
        if (!access.HasPermission(QualityAssurancePermissions.SalesOrderEdit)
            && !access.HasPermission(QualityAssurancePermissions.ShipperNumberEdit))
            throw new UnauthorizedAccessException("You do not have permission to set Shipper Number.");
    }

    private static string? Text(string? value, string? fallback, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (normalized?.Length > maxLength) throw new ArgumentException($"Text cannot exceed {maxLength:N0} characters.");
        return normalized;
    }

    private static decimal? NonNegative(decimal? value, string label)
    {
        if (value < 0) throw new ArgumentException($"{label} cannot be negative.");
        return value;
    }

    private static int? NonNegativeWhole(decimal? value, string label)
    {
        if (!value.HasValue) return null;
        if (value.Value < 0) throw new ArgumentException($"{label} cannot be negative.");
        if (decimal.Truncate(value.Value) != value.Value)
            throw new ArgumentException($"{label} must be a whole number.");
        if (value.Value > int.MaxValue)
            throw new ArgumentException($"{label} is too large.");
        return decimal.ToInt32(value.Value);
    }

    private static DateOnly RequiredDate(DateOnly? value, string label) =>
        value ?? throw new ArgumentException($"{label} is required.");

    private static string? ReadString(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value.GetString();

    private static DateOnly? ReadDate(JsonElement value)
    {
        var text = ReadString(value);
        return string.IsNullOrWhiteSpace(text)
            ? null
            : DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : throw new ArgumentException($"'{text}' is not a valid date.");
    }

    private static decimal? ReadDecimal(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => number,
        _ => throw new ArgumentException("Enter a valid number.")
    };

    private static string? Format<T>(T value) => value switch
    {
        null => null,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };
}
