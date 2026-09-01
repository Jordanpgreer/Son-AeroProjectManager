using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Models;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed class QualityShipmentImportService(
    QualityAssuranceDbContext db,
    IQualityAssuranceAccessStore accessStore)
{
    private const string WorksheetName = "Complete List";
    private const int MaximumRows = 5000;
    private static readonly string[] Headers =
    [
        "Status",
        "Sales Order#",
        "QA Arrival Date",
        "Part Number",
        "P.O.",
        "Customer",
        "Quantity",
        "Dollar Value",
        "Ship Date",
        "Hold Reason",
        "When Was Source Requested",
        "Action",
        "Date Last Worked On",
        "COMMENTS"
    ];

    public async Task<QualityShippingImportResultDto> ImportAsync(
        Stream stream,
        string fileName,
        QualityAssuranceAccessProfile actor,
        CancellationToken cancellationToken)
    {
        var rows = Parse(stream);
        var groups = await accessStore.GetGroupsWithPermissionAsync(
            QualityAssurancePermissions.ResponsibleGroupEligible,
            cancellationToken);
        var users = await accessStore.GetUsersWithPermissionAsync(
            QualityAssurancePermissions.AssignmentEligible,
            cancellationToken);
        var existing = await db.Shipments
            .Include(shipment => shipment.AuditEntries)
            .ToListAsync(cancellationToken);
        var existingBySignature = existing
            .GroupBy(IdentitySignature)
            .ToDictionary(
                group => group.Key,
                group => new Queue<QualityShipment>(group.OrderBy(shipment => shipment.Id)),
                StringComparer.Ordinal);
        var known = existingBySignature.Keys.ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var created = 0;
        var skipped = 0;
        var reconciled = 0;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var row in rows)
        {
            var signature = IdentitySignature(row);
            if (existingBySignature.TryGetValue(signature, out var matches) && matches.Count > 0)
            {
                var existingShipment = matches.Dequeue();
                var existingOwner = ResolveOwner(row.Action, users, groups);
                if (ReconcileLegacyAssignment(existingShipment, row.Action, existingOwner, actor, now)) reconciled++;
                skipped++;
                continue;
            }
            if (!known.Add(signature))
            {
                skipped++;
                continue;
            }

            var shipment = CreateShipment(row, actor, now);
            var owner = ResolveOwner(row.Action, users, groups);
            ApplyImportedOwner(shipment, row.Action, owner);

            shipment.AuditEntries.Add(new QualityShipmentAuditEntry
            {
                EventType = "Imported",
                NewValue = $"{fileName} / {WorksheetName} row {row.RowNumber}",
                AccountName = actor.AccountName,
                DisplayName = actor.DisplayName,
                OccurredAt = now
            });
            shipment.AuditEntries.Add(new QualityShipmentAuditEntry
            {
                EventType = owner is null ? "AssignmentPending" : "AutoAssigned",
                FieldName = "assignment",
                NewValue = owner is null
                    ? string.IsNullOrWhiteSpace(row.Action)
                        ? "Unassigned: no action owner supplied"
                        : $"Unassigned: '{QualityLegacyAssignmentIdentity.TryNormalizePrefixedTag(row.Action) ?? row.Action}' did not match one eligible user by first name"
                    : $"{owner.Group.Name} / {owner.User.DisplayName}",
                AccountName = actor.AccountName,
                DisplayName = actor.DisplayName,
                OccurredAt = now
            });
            db.Shipments.Add(shipment);
            created++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new QualityShippingImportResultDto(rows.Count, created, skipped, reconciled, WorksheetName);
    }

    private static bool ReconcileLegacyAssignment(
        QualityShipment shipment,
        string? action,
        ResolvedQualityOwner? owner,
        QualityAssuranceAccessProfile actor,
        DateTimeOffset now)
    {
        var isImported = shipment.AuditEntries.Any(entry => entry.EventType == "Imported");
        var hasManualAssignment = shipment.AuditEntries.Any(entry => entry.EventType == "Assigned");
        if (!isImported || hasManualAssignment) return false;

        var oldAssignment = AssignmentLabel(shipment);
        var oldAction = shipment.NextAction;
        ApplyImportedOwner(shipment, action, owner);
        var newAssignment = AssignmentLabel(shipment);
        var assignmentChanged = oldAssignment != newAssignment;
        var actionChanged = oldAction != shipment.NextAction;
        if (!assignmentChanged && !actionChanged) return false;

        shipment.UpdatedAt = now;
        shipment.UpdatedByAccountName = actor.AccountName;
        shipment.UpdatedByDisplayName = actor.DisplayName;
        shipment.Version++;
        if (assignmentChanged) shipment.AuditEntries.Add(new QualityShipmentAuditEntry
        {
            EventType = owner is null ? "AssignmentPending" : "AutoAssigned",
            FieldName = "assignment",
            OldValue = oldAssignment,
            NewValue = owner is null
                ? string.IsNullOrWhiteSpace(action)
                    ? "Unassigned: no action owner supplied"
                    : $"Unassigned: '{QualityLegacyAssignmentIdentity.TryNormalizePrefixedTag(action) ?? action}' did not match one eligible user by first name"
                : $"{owner.Group.Name} / {owner.User.DisplayName}",
            AccountName = actor.AccountName,
            DisplayName = actor.DisplayName,
            OccurredAt = now
        });
        if (actionChanged) shipment.AuditEntries.Add(new QualityShipmentAuditEntry
        {
            EventType = "UpdatedByImport",
            FieldName = "nextAction",
            OldValue = oldAction,
            NewValue = shipment.NextAction,
            AccountName = actor.AccountName,
            DisplayName = actor.DisplayName,
            OccurredAt = now
        });
        return true;
    }

    private static void ApplyImportedOwner(QualityShipment shipment, string? uploadedAction, ResolvedQualityOwner? owner)
    {
        var legacyTag = QualityLegacyAssignmentIdentity.TryNormalizePrefixedTag(uploadedAction);
        shipment.AssignedGroupId = owner?.Group.Id;
        shipment.AssignedGroupName = owner?.Group.Name;
        shipment.AssignedUserId = owner?.User.Id;
        shipment.AssignedAccountName = owner?.User.AccountName;
        shipment.AssignedDisplayName = owner?.User.DisplayName;
        shipment.NextAction = owner?.User.DisplayName ?? legacyTag ?? Clean(uploadedAction);
        shipment.LegacyAssigneeTag = owner is null ? legacyTag : null;
    }

    private static string AssignmentLabel(QualityShipment shipment)
    {
        var label = string.Join(" / ", new[] { shipment.AssignedGroupName, shipment.AssignedDisplayName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(label) ? "Unassigned" : label;
    }

    private static ResolvedQualityOwner? ResolveOwner(
        string? action,
        IReadOnlyList<QualityDirectoryUser> users,
        IReadOnlyList<QualityDirectoryGroup> groups)
    {
        var tag = QualityLegacyAssignmentIdentity.TryNormalizePrefixedTag(action)
            ?? Clean(action);
        return tag is null
            ? null
            : QualityLegacyAssignmentIdentity.ResolveOwnerByFirstName(tag, users, groups);
    }

    private static IReadOnlyList<ImportRow> Parse(Stream stream)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ArgumentException($"The Excel workbook could not be read: {exception.Message}");
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, WorksheetName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"The workbook must contain a '{WorksheetName}' worksheet.");
            var columns = Columns(sheet);
            var missing = Headers.Where(header => !columns.ContainsKey(NormalizeHeader(header))).ToList();
            if (missing.Count > 0)
                throw new ArgumentException($"The '{WorksheetName}' worksheet is missing required columns: {string.Join(", ", missing)}.");

            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            if (lastRow - 1 > MaximumRows)
                throw new ArgumentException($"The '{WorksheetName}' worksheet cannot exceed {MaximumRows:N0} data rows.");

            var rows = new List<ImportRow>();
            var issues = new List<ImportIssue>();
            for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
            {
                if (IsBlank(sheet, rowNumber, columns)) continue;
                var rowIssues = new List<ImportIssue>();
                foreach (var header in Headers)
                {
                    var cell = Cell(sheet, rowNumber, columns, header);
                    if (cell.HasFormula)
                        rowIssues.Add(new ImportIssue(rowNumber, header, "formulas are not accepted in import columns"));
                }

                var status = Text(sheet, rowNumber, columns, "Status", 80, rowIssues) ?? "WIP";
                var salesOrder = RequiredText(sheet, rowNumber, columns, "Sales Order#", 80, rowIssues);
                var qaArrival = Date(sheet, rowNumber, columns, "QA Arrival Date", rowIssues);
                var partNumber = RequiredText(sheet, rowNumber, columns, "Part Number", 160, rowIssues);
                var purchaseOrder = Text(sheet, rowNumber, columns, "P.O.", 160, rowIssues);
                var customer = RequiredText(sheet, rowNumber, columns, "Customer", 240, rowIssues);
                var quantity = Decimal(sheet, rowNumber, columns, "Quantity", rowIssues);
                var dollarValue = Decimal(sheet, rowNumber, columns, "Dollar Value", rowIssues);
                var shipDate = Date(sheet, rowNumber, columns, "Ship Date", rowIssues);
                var holdReason = Text(sheet, rowNumber, columns, "Hold Reason", 4000, rowIssues);
                var sourceRequested = Date(sheet, rowNumber, columns, "When Was Source Requested", rowIssues);
                var action = Text(sheet, rowNumber, columns, "Action", 2000, rowIssues);
                var lastWorked = Date(sheet, rowNumber, columns, "Date Last Worked On", rowIssues);
                var comments = Text(sheet, rowNumber, columns, "COMMENTS", 8000, rowIssues);
                if (quantity < 0) rowIssues.Add(new ImportIssue(rowNumber, "Quantity", "value cannot be negative"));
                if (dollarValue < 0) rowIssues.Add(new ImportIssue(rowNumber, "Dollar Value", "value cannot be negative"));

                rows.Add(new ImportRow(
                    rowNumber,
                    status,
                    salesOrder ?? string.Empty,
                    qaArrival,
                    partNumber ?? string.Empty,
                    purchaseOrder,
                    customer ?? string.Empty,
                    quantity,
                    dollarValue,
                    shipDate,
                    holdReason,
                    sourceRequested,
                    action,
                    lastWorked,
                    comments));
                issues.AddRange(rowIssues);
            }

            if (rows.Count == 0)
                throw new ArgumentException($"The '{WorksheetName}' worksheet does not contain any shipment rows.");
            if (issues.Count > 0)
            {
                var details = string.Join("; ", issues.Take(12).Select(issue =>
                    $"row {issue.RowNumber}, {issue.Column}: {issue.Message}"));
                var remainder = issues.Count > 12 ? $"; plus {issues.Count - 12} more error(s)" : string.Empty;
                throw new ArgumentException($"The workbook was not imported because validation failed: {details}{remainder}.");
            }
            return rows;
        }
    }

    private static QualityShipment CreateShipment(
        ImportRow row,
        QualityAssuranceAccessProfile actor,
        DateTimeOffset now)
    {
        var shipment = new QualityShipment
        {
            Status = row.Status,
            SalesOrderNumber = row.SalesOrderNumber,
            QaArrivalDate = row.QaArrivalDate,
            PartNumber = row.PartNumber,
            PurchaseOrderNumber = row.PurchaseOrderNumber,
            Customer = row.Customer,
            TaskType = "General",
            Quantity = row.Quantity,
            DollarValue = row.DollarValue,
            ShipDate = row.ShipDate,
            HoldReason = row.HoldReason,
            SourceRequestedDate = row.SourceRequestedDate,
            NextAction = row.Action,
            LastWorkedAt = row.LastWorkedDate.HasValue
                ? new DateTimeOffset(row.LastWorkedDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : null,
            Comments = row.Comments,
            CreatedAt = now,
            CreatedByAccountName = actor.AccountName,
            CreatedByDisplayName = actor.DisplayName,
            UpdatedAt = now,
            UpdatedByAccountName = actor.AccountName,
            UpdatedByDisplayName = actor.DisplayName,
            Version = 1
        };
        if (!string.IsNullOrWhiteSpace(row.Comments))
        {
            shipment.CommentThread.Add(new QualityShipmentComment
            {
                Body = row.Comments,
                AuthorUserId = actor.UserId,
                AuthorAccountName = actor.AccountName,
                AuthorDisplayName = actor.DisplayName,
                CreatedAt = now,
                IsLegacyImport = true
            });
        }
        return shipment;
    }

    private static IReadOnlyDictionary<string, int> Columns(IXLWorksheet sheet)
    {
        var lastColumn = sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        return sheet.Row(1)
            .Cells(1, lastColumn)
            .Where(cell => !string.IsNullOrWhiteSpace(cell.GetString()))
            .GroupBy(cell => NormalizeHeader(cell.GetString()))
            .ToDictionary(group => group.Key, group => group.First().Address.ColumnNumber);
    }

    private static bool IsBlank(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns) => Headers.All(header =>
            Cell(sheet, row, columns, header).IsEmpty());

    private static string? RequiredText(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header,
        int maximumLength,
        List<ImportIssue> issues)
    {
        var value = Text(sheet, row, columns, header, maximumLength, issues);
        if (value is null) issues.Add(new ImportIssue(row, header, "value is required"));
        return value;
    }

    private static string? Text(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header,
        int maximumLength,
        List<ImportIssue> issues)
    {
        var value = Clean(Cell(sheet, row, columns, header).GetFormattedString());
        if (value?.Length > maximumLength)
        {
            issues.Add(new ImportIssue(row, header, $"value cannot exceed {maximumLength:N0} characters"));
            return value[..maximumLength];
        }
        return value;
    }

    private static decimal? Decimal(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header,
        List<ImportIssue> issues)
    {
        var cell = Cell(sheet, row, columns, header);
        if (cell.IsEmpty()) return null;
        if (cell.TryGetValue<decimal>(out var number)) return number;
        var value = Clean(cell.GetFormattedString());
        if (decimal.TryParse(value, NumberStyles.Currency, CultureInfo.InvariantCulture, out number)
            || decimal.TryParse(value, NumberStyles.Currency, CultureInfo.CurrentCulture, out number)) return number;
        if (TryParseLegacyDecimal(header, value, out number)) return number;
        issues.Add(new ImportIssue(row, header, $"'{value}' is not a valid number"));
        return null;
    }

    private static bool TryParseLegacyDecimal(string header, string? value, out decimal number)
    {
        number = default;
        if (value is null) return false;

        if (header == "Quantity")
        {
            var quantity = Regex.Match(
                value,
                @"^(?<value>\d+(?:\.\d+)?)\s*(?:KIT|KITS|PC|PCS|PIECE|PIECES)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return quantity.Success
                && decimal.TryParse(quantity.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out number);
        }

        if (header == "Dollar Value")
        {
            var currency = Regex.Match(value, @"^(?<whole>\d{1,12})-(?<cents>\d{2})$", RegexOptions.CultureInvariant);
            return currency.Success
                && decimal.TryParse(
                    $"{currency.Groups["whole"].Value}.{currency.Groups["cents"].Value}",
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out number);
        }

        return false;
    }

    private static DateOnly? Date(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header,
        List<ImportIssue> issues)
    {
        var cell = Cell(sheet, row, columns, header);
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.DateTime) return DateOnly.FromDateTime(cell.GetDateTime());
        if (cell.DataType == XLDataType.Number)
        {
            try { return DateOnly.FromDateTime(DateTime.FromOADate(cell.GetDouble())); }
            catch (ArgumentException) { }
        }
        var value = Clean(cell.GetFormattedString());
        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            || DateOnly.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed)) return parsed;
        issues.Add(new ImportIssue(row, header, $"'{value}' is not a valid date"));
        return null;
    }

    private static IXLCell Cell(
        IXLWorksheet sheet,
        int row,
        IReadOnlyDictionary<string, int> columns,
        string header) => sheet.Cell(row, columns[NormalizeHeader(header)]);

    private static string IdentitySignature(QualityShipment shipment) => IdentitySignature(
        shipment.Status,
        shipment.SalesOrderNumber,
        shipment.QaArrivalDate,
        shipment.PartNumber,
        shipment.PurchaseOrderNumber,
        shipment.Customer,
        shipment.Quantity,
        shipment.DollarValue,
        shipment.ShipDate,
        shipment.HoldReason,
        shipment.SourceRequestedDate,
        shipment.LastWorkedAt.HasValue ? DateOnly.FromDateTime(shipment.LastWorkedAt.Value.UtcDateTime) : null,
        shipment.Comments);

    private static string IdentitySignature(ImportRow row) => IdentitySignature(
        row.Status,
        row.SalesOrderNumber,
        row.QaArrivalDate,
        row.PartNumber,
        row.PurchaseOrderNumber,
        row.Customer,
        row.Quantity,
        row.DollarValue,
        row.ShipDate,
        row.HoldReason,
        row.SourceRequestedDate,
        row.LastWorkedDate,
        row.Comments);

    private static string IdentitySignature(
        string? status,
        string? salesOrder,
        DateOnly? qaArrival,
        string? partNumber,
        string? purchaseOrder,
        string? customer,
        decimal? quantity,
        decimal? dollarValue,
        DateOnly? shipDate,
        string? holdReason,
        DateOnly? sourceRequested,
        DateOnly? lastWorked,
        string? comments) => string.Join('\u001f',
    [
        FingerprintText(status),
        FingerprintText(salesOrder),
        DateValue(qaArrival),
        FingerprintText(partNumber),
        FingerprintText(purchaseOrder),
        FingerprintText(customer),
        NumberValue(quantity),
        NumberValue(dollarValue),
        DateValue(shipDate),
        FingerprintText(holdReason),
        DateValue(sourceRequested),
        DateValue(lastWorked),
        FingerprintText(comments)
    ]);

    private static string FingerprintText(string? value) =>
        (value ?? string.Empty).Trim().Replace("\r\n", "\n").ToUpperInvariant();

    private static string DateValue(DateOnly? value) => value?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string NumberValue(decimal? value) => value?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string NormalizeHeader(string value) => new(
        value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ImportIssue(int RowNumber, string Column, string Message);
    private sealed record ImportRow(
        int RowNumber,
        string Status,
        string SalesOrderNumber,
        DateOnly? QaArrivalDate,
        string PartNumber,
        string? PurchaseOrderNumber,
        string Customer,
        decimal? Quantity,
        decimal? DollarValue,
        DateOnly? ShipDate,
        string? HoldReason,
        DateOnly? SourceRequestedDate,
        string? Action,
        DateOnly? LastWorkedDate,
        string? Comments);
}
