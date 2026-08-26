using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Dtos;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed record QualityFieldDefinition(
    string Key,
    string Label,
    string ViewPermission,
    string? EditPermission);

public static class QualityFieldAccess
{
    public static readonly IReadOnlyList<QualityFieldDefinition> All =
    [
        new("status", "Status", QualityAssurancePermissions.StatusView, QualityAssurancePermissions.StatusEdit),
        new("salesOrderNumber", "Sales Order #", QualityAssurancePermissions.SalesOrderView, QualityAssurancePermissions.SalesOrderEdit),
        new("qaArrivalDate", "QA Arrival Date", QualityAssurancePermissions.QaArrivalDateView, QualityAssurancePermissions.QaArrivalDateEdit),
        new("partNumber", "Part Number", QualityAssurancePermissions.PartNumberView, QualityAssurancePermissions.PartNumberEdit),
        new("purchaseOrderNumber", "P.O.", QualityAssurancePermissions.PurchaseOrderView, QualityAssurancePermissions.PurchaseOrderEdit),
        new("customer", "Customer", QualityAssurancePermissions.CustomerView, QualityAssurancePermissions.CustomerEdit),
        new("taskType", "Task Type", QualityAssurancePermissions.TaskTypeView, QualityAssurancePermissions.TaskTypeEdit),
        new("quantity", "Quantity", QualityAssurancePermissions.QuantityView, QualityAssurancePermissions.QuantityEdit),
        new("dollarValue", "Dollar Value", QualityAssurancePermissions.DollarValueView, QualityAssurancePermissions.DollarValueEdit),
        new("shipDate", "Ship By", QualityAssurancePermissions.ShipDateView, QualityAssurancePermissions.ShipDateEdit),
        new("holdReason", "Hold Reason", QualityAssurancePermissions.HoldReasonView, QualityAssurancePermissions.HoldReasonEdit),
        new("sourceRequestedDate", "Source Scheduled", QualityAssurancePermissions.SourceRequestedDateView, QualityAssurancePermissions.SourceRequestedDateEdit),
        new("nextAction", "Action", QualityAssurancePermissions.ActionView, QualityAssurancePermissions.ActionEdit),
        new("lastWorkedAt", "Last Worked On", QualityAssurancePermissions.LastWorkedView, null),
        new("comments", "Comments", QualityAssurancePermissions.CommentsView, QualityAssurancePermissions.CommentsEdit)
    ];

    public static IReadOnlyList<QualityFieldAccessDto> For(QualityAssuranceAccessProfile access) =>
        All.Select(field => new QualityFieldAccessDto(
            field.Key,
            field.Label,
            access.HasPermission(field.ViewPermission),
            field.EditPermission is not null && access.HasPermission(field.EditPermission)))
        .ToList();

    public static QualityFieldDefinition Find(string key) =>
        All.SingleOrDefault(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown shipping field '{key}'.", nameof(key));
}
