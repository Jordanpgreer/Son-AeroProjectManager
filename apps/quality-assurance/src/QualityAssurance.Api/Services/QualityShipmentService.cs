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
    QualityLegacyAssignmentReconciler legacyAssignments,
    IConfiguration? configuration = null,
    IQualityShipmentSyncService? integrationSync = null)
{
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
        EnsureRecordAccess(shipment, access);
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
        var normalizedScope = NormalizeScope(access, scope);
        var normalizedSort = NormalizeSort(access, sort);
        var normalizedDirection = NormalizeDirection(direction);

        var query = ApplyVisibility(db.Shipments.AsNoTracking().Include(shipment => shipment.Parts), access, normalizedScope);
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
            QualityFieldAccess.For(access));
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
        var normalizedScope = NormalizeScope(access, scope);
        var query = ApplyVisibility(db.Shipments.AsNoTracking().Include(shipment => shipment.Parts), access, normalizedScope);
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
        var normalizedScope = NormalizeScope(access, scope);
        var query = ApplyVisibility(db.Shipments.AsNoTracking(), access, normalizedScope);
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

    public async Task<QualityDashboardDto> DashboardAsync(
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        await legacyAssignments.ReconcileAsync(cancellationToken);
        var canViewTeam = access.HasPermission(QualityAssurancePermissions.TeamDashboardView)
            || access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll);
        var canReviewUnassigned = CanReviewUnassigned(access);
        var canAssignGroup = access.HasPermission(QualityAssurancePermissions.AssignmentGroup);
        var canAssignUser = access.HasPermission(QualityAssurancePermissions.AssignmentUser);
        var canViewAssignment = access.HasPermission(QualityAssurancePermissions.AssignmentView);
        var canAssign = canViewAssignment
            && (canAssignGroup || canAssignUser);
        var canViewDollarValue = access.HasPermission(QualityAssurancePermissions.DollarValueView);
        var groupIds = access.Groups.Select(group => group.Id).ToList();
        IQueryable<QualityShipment> dashboardQuery = db.Shipments
            .AsNoTracking()
            .Include(shipment => shipment.Parts);
        if (!access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll))
        {
            dashboardQuery = dashboardQuery.Where(shipment => shipment.AssignedUserId == access.UserId
                || (!shipment.AssignedUserId.HasValue
                    && shipment.AssignedGroupId.HasValue
                    && groupIds.Contains(shipment.AssignedGroupId.Value))
                || (canViewTeam
                    && shipment.AssignedGroupId.HasValue
                    && groupIds.Contains(shipment.AssignedGroupId.Value))
                || (canReviewUnassigned && !shipment.AssignedGroupId.HasValue && !shipment.AssignedUserId.HasValue));
        }
        var all = await dashboardQuery.ToListAsync(cancellationToken);
        var reviewQueue = all.Where(shipment => shipment.AssignedUserId == access.UserId
            || (!shipment.AssignedUserId.HasValue
                && shipment.AssignedGroupId.HasValue
                && groupIds.Contains(shipment.AssignedGroupId.Value))
            || (canReviewUnassigned && !shipment.AssignedGroupId.HasValue && !shipment.AssignedUserId.HasValue));
        var queue = reviewQueue.Where(shipment => !shipment.IsShipped)
            .OrderBy(shipment => shipment.ShipDate ?? DateOnly.MaxValue)
            .ThenBy(shipment => shipment.CreatedAt)
            .ThenBy(shipment => shipment.QaArrivalDate ?? DateOnly.MaxValue)
            .Take(12)
            .Select(shipment => ToDto(shipment, access))
            .ToList();
        var unassigned = canReviewUnassigned
            ? all.Where(shipment => !shipment.AssignedGroupId.HasValue
                    && !shipment.AssignedUserId.HasValue)
                .ToList()
            : [];
        var groupQueue = all.Where(shipment => shipment.AssignedGroupId.HasValue
                && !shipment.AssignedUserId.HasValue)
            .ToList();
        var team = new List<QualityPersonQueueDto>();
        if (canViewTeam)
        {
            var users = await accessStore.GetUsersWithPermissionAsync(
                QualityAssurancePermissions.AssignmentEligible,
                cancellationToken);
            if (!access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll))
            {
                var permittedGroupIds = groupIds.ToHashSet();
                users = users.Where(user => user.GroupIds.Any(permittedGroupIds.Contains)).ToList();
            }
            team = users.Select(user =>
                {
                    var personShipments = all.Where(shipment => shipment.AssignedUserId == user.Id).ToList();
                    var openShipments = personShipments.Where(shipment => !shipment.IsShipped)
                        .OrderBy(shipment => shipment.ShipDate ?? DateOnly.MaxValue)
                        .ThenBy(shipment => shipment.QaArrivalDate ?? DateOnly.MaxValue)
                        .ThenBy(shipment => shipment.Id)
                        .Take(20)
                        .Select(shipment => ToDto(shipment, access))
                        .ToList();
                    return new QualityPersonQueueDto(
                        user.Id,
                        user.DisplayName,
                        user.AccountName,
                        Metrics(personShipments, canViewDollarValue),
                        openShipments);
                })
                .OrderByDescending(user => user.Metrics.Overdue)
                .ThenByDescending(user => user.Metrics.Open)
                .ThenBy(user => user.DisplayName)
                .ToList();
        }
        return new QualityDashboardDto(
            Metrics(reviewQueue, canViewDollarValue),
            queue,
            team,
            Metrics(groupQueue, canViewDollarValue),
            groupQueue.Where(shipment => !shipment.IsShipped)
                .OrderBy(shipment => shipment.ShipDate ?? DateOnly.MaxValue)
                .ThenBy(shipment => shipment.QaArrivalDate ?? DateOnly.MaxValue)
                .ThenBy(shipment => shipment.Id)
                .Take(20)
                .Select(shipment => ToDto(shipment, access))
                .ToList(),
            Metrics(unassigned, canViewDollarValue),
            unassigned.Where(shipment => !shipment.IsShipped)
                .OrderBy(shipment => shipment.ShipDate ?? DateOnly.MaxValue)
                .ThenBy(shipment => shipment.QaArrivalDate ?? DateOnly.MaxValue)
                .ThenBy(shipment => shipment.Id)
                .Take(20)
                .Select(shipment => ToDto(shipment, access))
                .ToList(),
            canReviewUnassigned,
            canViewTeam,
            canViewAssignment,
            canAssign,
            canAssignGroup,
            canAssignUser,
            canViewDollarValue,
            QualityFieldAccess.For(access));
    }

    public async Task<QualityShipmentDto> CreateAsync(
        QualityShipmentCreateDto dto,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        EnsureEditable(access, "status", dto.Status);
        EnsureEditable(access, "salesOrderNumber", dto.SalesOrderNumber);
        EnsureEditable(access, "qaArrivalDate", dto.QaArrivalDate);
        EnsureEditable(access, "partNumber", dto.PartNumber);
        EnsureEditable(access, "purchaseOrderNumber", dto.PurchaseOrderNumber);
        EnsureEditable(access, "customer", dto.Customer);
        EnsureEditable(access, "taskType", dto.TaskType);
        EnsureEditable(access, "quantity", dto.Quantity);
        EnsureEditable(access, "dollarValue", dto.DollarValue);
        EnsureEditable(access, "shipDate", dto.ShipDate);
        EnsureEditable(access, "holdReason", dto.HoldReason);
        EnsureEditable(access, "sourceRequestedDate", dto.SourceRequestedDate);
        EnsureEditable(access, "nextAction", dto.NextAction);
        EnsureEditable(access, "comments", dto.Comments);
        if (dto.Parts is { Count: > 0 }) EnsurePartsEditable(access, dto.Parts);

        var normalizedParts = NormalizeParts(dto.Parts, dto.PartNumber, dto.Quantity, dto.DollarValue);
        var now = DateTimeOffset.UtcNow;
        var shipment = new QualityShipment
        {
            Status = Required(dto.Status ?? "WIP", "Status", 80),
            SalesOrderNumber = Required(dto.SalesOrderNumber, "Shipper number", 80),
            QaArrivalDate = RequiredDate(dto.QaArrivalDate, "Shipment arrival date"),
            PartNumber = normalizedParts[0].PartNumber,
            PurchaseOrderNumber = Required(dto.PurchaseOrderNumber, "PO number", 160),
            Customer = Required(dto.Customer, "Customer", 240),
            TaskType = Required(dto.TaskType ?? "General", "Task type", 120),
            Quantity = normalizedParts.Sum(part => (decimal?)(part.Quantity ?? 0)),
            DollarValue = normalizedParts.Sum(part => part.TotalValue ?? 0),
            ShipDate = RequiredDate(dto.ShipDate, "Ship by date"),
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
            var responsibleGroupIds = (await accessStore.GetGroupsWithPermissionAsync(
                    QualityAssurancePermissions.ResponsibleGroupEligible,
                    cancellationToken))
                .Select(group => group.Id)
                .ToHashSet();
            var group = access.Groups.FirstOrDefault(candidate => responsibleGroupIds.Contains(candidate.Id));
            shipment.AssignedGroupId = group?.Id;
            shipment.AssignedGroupName = group?.Name;
            if (group is not null && access.HasPermission(QualityAssurancePermissions.AssignmentEligible))
            {
                shipment.AssignedUserId = access.UserId;
                shipment.AssignedAccountName = access.AccountName;
                shipment.AssignedDisplayName = access.DisplayName;
            }
        }
        db.Shipments.Add(shipment);
        AddAudit(shipment, access, "Created", null, null, shipment.SalesOrderNumber, now);
        AddAudit(
            shipment,
            access,
            rule is null && shipment.AssignedGroupId is null ? "AssignmentPending" : rule is null ? "Assigned" : "AutoAssigned",
            "assignment",
            null,
            AssignmentLabel(shipment),
            now);
        await db.SaveChangesAsync(cancellationToken);
        if (integrationSync is not null)
            await integrationSync.TrySyncShipmentAsync(shipment.Id, cancellationToken);
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
        EnsureRecordAccess(shipment, access);
        PrepareVersion(shipment, dto.Version);
        var now = DateTimeOffset.UtcNow;
        var changedRoutingInput = false;
        foreach (var change in dto.Changes)
        {
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
        if (changedRoutingInput && shipment.AssignedGroupId is null)
            await assignments.ApplyFirstMatchingRuleAsync(shipment, cancellationToken);
        shipment.LastWorkedAt = now;
        shipment.UpdatedAt = now;
        shipment.UpdatedByAccountName = access.AccountName;
        shipment.UpdatedByDisplayName = access.DisplayName;
        shipment.Version++;
        await db.SaveChangesAsync(cancellationToken);
        if (integrationSync is not null && dto.Changes.Keys.Any(key =>
                string.Equals(key, "salesOrderNumber", StringComparison.OrdinalIgnoreCase)))
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
        EnsureRecordAccess(shipment, access);
        PrepareVersion(shipment, dto.Version);
        if (dto.GroupId != shipment.AssignedGroupId
            && !access.HasPermission(QualityAssurancePermissions.AssignmentGroup))
            throw new UnauthorizedAccessException("You do not have permission to move shipments between groups.");
        if (dto.UserId != shipment.AssignedUserId
            && !access.HasPermission(QualityAssurancePermissions.AssignmentUser))
            throw new UnauthorizedAccessException("You do not have permission to assign individual users.");

        if (dto.GroupId == shipment.AssignedGroupId && dto.UserId == shipment.AssignedUserId)
        {
            if (string.IsNullOrWhiteSpace(shipment.LegacyAssigneeTag)) return ToDto(shipment, access);
            if (!access.HasPermission(QualityAssurancePermissions.AssignmentGroup)
                && !access.HasPermission(QualityAssurancePermissions.AssignmentUser))
                throw new UnauthorizedAccessException("You do not have permission to confirm legacy assignments.");
            var legacyTag = shipment.LegacyAssigneeTag;
            var decisionAt = DateTimeOffset.UtcNow;
            shipment.LegacyAssigneeTag = null;
            shipment.LastWorkedAt = decisionAt;
            shipment.UpdatedAt = decisionAt;
            shipment.UpdatedByAccountName = access.AccountName;
            shipment.UpdatedByDisplayName = access.DisplayName;
            shipment.Version++;
            AddAudit(
                shipment,
                access,
                "Assigned",
                "assignment",
                $"Legacy tag: {legacyTag}",
                AssignmentValue(shipment),
                decisionAt);
            await db.SaveChangesAsync(cancellationToken);
            return ToDto(shipment, access);
        }

        var groups = await accessStore.GetGroupsWithPermissionAsync(
            QualityAssurancePermissions.ResponsibleGroupEligible,
            cancellationToken);
        var group = dto.GroupId.HasValue
            ? groups.SingleOrDefault(candidate => candidate.Id == dto.GroupId.Value)
                ?? throw new ArgumentException("Select a Responsible Group enabled for Quality assignment in Arda Access.")
            : null;
        QualityDirectoryUser? user = null;
        if (dto.UserId.HasValue)
        {
            if (group is null) throw new ArgumentException("Select a group before assigning a user.");
            if (!access.HasPermission(QualityAssurancePermissions.AssignmentGroup)
                && !access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll)
                && access.Groups.All(candidate => candidate.Id != group.Id))
                throw new UnauthorizedAccessException("Group leads can assign users only within their own groups.");
            user = (await accessStore.GetUsersWithPermissionAsync(
                    QualityAssurancePermissions.AssignmentEligible,
                    cancellationToken))
                .Where(candidate => candidate.GroupIds.Contains(group.Id))
                .SingleOrDefault(candidate => candidate.Id == dto.UserId.Value)
                ?? throw new ArgumentException("The selected user must be active, eligible for Quality assignment, and assigned to the selected group.");
        }

        var old = AssignmentLabel(shipment);
        var oldAction = shipment.NextAction;
        shipment.AssignedGroupId = group?.Id;
        shipment.AssignedGroupName = group?.Name;
        shipment.AssignedUserId = user?.Id;
        shipment.AssignedAccountName = user?.AccountName;
        shipment.AssignedDisplayName = user?.DisplayName;
        shipment.LegacyAssigneeTag = null;
        if (user is not null) shipment.NextAction = user.DisplayName;
        var now = DateTimeOffset.UtcNow;
        shipment.LastWorkedAt = now;
        shipment.UpdatedAt = now;
        shipment.UpdatedByAccountName = access.AccountName;
        shipment.UpdatedByDisplayName = access.DisplayName;
        shipment.Version++;
        AddAudit(shipment, access, "Assigned", "assignment", old, AssignmentLabel(shipment), now);
        if (oldAction != shipment.NextAction)
            AddAudit(shipment, access, "Updated", "nextAction", oldAction, shipment.NextAction, now);
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
        EnsureRecordAccess(shipment, access);
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
        EnsureRecordAccess(shipment, access);
        PrepareVersion(shipment, version);
        if (shipment.IsShipped)
            throw new ArgumentException("A shipped record cannot be returned to the Shipping queue.");

        var qualityGroupName = configuration?["QualityWorkflow:QualityGroupName"] ?? "Quality";
        if (!string.Equals(shipment.AssignedGroupName, qualityGroupName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"QA Complete is available only while the record is assigned to the {qualityGroupName} group.");

        var shippingGroupName = configuration?["QualityWorkflow:ShippingGroupName"] ?? "Shipping";
        var shippingGroup = (await accessStore.GetGroupsWithPermissionAsync(
                QualityAssurancePermissions.ResponsibleGroupEligible,
                cancellationToken))
            .FirstOrDefault(group => string.Equals(group.Name, shippingGroupName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The {shippingGroupName} group must be enabled as a Quality Responsible Group before QA Complete can route work to it.");

        var now = DateTimeOffset.UtcNow;
        var oldStatus = shipment.Status;
        var oldAssignment = AssignmentValue(shipment);
        shipment.Status = "Ready to Ship";
        shipment.AssignedGroupId = shippingGroup.Id;
        shipment.AssignedGroupName = shippingGroup.Name;
        shipment.AssignedUserId = null;
        shipment.AssignedAccountName = null;
        shipment.AssignedDisplayName = null;
        shipment.NextAction = shippingGroup.Name;
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
        EnsureRecordAccess(shipment, access);
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
                entry.OldValue,
                entry.NewValue,
                entry.AccountName,
                entry.DisplayName,
                entry.OccurredAt))
            .ToList();
    }

    private IQueryable<QualityShipment> ApplyVisibility(
        IQueryable<QualityShipment> query,
        QualityAssuranceAccessProfile access,
        string scope)
    {
        if (scope == "all" && access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll)) return query;
        if (scope == "team" && (access.HasPermission(QualityAssurancePermissions.TeamDashboardView)
            || access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll)))
        {
            var groupIds = access.Groups.Select(group => group.Id).ToList();
            return access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll)
                ? query
                : query.Where(shipment => shipment.AssignedUserId == access.UserId
                    || (shipment.AssignedGroupId.HasValue && groupIds.Contains(shipment.AssignedGroupId.Value))
                    || (CanReviewUnassigned(access)
                        && !shipment.AssignedGroupId.HasValue
                        && !shipment.AssignedUserId.HasValue));
        }
        if (CanReviewUnassigned(access))
        {
            var groupIds = access.Groups.Select(group => group.Id).ToList();
            return query.Where(shipment => shipment.AssignedUserId == access.UserId
                || (!shipment.AssignedUserId.HasValue
                    && shipment.AssignedGroupId.HasValue
                    && groupIds.Contains(shipment.AssignedGroupId.Value))
                || (!shipment.AssignedGroupId.HasValue && !shipment.AssignedUserId.HasValue));
        }
        var mineGroupIds = access.Groups.Select(group => group.Id).ToList();
        return query.Where(shipment => shipment.AssignedUserId == access.UserId
            || (!shipment.AssignedUserId.HasValue
                && shipment.AssignedGroupId.HasValue
                && mineGroupIds.Contains(shipment.AssignedGroupId.Value)));
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
            query = query.Where(shipment => shipment.Status.ToLower() == normalizedShipmentStatus);

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
            return query.Where(shipment => !shipment.AssignedGroupId.HasValue && !shipment.AssignedUserId.HasValue);
        if (normalizedAssignee.StartsWith("user:", StringComparison.Ordinal)
            && int.TryParse(normalizedAssignee[5..], out var userId))
            return query.Where(shipment => shipment.AssignedUserId == userId);
        if (normalizedAssignee.StartsWith("group:", StringComparison.Ordinal)
            && int.TryParse(normalizedAssignee[6..], out var groupId))
            return query.Where(shipment => shipment.AssignedGroupId == groupId && !shipment.AssignedUserId.HasValue);
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
        "shipped" => "shipped",
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

    private static string NormalizeScope(QualityAssuranceAccessProfile access, string? scope)
    {
        if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase)
            && access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll)) return "all";
        if (string.Equals(scope, "team", StringComparison.OrdinalIgnoreCase)
            && (access.HasPermission(QualityAssurancePermissions.TeamDashboardView)
                || access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll))) return "team";
        return "mine";
    }

    private static void EnsureRecordAccess(QualityShipment shipment, QualityAssuranceAccessProfile access)
    {
        if (access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll)) return;
        if (shipment.AssignedUserId == access.UserId) return;
        if (!shipment.AssignedUserId.HasValue
            && shipment.AssignedGroupId.HasValue
            && access.Groups.Any(group => group.Id == shipment.AssignedGroupId.Value)) return;
        if (!shipment.AssignedGroupId.HasValue
            && !shipment.AssignedUserId.HasValue
            && CanReviewUnassigned(access)) return;
        if (access.HasPermission(QualityAssurancePermissions.TeamDashboardView)
            && shipment.AssignedGroupId.HasValue
            && access.Groups.Any(group => group.Id == shipment.AssignedGroupId.Value)) return;
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
            shipment.Parts.Add(new QualityShipmentPart
            {
                PartNumber = part.PartNumber,
                Quantity = part.Quantity,
                UnitPrice = part.UnitPrice,
                TotalValue = part.TotalValue,
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
            return [new NormalizedShipmentPart(partNumber, quantity, unitPrice, totalValue, 0)];
        }

        if (parts.Count > 100)
            throw new ArgumentException("A shipping record cannot contain more than 100 part lines.");

        var normalized = parts.Select((part, index) =>
        {
            var partNumber = Required(part.PartNumber, $"Part number on line {index + 1}", 160);
            if (part.Quantity < 0) throw new ArgumentException($"Quantity on line {index + 1} cannot be negative.");
            var unitPrice = NonNegative(part.UnitPrice, $"Unit price on line {index + 1}");
            decimal? totalValue = part.Quantity.HasValue && unitPrice.HasValue
                ? decimal.Round(part.Quantity.Value * unitPrice.Value, 2, MidpointRounding.AwayFromZero)
                : null;
            return new NormalizedShipmentPart(partNumber, part.Quantity, unitPrice, totalValue, index);
        }).ToList();

        var duplicate = normalized
            .GroupBy(part => part.PartNumber, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Part number '{duplicate.Key}' is listed more than once.");
        return normalized;
    }

    private static string FormatParts(IEnumerable<QualityShipmentPart> parts) => string.Join(
        "; ",
        parts.OrderBy(part => part.DisplayOrder).Select(part =>
            $"{part.PartNumber} | Qty {part.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "Not set"} | Unit {part.UnitPrice?.ToString("0.00", CultureInfo.InvariantCulture) ?? "Not set"}"));

    private sealed record NormalizedShipmentPart(
        string PartNumber,
        int? Quantity,
        decimal? UnitPrice,
        decimal? TotalValue,
        int DisplayOrder);

    private void ApplyChange(
        QualityShipment shipment,
        string key,
        JsonElement value,
        QualityAssuranceAccessProfile access,
        DateTimeOffset now)
    {
        switch (key)
        {
            case "status": Change(shipment, key, shipment.Status, Required(ReadString(value), "Status", 80), next => shipment.Status = next, access, now); break;
            case "salesOrderNumber": Change(shipment, key, shipment.SalesOrderNumber, Required(ReadString(value), "Shipper number", 80), next => shipment.SalesOrderNumber = next, access, now); break;
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
            Visible(access, QualityAssurancePermissions.ActionView, shipment.NextAction),
            Visible(access, QualityAssurancePermissions.LastWorkedView, shipment.LastWorkedAt),
            Visible(access, QualityAssurancePermissions.CommentsView, shipment.Comments),
            canAssignment ? shipment.AssignedGroupId : null,
            canAssignment ? shipment.AssignedGroupName : null,
            canAssignment ? shipment.AssignedUserId : null,
            canAssignment ? shipment.AssignedDisplayName : null,
            shipment.IsShipped,
            canShipDate ? DueState(shipment) : "Hidden",
            shipment.CreatedAt,
            shipment.UpdatedAt,
            shipment.ShippedAt,
            shipment.ExternalShipmentUrl,
            shipment.ExternalShipmentStatus,
            shipment.ExternalSyncProvider,
            shipment.ExternalSyncError,
            shipment.ExternalSyncedAt);
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
