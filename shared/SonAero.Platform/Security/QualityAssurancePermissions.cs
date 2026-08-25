namespace SonAero.Platform.Security;

public static class QualityAssurancePermissions
{
    public const string ModuleView = "quality-assurance.view";
    public const string ShipmentsView = "quality-assurance.shipments.view";
    public const string ShipmentsViewAll = "quality-assurance.shipments.view-all";
    public const string TeamDashboardView = "quality-assurance.dashboard.team-view";
    public const string ShipmentCreate = "quality-assurance.shipments.create";
    public const string ShipmentImport = "quality-assurance.shipments.import";
    public const string AssignmentView = "quality-assurance.assignments.view";
    public const string AssignmentGroup = "quality-assurance.assignments.group";
    public const string AssignmentUser = "quality-assurance.assignments.user";
    public const string MarkShipped = "quality-assurance.shipments.mark-shipped";
    public const string AuditView = "quality-assurance.audit.view";
    public const string RulesManage = "quality-assurance.rules.manage";

    public const string StatusView = "quality-assurance.fields.status.view";
    public const string StatusEdit = "quality-assurance.fields.status.edit";
    public const string SalesOrderView = "quality-assurance.fields.sales-order.view";
    public const string SalesOrderEdit = "quality-assurance.fields.sales-order.edit";
    public const string QaArrivalDateView = "quality-assurance.fields.qa-arrival-date.view";
    public const string QaArrivalDateEdit = "quality-assurance.fields.qa-arrival-date.edit";
    public const string PartNumberView = "quality-assurance.fields.part-number.view";
    public const string PartNumberEdit = "quality-assurance.fields.part-number.edit";
    public const string PurchaseOrderView = "quality-assurance.fields.purchase-order.view";
    public const string PurchaseOrderEdit = "quality-assurance.fields.purchase-order.edit";
    public const string CustomerView = "quality-assurance.fields.customer.view";
    public const string CustomerEdit = "quality-assurance.fields.customer.edit";
    public const string TaskTypeView = "quality-assurance.fields.task-type.view";
    public const string TaskTypeEdit = "quality-assurance.fields.task-type.edit";
    public const string QuantityView = "quality-assurance.fields.quantity.view";
    public const string QuantityEdit = "quality-assurance.fields.quantity.edit";
    public const string DollarValueView = "quality-assurance.fields.dollar-value.view";
    public const string DollarValueEdit = "quality-assurance.fields.dollar-value.edit";
    public const string ShipDateView = "quality-assurance.fields.ship-date.view";
    public const string ShipDateEdit = "quality-assurance.fields.ship-date.edit";
    public const string HoldReasonView = "quality-assurance.fields.hold-reason.view";
    public const string HoldReasonEdit = "quality-assurance.fields.hold-reason.edit";
    public const string SourceRequestedDateView = "quality-assurance.fields.source-requested-date.view";
    public const string SourceRequestedDateEdit = "quality-assurance.fields.source-requested-date.edit";
    public const string ActionView = "quality-assurance.fields.action.view";
    public const string ActionEdit = "quality-assurance.fields.action.edit";
    public const string LastWorkedView = "quality-assurance.fields.last-worked.view";
    public const string CommentsView = "quality-assurance.fields.comments.view";
    public const string CommentsEdit = "quality-assurance.fields.comments.edit";

    public static readonly IReadOnlyList<PermissionDefinition> FieldViewDefinitions =
    [
        Field(StatusView, "View status", "View shipment workflow status."),
        Field(SalesOrderView, "View sales order", "View sales order numbers."),
        Field(QaArrivalDateView, "View QA arrival date", "View when work arrived in Quality."),
        Field(PartNumberView, "View part number", "View shipment part numbers."),
        Field(PurchaseOrderView, "View purchase order", "View customer purchase order numbers."),
        Field(CustomerView, "View customer", "View shipment customer names."),
        Field(TaskTypeView, "View task type", "View the Quality task classification."),
        Field(QuantityView, "View quantity", "View shipment quantities."),
        Field(DollarValueView, "View dollar value", "View shipment dollar values."),
        Field(ShipDateView, "View ship date", "View required ship dates and due state."),
        Field(HoldReasonView, "View hold reason", "View shipment hold and delay reasons."),
        Field(SourceRequestedDateView, "View source request date", "View when source inspection was requested."),
        Field(ActionView, "View action", "View the current required action."),
        Field(LastWorkedView, "View last worked date", "View when the shipment record was last updated."),
        Field(CommentsView, "View comments", "View shipment comments and notes.")
    ];

    public static readonly IReadOnlyList<PermissionDefinition> FieldEditDefinitions =
    [
        Field(StatusEdit, "Edit status", "Change shipment workflow status."),
        Field(SalesOrderEdit, "Edit sales order", "Change sales order numbers."),
        Field(QaArrivalDateEdit, "Edit QA arrival date", "Change when work arrived in Quality."),
        Field(PartNumberEdit, "Edit part number", "Change shipment part numbers."),
        Field(PurchaseOrderEdit, "Edit purchase order", "Change customer purchase order numbers."),
        Field(CustomerEdit, "Edit customer", "Change shipment customer names."),
        Field(TaskTypeEdit, "Edit task type", "Change the Quality task classification."),
        Field(QuantityEdit, "Edit quantity", "Change shipment quantities."),
        Field(DollarValueEdit, "Edit dollar value", "Change shipment dollar values."),
        Field(ShipDateEdit, "Edit ship date", "Change required ship dates."),
        Field(HoldReasonEdit, "Edit hold reason", "Change shipment hold and delay reasons."),
        Field(SourceRequestedDateEdit, "Edit source request date", "Change when source inspection was requested."),
        Field(ActionEdit, "Edit action", "Change the current required action."),
        Field(CommentsEdit, "Edit comments", "Change shipment comments and notes.")
    ];

    public static readonly IReadOnlyList<PermissionDefinition> WorkflowDefinitions =
    [
        Permission(ModuleView, "View Quality Assurance", "Open the Quality Assurance module.", "Module access"),
        Permission(ShipmentsView, "View own shipping queue", "View shipments assigned directly to the current user.", "Shipping workflow"),
        Permission(ShipmentsViewAll, "View all shipments", "View open and past shipments across all groups and users.", "Shipping workflow"),
        Permission(TeamDashboardView, "View team queue statistics", "View queue volume and completion statistics for other users.", "Shipping workflow"),
        Permission(ShipmentCreate, "Create shipments", "Add new Shipping Status records.", "Shipping workflow"),
        Permission(ShipmentImport, "Import shipping status", "Import controlled Shipping Status records from the Complete List worksheet in an Excel workbook.", "Shipping workflow"),
        Permission(AssignmentView, "View assignments", "View assigned groups and individual owners.", "Assignments"),
        Permission(AssignmentGroup, "Assign groups", "Move shipments between shared groups such as Quality, Customer Service, or Sales.", "Assignments"),
        Permission(AssignmentUser, "Assign individual users", "Assign shipments to active users within the selected group.", "Assignments"),
        Permission(MarkShipped, "Mark shipments shipped", "Complete a shipment and move it to Past Shipments.", "Shipping workflow"),
        Permission(AuditView, "View shipment audit history", "View permanent field, assignment, and completion changes.", "Audit"),
        Permission(RulesManage, "Manage automatic assignment rules", "Create customer and task-type routing rules, including least-loaded assignment.", "Administration")
    ];

    public static readonly IReadOnlyList<PermissionDefinition> All =
        [.. WorkflowDefinitions, .. FieldViewDefinitions, .. FieldEditDefinitions];

    public static readonly IReadOnlyList<string> ViewerDefaults =
        [ModuleView, ShipmentsView, AssignmentView, .. FieldViewDefinitions.Select(x => x.Key)];

    public static readonly IReadOnlyList<string> EditorDefaults =
        [.. ViewerDefaults, ShipmentCreate, MarkShipped, AuditView, .. FieldEditDefinitions.Select(x => x.Key)];

    public static readonly IReadOnlyList<string> AdministratorDefaults =
        All.Select(permission => permission.Key).ToArray();

    private static PermissionDefinition Field(string key, string label, string description) =>
        Permission(key, label, description, key.EndsWith(".view", StringComparison.Ordinal) ? "Shipping fields - view" : "Shipping fields - edit");

    private static PermissionDefinition Permission(string key, string label, string description, string category) =>
        new(key, label, description, category);
}
