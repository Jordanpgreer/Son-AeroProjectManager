using System.Globalization;
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
    QualityAssignmentService assignments)
{
    public async Task<QualityShipmentListDto> ListAsync(
        QualityAssuranceAccessProfile access,
        string? status,
        string? scope,
        string? sort,
        string? search,
        CancellationToken cancellationToken)
    {
        var normalizedStatus = status?.Trim().ToLowerInvariant() switch
        {
            "shipped" => "shipped",
            "all" => "all",
            _ => "open"
        };
        var normalizedScope = NormalizeScope(access, scope);
        var normalizedSort = string.Equals(sort, "ship-date", StringComparison.OrdinalIgnoreCase)
            ? "ship-date"
            : "oldest";

        var query = ApplyVisibility(db.Shipments.AsNoTracking(), access, normalizedScope);
        query = normalizedStatus switch
        {
            "shipped" => query.Where(shipment => shipment.IsShipped),
            "all" => query,
            _ => query.Where(shipment => !shipment.IsShipped)
        };
        query = ApplySearch(query, access, search);
        var total = await query.CountAsync(cancellationToken);
        var sorted = normalizedSort == "ship-date"
            ? query.OrderBy(shipment => shipment.ShipDate == null)
                .ThenBy(shipment => shipment.ShipDate)
                .ThenBy(shipment => shipment.Id)
            : query.OrderBy(shipment => shipment.QaArrivalDate == null)
                .ThenBy(shipment => shipment.QaArrivalDate)
                .ThenBy(shipment => shipment.Id);
        var shipments = await sorted.Take(500).ToListAsync(cancellationToken);
        return new QualityShipmentListDto(
            shipments.Select(shipment => ToDto(shipment, access)).ToList(),
            total,
            normalizedStatus,
            normalizedScope,
            normalizedSort,
            QualityFieldAccess.For(access));
    }

    public async Task<QualityDashboardDto> DashboardAsync(
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        var canViewTeam = access.HasPermission(QualityAssurancePermissions.TeamDashboardView)
            || access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll);
        var canReviewUnassigned = access.HasPermission(QualityAssurancePermissions.AssignmentGroup);
        var groupIds = access.Groups.Select(group => group.Id).ToList();
        var dashboardQuery = db.Shipments.AsNoTracking();
        if (!access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll))
        {
            dashboardQuery = canViewTeam
                ? dashboardQuery.Where(shipment => shipment.AssignedUserId == access.UserId
                    || (shipment.AssignedGroupId.HasValue && groupIds.Contains(shipment.AssignedGroupId.Value))
                    || (canReviewUnassigned && !shipment.AssignedGroupId.HasValue && !shipment.AssignedUserId.HasValue))
                : dashboardQuery.Where(shipment => shipment.AssignedUserId == access.UserId
                    || (canReviewUnassigned && !shipment.AssignedGroupId.HasValue && !shipment.AssignedUserId.HasValue));
        }
        var all = await dashboardQuery.ToListAsync(cancellationToken);
        var mine = all.Where(shipment => shipment.AssignedUserId == access.UserId).ToList();
        var reviewQueue = canReviewUnassigned
            ? all.Where(shipment => shipment.AssignedUserId == access.UserId
                || (!shipment.AssignedGroupId.HasValue && !shipment.AssignedUserId.HasValue))
            : mine;
        var queue = reviewQueue.Where(shipment => !shipment.IsShipped)
            .OrderBy(shipment => shipment.QaArrivalDate ?? DateOnly.MaxValue)
            .ThenBy(shipment => shipment.CreatedAt)
            .ThenBy(shipment => shipment.ShipDate ?? DateOnly.MaxValue)
            .Take(12)
            .Select(shipment => ToDto(shipment, access))
            .ToList();
        var team = new List<QualityPersonQueueDto>();
        if (canViewTeam)
        {
            var users = await accessStore.GetUsersAsync(null, cancellationToken);
            if (!access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll))
            {
                var permittedGroupIds = groupIds.ToHashSet();
                users = users.Where(user => user.GroupIds.Any(permittedGroupIds.Contains)).ToList();
            }
            team = users.Select(user => new QualityPersonQueueDto(
                    user.Id,
                    user.DisplayName,
                    user.AccountName,
                    Metrics(all.Where(shipment => shipment.AssignedUserId == user.Id))))
                .OrderByDescending(user => user.Metrics.Overdue)
                .ThenByDescending(user => user.Metrics.Open)
                .ThenBy(user => user.DisplayName)
                .ToList();
        }
        return new QualityDashboardDto(Metrics(mine), queue, team, canViewTeam);
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

        var now = DateTimeOffset.UtcNow;
        var shipment = new QualityShipment
        {
            Status = Required(dto.Status ?? "WIP", "Status", 80),
            SalesOrderNumber = Required(dto.SalesOrderNumber, "Sales order number", 80),
            QaArrivalDate = dto.QaArrivalDate,
            PartNumber = Required(dto.PartNumber, "Part number", 160),
            PurchaseOrderNumber = Text(dto.PurchaseOrderNumber, null, 160),
            Customer = Required(dto.Customer, "Customer", 240),
            TaskType = Required(dto.TaskType ?? "General", "Task type", 120),
            Quantity = NonNegative(dto.Quantity, "Quantity"),
            DollarValue = NonNegative(dto.DollarValue, "Dollar value"),
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
        var rule = await assignments.ApplyFirstMatchingRuleAsync(shipment, cancellationToken);
        if (rule is null)
        {
            var group = access.Groups.FirstOrDefault();
            shipment.AssignedGroupId = group?.Id;
            shipment.AssignedGroupName = group?.Name;
            shipment.AssignedUserId = access.UserId;
            shipment.AssignedAccountName = access.AccountName;
            shipment.AssignedDisplayName = access.DisplayName;
        }
        db.Shipments.Add(shipment);
        AddAudit(shipment, access, "Created", null, null, shipment.SalesOrderNumber, now);
        AddAudit(
            shipment,
            access,
            rule is null ? "Assigned" : "AutoAssigned",
            "assignment",
            null,
            AssignmentLabel(shipment),
            now);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(shipment, access);
    }

    public async Task<QualityShipmentDto?> PatchAsync(
        int id,
        QualityShipmentPatchDto dto,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (shipment is null) return null;
        EnsureRecordAccess(shipment, access);
        PrepareVersion(shipment, dto.Version);
        var now = DateTimeOffset.UtcNow;
        var changedRoutingInput = false;
        foreach (var change in dto.Changes)
        {
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
        return ToDto(shipment, access);
    }

    public async Task<QualityShipmentDto?> AssignAsync(
        int id,
        QualityShipmentAssignmentDto dto,
        QualityAssuranceAccessProfile access,
        CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (shipment is null) return null;
        EnsureRecordAccess(shipment, access);
        PrepareVersion(shipment, dto.Version);
        if (dto.GroupId != shipment.AssignedGroupId
            && !access.HasPermission(QualityAssurancePermissions.AssignmentGroup))
            throw new UnauthorizedAccessException("You do not have permission to move shipments between groups.");
        if (dto.UserId != shipment.AssignedUserId
            && !access.HasPermission(QualityAssurancePermissions.AssignmentUser))
            throw new UnauthorizedAccessException("You do not have permission to assign individual users.");

        var groups = await accessStore.GetGroupsAsync(cancellationToken);
        var group = dto.GroupId.HasValue
            ? groups.SingleOrDefault(candidate => candidate.Id == dto.GroupId.Value)
                ?? throw new ArgumentException("Select an existing shared group.")
            : null;
        QualityDirectoryUser? user = null;
        if (dto.UserId.HasValue)
        {
            if (group is null) throw new ArgumentException("Select a group before assigning a user.");
            if (!access.HasPermission(QualityAssurancePermissions.AssignmentGroup)
                && !access.HasPermission(QualityAssurancePermissions.ShipmentsViewAll)
                && access.Groups.All(candidate => candidate.Id != group.Id))
                throw new UnauthorizedAccessException("Group leads can assign users only within their own groups.");
            user = (await accessStore.GetUsersAsync(group.Id, cancellationToken))
                .SingleOrDefault(candidate => candidate.Id == dto.UserId.Value)
                ?? throw new ArgumentException("The selected user must be active and assigned to the selected group.");
        }

        var old = AssignmentLabel(shipment);
        var oldAction = shipment.NextAction;
        shipment.AssignedGroupId = group?.Id;
        shipment.AssignedGroupName = group?.Name;
        shipment.AssignedUserId = user?.Id;
        shipment.AssignedAccountName = user?.AccountName;
        shipment.AssignedDisplayName = user?.DisplayName;
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
        var shipment = await db.Shipments.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
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
                    || (access.HasPermission(QualityAssurancePermissions.AssignmentGroup)
                        && !shipment.AssignedGroupId.HasValue
                        && !shipment.AssignedUserId.HasValue));
        }
        if (access.HasPermission(QualityAssurancePermissions.AssignmentGroup))
            return query.Where(shipment => shipment.AssignedUserId == access.UserId
                || (!shipment.AssignedGroupId.HasValue && !shipment.AssignedUserId.HasValue));
        return query.Where(shipment => shipment.AssignedUserId == access.UserId);
    }

    private static IQueryable<QualityShipment> ApplySearch(
        IQueryable<QualityShipment> query,
        QualityAssuranceAccessProfile access,
        string? search)
    {
        var value = search?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return query;
        var canSalesOrder = access.HasPermission(QualityAssurancePermissions.SalesOrderView);
        var canPart = access.HasPermission(QualityAssurancePermissions.PartNumberView);
        var canPo = access.HasPermission(QualityAssurancePermissions.PurchaseOrderView);
        var canCustomer = access.HasPermission(QualityAssurancePermissions.CustomerView);
        var canType = access.HasPermission(QualityAssurancePermissions.TaskTypeView);
        var canAction = access.HasPermission(QualityAssurancePermissions.ActionView);
        var canComments = access.HasPermission(QualityAssurancePermissions.CommentsView);
        return query.Where(shipment =>
            (canSalesOrder && shipment.SalesOrderNumber.Contains(value))
            || (canPart && shipment.PartNumber.Contains(value))
            || (canPo && shipment.PurchaseOrderNumber != null && shipment.PurchaseOrderNumber.Contains(value))
            || (canCustomer && shipment.Customer.Contains(value))
            || (canType && shipment.TaskType.Contains(value))
            || (canAction && shipment.NextAction != null && shipment.NextAction.Contains(value))
            || (canComments && shipment.Comments != null && shipment.Comments.Contains(value)));
    }

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
        if (!shipment.AssignedGroupId.HasValue
            && !shipment.AssignedUserId.HasValue
            && access.HasPermission(QualityAssurancePermissions.AssignmentGroup)) return;
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
            case "salesOrderNumber": Change(shipment, key, shipment.SalesOrderNumber, Required(ReadString(value), "Sales order number", 80), next => shipment.SalesOrderNumber = next, access, now); break;
            case "qaArrivalDate": Change(shipment, key, shipment.QaArrivalDate, ReadDate(value), next => shipment.QaArrivalDate = next, access, now); break;
            case "partNumber": Change(shipment, key, shipment.PartNumber, Required(ReadString(value), "Part number", 160), next => shipment.PartNumber = next, access, now); break;
            case "purchaseOrderNumber": Change(shipment, key, shipment.PurchaseOrderNumber, Text(ReadString(value), null, 160), next => shipment.PurchaseOrderNumber = next, access, now); break;
            case "customer": Change(shipment, key, shipment.Customer, Required(ReadString(value), "Customer", 240), next => shipment.Customer = next, access, now); break;
            case "taskType": Change(shipment, key, shipment.TaskType, Required(ReadString(value), "Task type", 120), next => shipment.TaskType = next, access, now); break;
            case "quantity": Change(shipment, key, shipment.Quantity, NonNegative(ReadDecimal(value), "Quantity"), next => shipment.Quantity = next, access, now); break;
            case "dollarValue": Change(shipment, key, shipment.DollarValue, NonNegative(ReadDecimal(value), "Dollar value"), next => shipment.DollarValue = next, access, now); break;
            case "shipDate": Change(shipment, key, shipment.ShipDate, ReadDate(value), next => shipment.ShipDate = next, access, now); break;
            case "holdReason": Change(shipment, key, shipment.HoldReason, Text(ReadString(value), null, 4000), next => shipment.HoldReason = next, access, now); break;
            case "sourceRequestedDate": Change(shipment, key, shipment.SourceRequestedDate, ReadDate(value), next => shipment.SourceRequestedDate = next, access, now); break;
            case "nextAction": Change(shipment, key, shipment.NextAction, Text(ReadString(value), null, 2000), next => shipment.NextAction = next, access, now); break;
            case "comments": Change(shipment, key, shipment.Comments, Text(ReadString(value), null, 8000), next => shipment.Comments = next, access, now); break;
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
        return new QualityShipmentDto(
            shipment.Id,
            shipment.Version,
            Visible(access, QualityAssurancePermissions.StatusView, shipment.Status),
            Visible(access, QualityAssurancePermissions.SalesOrderView, shipment.SalesOrderNumber),
            Visible(access, QualityAssurancePermissions.QaArrivalDateView, shipment.QaArrivalDate),
            Visible(access, QualityAssurancePermissions.PartNumberView, shipment.PartNumber),
            Visible(access, QualityAssurancePermissions.PurchaseOrderView, shipment.PurchaseOrderNumber),
            Visible(access, QualityAssurancePermissions.CustomerView, shipment.Customer),
            Visible(access, QualityAssurancePermissions.TaskTypeView, shipment.TaskType),
            Visible(access, QualityAssurancePermissions.QuantityView, shipment.Quantity),
            Visible(access, QualityAssurancePermissions.DollarValueView, shipment.DollarValue),
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
            shipment.ShippedAt);
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

    private static QualityQueueMetricsDto Metrics(IEnumerable<QualityShipment> source)
    {
        var shipments = source.ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shipped = shipments.Where(shipment => shipment.IsShipped && shipment.ShippedAt.HasValue).ToList();
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
                }));
    }

    private static string AssignmentLabel(QualityShipment shipment) =>
        string.Join(" / ", new[] { shipment.AssignedGroupName, shipment.AssignedDisplayName }.Where(value => !string.IsNullOrWhiteSpace(value)));

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
