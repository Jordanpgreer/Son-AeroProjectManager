using System.Globalization;
using ClosedXML.Excel;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Dtos;
using EngineeringHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Services;

public sealed class ToolCatalogWorkbookService(ToolCatalogReviewStore reviews)
{
    public const string SheetName = "Tool Catalogue";
    public const string TemplateFileName = "Tool-Catalogue-Audit-Update.xlsx";

    internal static readonly string[] Headers =
    [
        "Record ID",
        "Tool Number (Required)",
        "Tool Name",
        "Tool Type",
        "Owner (Required)",
        "Current Status",
        "Physical Location / Vendor",
        "Default Check-In Location",
        "Current Holder",
        "Last Audit Date (Reference)",
        "New Audit Date",
        "Part Numbers (Required)",
        "Description",
        "Notes",
        "Archived"
    ];

    private const int MaxRows = 5000;
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static string ContentType => XlsxContentType;

    public async Task<byte[]> ExportAsync(EngineeringDbContext db, CancellationToken cancellationToken = default)
    {
        var tools = await db.Tools.AsNoTracking()
            .Include(tool => tool.CurrentLocation)
            .Include(tool => tool.HomeLocationAssignment).ThenInclude(assignment => assignment!.Location)
            .Include(tool => tool.PartNumbers)
            .OrderBy(tool => tool.ToolNumber)
            .ToListAsync(cancellationToken);
        var locations = await db.ToolLocations.AsNoTracking().OrderBy(location => location.Code).ToListAsync(cancellationToken);

        using var workbook = new XLWorkbook();
        BuildInstructions(workbook.AddWorksheet("Instructions"));
        var sheet = workbook.AddWorksheet(SheetName);
        var lists = workbook.AddWorksheet("Lists");
        BuildLists(lists, locations);
        WriteHeaders(sheet);

        var row = 2;
        foreach (var tool in tools)
        {
            sheet.Cell(row, 1).Value = tool.Id;
            sheet.Cell(row, 2).Value = tool.ToolNumber;
            sheet.Cell(row, 3).Value = tool.Name;
            sheet.Cell(row, 4).Value = tool.ToolType;
            sheet.Cell(row, 5).Value = tool.Owner;
            sheet.Cell(row, 6).Value = FriendlyStatus(tool.CustodyStatus);
            sheet.Cell(row, 7).Value = PhysicalAssignment(tool);
            sheet.Cell(row, 8).Value = tool.HomeLocationAssignment?.Location.Code ?? string.Empty;
            sheet.Cell(row, 9).Value = tool.CurrentHolder ?? string.Empty;
            SetDate(sheet.Cell(row, 10), tool.LastAuditDate);
            sheet.Cell(row, 12).Value = JoinParts(tool.PartNumbers.Select(part => part.PartNumber));
            sheet.Cell(row, 13).Value = tool.Description ?? string.Empty;
            sheet.Cell(row, 14).Value = tool.Notes ?? string.Empty;
            sheet.Cell(row, 15).Value = tool.IsArchived ? "Yes" : "No";
            row++;
        }

        FormatCatalogue(sheet, Math.Max(row - 1, 2), locations.Count(location => location.IsActive));
        lists.Visibility = XLWorksheetVisibility.VeryHidden;
        workbook.Worksheet(SheetName).SetTabActive();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<ToolCatalogValidationDto> ValidateAsync(
        EngineeringDbContext db,
        byte[] workbookBytes,
        string fileName,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<ToolCatalogIssueDto>();
        IReadOnlyList<ToolCatalogRow> rows;
        try
        {
            rows = ParseWorkbook(workbookBytes, errors);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ToolCatalogValidationException($"The workbook could not be read. Download a fresh tool catalogue and try again. {exception.Message}");
        }

        var tools = await db.Tools.AsNoTracking()
            .Include(tool => tool.CurrentLocation)
            .Include(tool => tool.HomeLocationAssignment).ThenInclude(assignment => assignment!.Location)
            .Include(tool => tool.PartNumbers)
            .OrderBy(tool => tool.Id)
            .ToListAsync(cancellationToken);
        var locations = await db.ToolLocations.AsNoTracking().ToListAsync(cancellationToken);
        var changes = CompareAndValidate(rows, tools, locations, errors);
        var versions = tools.Where(tool => rows.Any(row => row.ExistingId == tool.Id))
            .ToDictionary(tool => tool.Id, tool => tool.Version);
        var review = ToolCatalogReviewStore.Create(actor, fileName, workbookBytes, rows, errors, changes, versions);
        reviews.Save(review);
        return ToValidationDto(review);
    }

    public byte[] BuildReviewWorkbook(string reviewId, string actor)
    {
        var review = FindReview(reviewId, actor);
        using var input = new MemoryStream(review.OriginalWorkbook);
        using var workbook = new XLWorkbook(input);
        var previousReview = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name == "Import Review");
        previousReview?.Delete();
        var sheet = workbook.Worksheets.FirstOrDefault(candidate => candidate.Name == SheetName)
            ?? workbook.AddWorksheet(SheetName);
        AnnotateCatalogue(sheet, review);
        BuildReviewSummary(workbook.AddWorksheet("Import Review"), review);
        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    public async Task<ToolCatalogApplyResultDto> ApplyAsync(
        EngineeringDbContext db,
        string reviewId,
        string actor,
        bool continueWithErrors,
        CancellationToken cancellationToken = default)
    {
        var review = FindReview(reviewId, actor);
        if (review.Errors.Count > 0 && !continueWithErrors)
            throw new ToolCatalogValidationException("This workbook still has validation errors. Correct and re-upload it, or explicitly confirm that valid rows should be applied while invalid rows are skipped.");

        var invalidRows = review.Errors.Where(error => error.Row > 1).Select(error => error.Row).ToHashSet();
        var changedRows = review.Changes.Select(change => change.Row).ToHashSet();
        var rows = review.Rows.Where(row => !invalidRows.Contains(row.Row) && changedRows.Contains(row.Row)).ToList();
        if (rows.Count == 0)
            throw new ToolCatalogValidationException("The workbook does not contain any valid changes to apply.");

        var existingIds = rows.Where(row => row.ExistingId.HasValue).Select(row => row.ExistingId!.Value).ToList();
        var tools = await db.Tools
            .Where(tool => existingIds.Contains(tool.Id))
            .Include(tool => tool.CurrentLocation)
            .Include(tool => tool.HomeLocationAssignment).ThenInclude(assignment => assignment!.Location)
            .Include(tool => tool.PartNumbers)
            .Include(tool => tool.Movements)
            .Include(tool => tool.AuditEntries)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        foreach (var tool in tools)
        {
            if (!review.Versions.TryGetValue(tool.Id, out var version) || version != tool.Version)
                throw new ToolCatalogConflictException($"Tool {tool.ToolNumber} changed after this workbook was reviewed. Validate the workbook again before applying it.");
        }

        var locations = await db.ToolLocations.ToListAsync(cancellationToken);
        var locationMap = locations.ToDictionary(location => Normalize(location.Code), StringComparer.OrdinalIgnoreCase);
        var toolsById = tools.ToDictionary(tool => tool.Id);
        var now = DateTime.UtcNow;
        var added = 0;
        var updated = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var row in rows.OrderBy(row => row.Row))
        {
            if (row.ExistingId.HasValue)
            {
                var tool = toolsById[row.ExistingId.Value];
                ApplyExisting(tool, row, locationMap, review.Changes.Where(change => change.Row == row.Row).ToList(), actor, now);
                updated++;
            }
            else
            {
                db.Tools.Add(CreateTool(row, locationMap, actor, now));
                added++;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        reviews.Remove(review.Id);

        return new ToolCatalogApplyResultDto(
            added,
            updated,
            invalidRows.Count,
            review.Changes.Count(change => !invalidRows.Contains(change.Row)));
    }

    private ToolCatalogReview FindReview(string reviewId, string actor) =>
        reviews.Find(reviewId, actor)
        ?? throw new ToolCatalogValidationException("The catalogue review expired or is not available for this user. Upload the workbook again.");

    private static IReadOnlyList<ToolCatalogRow> ParseWorkbook(byte[] workbookBytes, List<ToolCatalogIssueDto> errors)
    {
        using var stream = new MemoryStream(workbookBytes);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.FirstOrDefault(candidate => candidate.Name == SheetName);
        if (sheet is null)
        {
            errors.Add(new ToolCatalogIssueDto(1, null, $"The '{SheetName}' worksheet is missing. Download a fresh catalogue template."));
            return [];
        }
        if (!ValidateHeaders(sheet, errors)) return [];

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        if (lastRow - 1 > MaxRows)
            errors.Add(new ToolCatalogIssueDto(1, null, $"The workbook contains more than {MaxRows:N0} tool rows."));
        var rows = new List<ToolCatalogRow>();
        for (var row = 2; row <= Math.Min(lastRow, MaxRows + 1); row++)
        {
            if (IsBlankRow(sheet, row)) continue;
            RejectFormulas(sheet, row, errors);
            var id = ParseId(sheet.Cell(row, 1), row, errors);
            var status = ParseStatus(sheet.Cell(row, 6), row, errors);
            var referenceDate = ParseDate(sheet.Cell(row, 10), row, Headers[9], errors);
            var newDate = ParseDate(sheet.Cell(row, 11), row, Headers[10], errors);
            var archived = ParseBoolean(sheet.Cell(row, 15), row, errors);
            rows.Add(new ToolCatalogRow(
                row,
                id,
                Text(sheet.Cell(row, 2)),
                Text(sheet.Cell(row, 3)),
                Text(sheet.Cell(row, 4)),
                Text(sheet.Cell(row, 5)),
                status,
                Text(sheet.Cell(row, 7)),
                Text(sheet.Cell(row, 8)),
                Text(sheet.Cell(row, 9)),
                referenceDate,
                newDate,
                ParseParts(Text(sheet.Cell(row, 12)), row, errors),
                Text(sheet.Cell(row, 13)),
                Text(sheet.Cell(row, 14)),
                archived));
        }
        if (rows.Count == 0)
            errors.Add(new ToolCatalogIssueDto(2, null, "The workbook does not contain any tool rows."));
        return rows;
    }

    private static IReadOnlyList<ToolCatalogChangeDto> CompareAndValidate(
        IReadOnlyList<ToolCatalogRow> rows,
        IReadOnlyList<ToolRecord> tools,
        IReadOnlyList<ToolLocation> locations,
        List<ToolCatalogIssueDto> errors)
    {
        var changes = new List<ToolCatalogChangeDto>();
        var toolsById = tools.ToDictionary(tool => tool.Id);
        var currentNumbers = tools.ToDictionary(tool => tool.NormalizedToolNumber, StringComparer.OrdinalIgnoreCase);
        var locationMap = locations.ToDictionary(location => Normalize(location.Code), StringComparer.OrdinalIgnoreCase);

        foreach (var group in rows.Where(row => row.ExistingId.HasValue).GroupBy(row => row.ExistingId!.Value).Where(group => group.Count() > 1))
            foreach (var row in group) errors.Add(new ToolCatalogIssueDto(row.Row, Headers[0], $"Record ID {group.Key} appears more than once."));
        foreach (var group in rows.Where(row => !string.IsNullOrWhiteSpace(row.ToolNumber)).GroupBy(row => Normalize(row.ToolNumber!), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            foreach (var row in group) errors.Add(new ToolCatalogIssueDto(row.Row, Headers[1], $"Tool number {row.ToolNumber} appears more than once."));

        foreach (var row in rows)
        {
            ToolRecord? current = null;
            if (row.ExistingId.HasValue && !toolsById.TryGetValue(row.ExistingId.Value, out current))
                errors.Add(new ToolCatalogIssueDto(row.Row, Headers[0], $"Record ID {row.ExistingId} does not exist."));

            var number = Clean(row.ToolNumber);
            var owner = Clean(row.Owner);
            var name = Clean(row.Name) ?? (current is null ? number : null);
            var toolType = Clean(row.ToolType) ?? (current is null ? "General tool" : null);
            var status = row.Status ?? current?.CustodyStatus ?? ToolCustodyStatus.InStorage;
            var archived = row.IsArchived ?? current?.IsArchived ?? false;
            var homeCode = Clean(row.HomeLocation);
            var physical = Clean(row.PhysicalAssignment) ?? (status == ToolCustodyStatus.OutsideProcessing ? null : homeCode);

            if (string.IsNullOrWhiteSpace(number)) errors.Add(new ToolCatalogIssueDto(row.Row, Headers[1], "Tool Number is required."));
            else if (number.Length > 100) errors.Add(new ToolCatalogIssueDto(row.Row, Headers[1], "Tool Number must be 100 characters or fewer."));
            if (current is not null && string.IsNullOrWhiteSpace(name)) errors.Add(new ToolCatalogIssueDto(row.Row, Headers[2], "Tool Name cannot be blank for an existing tool."));
            if (current is not null && string.IsNullOrWhiteSpace(toolType)) errors.Add(new ToolCatalogIssueDto(row.Row, Headers[3], "Tool Type cannot be blank for an existing tool."));
            if (string.IsNullOrWhiteSpace(owner)) errors.Add(new ToolCatalogIssueDto(row.Row, Headers[4], "Owner is required."));
            if (string.IsNullOrWhiteSpace(homeCode))
                errors.Add(new ToolCatalogIssueDto(row.Row, Headers[7], "A Default Check-In Location is required so the tool has a normal return bin."));
            else ValidateLocation(homeCode, current?.HomeLocationAssignment?.LocationId, row.Row, Headers[7], locationMap, errors);
            if (string.IsNullOrWhiteSpace(physical))
                errors.Add(new ToolCatalogIssueDto(row.Row, Headers[6], status == ToolCustodyStatus.OutsideProcessing
                    ? "Enter the outside processing vendor."
                    : "Enter an active physical location."));
            else if (status != ToolCustodyStatus.OutsideProcessing)
                ValidateLocation(physical, current?.CurrentLocationId, row.Row, Headers[6], locationMap, errors);
            if (archived && status != ToolCustodyStatus.InStorage)
                errors.Add(new ToolCatalogIssueDto(row.Row, Headers[14], "A tool must be In Storage before it can be archived."));
            if (row.PartNumbers.Count == 0)
                errors.Add(new ToolCatalogIssueDto(row.Row, Headers[11], "Add at least one associated part number."));
            if (row.ReferenceAuditDate?.Date > DateTime.UtcNow.Date)
                errors.Add(new ToolCatalogIssueDto(row.Row, Headers[9], "Audit dates cannot be in the future."));
            if (row.NewAuditDate?.Date > DateTime.UtcNow.Date)
                errors.Add(new ToolCatalogIssueDto(row.Row, Headers[10], "New Audit Date cannot be in the future."));

            if (!string.IsNullOrWhiteSpace(number) && currentNumbers.TryGetValue(Normalize(number), out var numberOwner) && numberOwner.Id != current?.Id)
                errors.Add(new ToolCatalogIssueDto(row.Row, Headers[1], $"Tool number {number} already belongs to record ID {numberOwner.Id}."));
            if (errors.Any(error => error.Row == row.Row)) continue;

            var key = current?.Id.ToString(CultureInfo.InvariantCulture) ?? $"NEW-{row.Row}";
            if (current is null)
            {
                changes.Add(new ToolCatalogChangeDto(row.Row, key, "Added", Headers[1], null, number));
                continue;
            }

            AddChange(changes, row.Row, key, Headers[1], current.ToolNumber, number);
            AddChange(changes, row.Row, key, Headers[2], current.Name, name);
            AddChange(changes, row.Row, key, Headers[3], current.ToolType, toolType);
            AddChange(changes, row.Row, key, Headers[4], current.Owner, owner);
            AddChange(changes, row.Row, key, Headers[5], FriendlyStatus(current.CustodyStatus), FriendlyStatus(status));
            AddChange(changes, row.Row, key, Headers[6], PhysicalAssignment(current), physical);
            AddChange(changes, row.Row, key, Headers[7], current.HomeLocationAssignment?.Location.Code, homeCode);
            AddChange(changes, row.Row, key, Headers[8], current.CurrentHolder, Clean(row.CurrentHolder));
            AddChange(changes, row.Row, key, Headers[10], DateValue(current.LastAuditDate), DateValue(DesiredAuditDate(row, current)));
            AddChange(changes, row.Row, key, Headers[11], JoinParts(current.PartNumbers.Select(part => part.PartNumber)), JoinParts(row.PartNumbers));
            AddChange(changes, row.Row, key, Headers[12], current.Description, Clean(row.Description));
            AddChange(changes, row.Row, key, Headers[13], current.Notes, Clean(row.Notes));
            AddChange(changes, row.Row, key, Headers[14], current.IsArchived ? "Yes" : "No", archived ? "Yes" : "No");
        }
        return changes;
    }

    private static void ApplyExisting(
        ToolRecord tool,
        ToolCatalogRow row,
        IReadOnlyDictionary<string, ToolLocation> locations,
        IReadOnlyList<ToolCatalogChangeDto> changes,
        string actor,
        DateTime now)
    {
        var status = row.Status ?? tool.CustodyStatus;
        var home = locations[Normalize(row.HomeLocation!)];
        var physical = Clean(row.PhysicalAssignment) ?? (status == ToolCustodyStatus.OutsideProcessing ? null : home.Code);
        var previousStatus = tool.CustodyStatus;
        var previousAssignment = PhysicalAssignment(tool);

        if (tool.HomeLocationAssignment is null)
            tool.HomeLocationAssignment = new ToolHomeLocation { Tool = tool, Location = home };
        else
            tool.HomeLocationAssignment.Location = home;
        tool.ToolNumber = row.ToolNumber!.Trim();
        tool.NormalizedToolNumber = Normalize(tool.ToolNumber);
        tool.Name = row.Name!.Trim();
        tool.ToolType = row.ToolType!.Trim();
        tool.Owner = row.Owner!.Trim();
        tool.Description = Clean(row.Description);
        tool.Notes = Clean(row.Notes);
        tool.IsArchived = row.IsArchived ?? tool.IsArchived;
        tool.CustodyStatus = status;
        tool.CurrentHolder = Clean(row.CurrentHolder);
        if (status == ToolCustodyStatus.OutsideProcessing)
        {
            tool.CurrentLocation = null;
            tool.CurrentVendor = physical;
        }
        else
        {
            tool.CurrentLocation = locations[Normalize(physical!)];
            tool.CurrentVendor = null;
        }
        tool.CheckedOutAt = status == ToolCustodyStatus.InStorage ? null : tool.CheckedOutAt ?? now;
        var desiredAuditDate = DesiredAuditDate(row, tool);
        if (desiredAuditDate?.Date != tool.LastAuditDate?.Date)
        {
            tool.LastAuditDate = desiredAuditDate?.Date;
            tool.LastAuditBy = actor;
        }
        SetParts(tool, row.PartNumbers);
        tool.UpdatedBy = actor;
        tool.UpdatedAt = now;
        tool.Version++;

        var currentAssignment = PhysicalAssignment(tool);
        if (previousStatus != status || !Same(previousAssignment, currentAssignment))
        {
            tool.Movements.Add(new ToolMovement
            {
                Type = MovementFor(previousStatus, status),
                Location = tool.CurrentLocation,
                LocationCode = tool.CurrentLocation?.Code,
                Vendor = tool.CurrentVendor,
                Person = tool.CurrentHolder,
                Purpose = "Controlled tool catalogue import",
                SignedOffBy = actor,
                RecordedAt = now
            });
        }
        tool.AuditEntries.Add(new ToolAuditEntry
        {
            Tool = tool,
            Action = "ToolCatalogImported",
            Details = $"Applied catalogue workbook changes: {string.Join("; ", changes.Select(change => $"{change.Field} from {AuditValue(change.CurrentValue)} to {AuditValue(change.UploadedValue)}"))}.",
            Actor = actor,
            OccurredAt = now
        });
    }

    private static ToolRecord CreateTool(
        ToolCatalogRow row,
        IReadOnlyDictionary<string, ToolLocation> locations,
        string actor,
        DateTime now)
    {
        var number = row.ToolNumber!.Trim();
        var status = row.Status ?? ToolCustodyStatus.InStorage;
        var home = locations[Normalize(row.HomeLocation!)];
        var physical = Clean(row.PhysicalAssignment) ?? (status == ToolCustodyStatus.OutsideProcessing ? null : home.Code);
        var tool = new ToolRecord
        {
            ToolNumber = number,
            NormalizedToolNumber = Normalize(number),
            Name = Clean(row.Name) ?? number,
            ToolType = Clean(row.ToolType) ?? "General tool",
            Owner = row.Owner!.Trim(),
            Description = Clean(row.Description),
            Notes = Clean(row.Notes),
            IsArchived = row.IsArchived ?? false,
            CustodyStatus = status,
            HomeLocationAssignment = new ToolHomeLocation { Location = home },
            CurrentLocation = status == ToolCustodyStatus.OutsideProcessing ? null : locations[Normalize(physical!)],
            CurrentVendor = status == ToolCustodyStatus.OutsideProcessing ? physical : null,
            CurrentHolder = Clean(row.CurrentHolder),
            CheckedOutAt = status == ToolCustodyStatus.InStorage ? null : now,
            LastAuditDate = row.NewAuditDate?.Date ?? row.ReferenceAuditDate?.Date,
            LastAuditBy = row.NewAuditDate.HasValue || row.ReferenceAuditDate.HasValue ? actor : null,
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedBy = actor,
            UpdatedAt = now
        };
        SetParts(tool, row.PartNumbers);
        tool.Movements.Add(new ToolMovement
        {
            Type = ToolMovementType.Registered,
            Location = tool.CurrentLocation ?? home,
            LocationCode = tool.CurrentLocation?.Code ?? home.Code,
            Vendor = tool.CurrentVendor,
            Person = tool.CurrentHolder,
            Purpose = "Created through controlled tool catalogue import",
            SignedOffBy = actor,
            RecordedAt = now
        });
        tool.AuditEntries.Add(new ToolAuditEntry
        {
            Tool = tool,
            Action = "ToolCreatedFromCatalog",
            Details = $"Created tool {number} through the controlled catalogue workbook with default location {home.Code}.",
            Actor = actor,
            OccurredAt = now
        });
        return tool;
    }

    private static void SetParts(ToolRecord tool, IReadOnlyList<string> partNumbers)
    {
        var desired = partNumbers.ToDictionary(Normalize, StringComparer.OrdinalIgnoreCase);
        tool.PartNumbers.RemoveAll(part => !desired.ContainsKey(part.NormalizedPartNumber));
        foreach (var pair in desired.Where(pair => tool.PartNumbers.All(part => part.NormalizedPartNumber != pair.Key)))
            tool.PartNumbers.Add(new ToolPartNumber { Tool = tool, NormalizedPartNumber = pair.Key, PartNumber = pair.Value });
        foreach (var part in tool.PartNumbers)
            if (desired.TryGetValue(part.NormalizedPartNumber, out var display)) part.PartNumber = display;
    }

    private static ToolMovementType MovementFor(ToolCustodyStatus previous, ToolCustodyStatus next) => next switch
    {
        ToolCustodyStatus.OutsideProcessing => ToolMovementType.SentToVendor,
        ToolCustodyStatus.CheckedOut => ToolMovementType.CheckedOut,
        ToolCustodyStatus.InStorage when previous == ToolCustodyStatus.OutsideProcessing => ToolMovementType.ReturnedFromVendor,
        ToolCustodyStatus.InStorage when previous == ToolCustodyStatus.InStorage => ToolMovementType.Relocated,
        _ => ToolMovementType.CheckedIn
    };

    private static ToolCatalogValidationDto ToValidationDto(ToolCatalogReview review)
    {
        var errorRows = review.Errors.Select(error => error.Row).Distinct().Count();
        var newRows = review.Changes.Where(change => change.ChangeType == "Added").Select(change => change.Row).Distinct().Count();
        var updatedRows = review.Changes.Where(change => change.ChangeType == "Modified").Select(change => change.Row).Distinct().Count();
        var validRows = review.Rows.Select(row => row.Row).Except(review.Errors.Where(error => error.Row > 1).Select(error => error.Row)).Count();
        return new ToolCatalogValidationDto(
            review.Id,
            review.ExpiresAt,
            review.FileName,
            review.Rows.Count,
            newRows,
            updatedRows,
            Math.Max(0, validRows - newRows - updatedRows),
            review.Changes.Count,
            errorRows,
            review.Errors.Take(250).ToList(),
            review.Changes.Take(250).ToList(),
            $"/api/tools/catalog-import/{review.Id}/workbook",
            review.Changes.Count > 0);
    }

    private static void BuildInstructions(IXLWorksheet sheet)
    {
        sheet.Cell("A1").Value = "SON-AERO TOOL CATALOGUE UPDATE";
        sheet.Range("A1:F1").Merge();
        sheet.Cell("A1").Style.Font.Bold = true;
        sheet.Cell("A1").Style.Font.FontSize = 18;
        sheet.Cell("A1").Style.Font.FontColor = XLColor.White;
        sheet.Cell("A1").Style.Fill.BackgroundColor = XLColor.FromHtml("#C83F31");
        sheet.Cell("A3").Value = "1. Update the Tool Catalogue sheet";
        sheet.Cell("A4").Value = "Edit existing rows or add rows at the bottom. Record ID links an existing row permanently; leave it blank for a new tool.";
        sheet.Cell("A6").Value = "2. Upload and validate in Engineering";
        sheet.Cell("A7").Value = "The app compares the workbook to the current catalogue before anything is saved. Download the annotated workbook if errors need correction.";
        sheet.Cell("A9").Value = "Important controls";
        sheet.Cell("A10").Value = "Rows omitted from this workbook are never deleted or archived. The import only changes rows included in the file and adds valid new rows.";
        sheet.Cell("A11").Value = "Tool Number, Owner, Part Numbers, and Default Check-In Location are required for new records. Blank Tool Name and Tool Type default to the tool number and General tool.";
        sheet.Cell("A12").Value = "Use semicolons between multiple part numbers. Enter audit dates as real Excel dates. Future audit dates are rejected.";
        sheet.Cell("A13").Value = "In Storage and Checked Out use an active location code in Physical Location / Vendor. Outside Processing uses the vendor name.";
        sheet.Column("A").Width = 115;
        sheet.Range("A3:A13").Style.Alignment.WrapText = true;
        sheet.Rows(3, 13).Height = 28;
        sheet.ShowGridLines = false;
    }

    private static void BuildLists(IXLWorksheet sheet, IReadOnlyList<ToolLocation> locations)
    {
        sheet.Cell("A1").Value = "Active Locations";
        var row = 2;
        foreach (var location in locations.Where(location => location.IsActive)) sheet.Cell(row++, 1).Value = location.Code;
        sheet.Cell("B1").Value = "Statuses";
        sheet.Cell("B2").Value = "In Storage";
        sheet.Cell("B3").Value = "Checked Out";
        sheet.Cell("B4").Value = "Outside Processing";
        sheet.Cell("C1").Value = "Yes / No";
        sheet.Cell("C2").Value = "Yes";
        sheet.Cell("C3").Value = "No";
    }

    private static void WriteHeaders(IXLWorksheet sheet)
    {
        for (var column = 1; column <= Headers.Length; column++)
        {
            var cell = sheet.Cell(1, column);
            cell.Value = Headers[column - 1];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = column is 2 or 5 or 8 or 12
                ? XLColor.FromHtml("#C83F31")
                : XLColor.FromHtml("#17212B");
            cell.Style.Alignment.WrapText = true;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }
        AddComment(sheet.Cell(1, 1), "Permanent database key. Keep existing values unchanged; leave blank when adding a tool.");
        AddComment(sheet.Cell(1, 7), "Use a location code for In Storage or Checked Out. Use a vendor name for Outside Processing.");
        AddComment(sheet.Cell(1, 10), "Current audit date for comparison. If edited, the changed date is accepted when New Audit Date is blank.");
        AddComment(sheet.Cell(1, 11), "Enter the replacement audit date here. Leave blank when the audit date should not change.");
        AddComment(sheet.Cell(1, 12), "Separate multiple part numbers with semicolons.");
    }

    private static void FormatCatalogue(IXLWorksheet sheet, int lastRow, int locationCount)
    {
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, lastRow, Headers.Length).SetAutoFilter();
        sheet.Range(2, 10, MaxRows + 1, 11).Style.NumberFormat.Format = "yyyy-mm-dd";
        sheet.Range(2, 1, MaxRows + 1, Headers.Length).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Range(2, 6, MaxRows + 1, 6).CreateDataValidation().List("'Lists'!$B$2:$B$4");
        sheet.Range(2, 15, MaxRows + 1, 15).CreateDataValidation().List("'Lists'!$C$2:$C$3");
        if (locationCount > 0)
            sheet.Range(2, 8, MaxRows + 1, 8).CreateDataValidation().List($"'Lists'!$A$2:$A${locationCount + 1}");
        sheet.Columns(1, Headers.Length).AdjustToContents();
        foreach (var column in sheet.Columns(1, Headers.Length)) column.Width = Math.Clamp(column.Width, 12, 34);
        sheet.Column(12).Width = 30;
        sheet.Column(13).Width = 34;
        sheet.Column(14).Width = 42;
        sheet.Row(1).Height = 34;
        sheet.Range(2, 1, lastRow, Headers.Length).Style.Border.BottomBorder = XLBorderStyleValues.Hair;
        sheet.Range(2, 1, lastRow, Headers.Length).Style.Border.BottomBorderColor = XLColor.FromHtml("#D8DEE6");
    }

    private static void AnnotateCatalogue(IXLWorksheet sheet, ToolCatalogReview review)
    {
        var statusColumn = Headers.Length + 1;
        var detailColumn = Headers.Length + 2;
        sheet.Cell(1, statusColumn).Value = "Review Status";
        sheet.Cell(1, detailColumn).Value = "Review Details";
        StyleReviewHeader(sheet.Cell(1, statusColumn));
        StyleReviewHeader(sheet.Cell(1, detailColumn));
        foreach (var issue in review.Errors.Where(error => error.Row == 1))
        {
            var column = FindColumn(issue.Column);
            var cell = sheet.Cell(1, column > 0 ? column : 1);
            cell.Style.Fill.BackgroundColor = XLColor.DarkRed;
            AddComment(cell, issue.Message);
        }
        var errorsByRow = review.Errors.Where(error => error.Row > 1).GroupBy(error => error.Row).ToDictionary(group => group.Key, group => group.ToList());
        var changesByRow = review.Changes.GroupBy(change => change.Row).ToDictionary(group => group.Key, group => group.ToList());
        var lastRow = Math.Max(sheet.LastRowUsed()?.RowNumber() ?? 1, 2);
        for (var row = 2; row <= lastRow; row++)
        {
            errorsByRow.TryGetValue(row, out var rowErrors);
            changesByRow.TryGetValue(row, out var rowChanges);
            if (rowErrors is { Count: > 0 })
            {
                sheet.Cell(row, statusColumn).Value = "ERROR";
                sheet.Cell(row, detailColumn).Value = string.Join(" | ", rowErrors.Select(error => error.Message));
                foreach (var issue in rowErrors)
                {
                    var column = FindColumn(issue.Column);
                    if (column == 0)
                    {
                        sheet.Range(row, 1, row, Headers.Length).Style.Fill.BackgroundColor = XLColor.LightSalmon;
                        AddComment(sheet.Cell(row, 1), issue.Message);
                    }
                    else
                    {
                        var cell = sheet.Cell(row, column);
                        cell.Style.Fill.BackgroundColor = XLColor.LightSalmon;
                        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
                        cell.Style.Border.OutsideBorderColor = XLColor.DarkRed;
                        AddComment(cell, issue.Message);
                    }
                }
                continue;
            }
            if (rowChanges is not { Count: > 0 })
            {
                sheet.Cell(row, statusColumn).Value = "UNCHANGED";
                continue;
            }
            var added = rowChanges.Any(change => change.ChangeType == "Added");
            sheet.Cell(row, statusColumn).Value = added ? "NEW" : "CHANGED";
            sheet.Cell(row, detailColumn).Value = added
                ? "New tool record"
                : string.Join(" | ", rowChanges.Select(change => $"{change.Field}: '{change.CurrentValue}' -> '{change.UploadedValue}'"));
            if (added)
                sheet.Range(row, 1, row, Headers.Length).Style.Fill.BackgroundColor = XLColor.LightGreen;
            else
                foreach (var change in rowChanges)
                {
                    var column = FindColumn(change.Field);
                    if (column > 0) sheet.Cell(row, column).Style.Fill.BackgroundColor = XLColor.LightYellow;
                }
        }
        sheet.Column(statusColumn).Width = 16;
        sheet.Column(detailColumn).Width = 70;
        sheet.Column(detailColumn).Style.Alignment.WrapText = true;
    }

    private static void BuildReviewSummary(IXLWorksheet sheet, ToolCatalogReview review)
    {
        var dto = ToValidationDto(review);
        sheet.Cell("A1").Value = "TOOL CATALOGUE IMPORT REVIEW";
        sheet.Range("A1:D1").Merge();
        sheet.Cell("A1").Style.Font.Bold = true;
        sheet.Cell("A1").Style.Font.FontSize = 17;
        sheet.Cell("A1").Style.Font.FontColor = XLColor.White;
        sheet.Cell("A1").Style.Fill.BackgroundColor = XLColor.FromHtml("#C83F31");
        var metrics = new[]
        {
            ("New records", dto.NewRecords), ("Updated records", dto.UpdatedRecords),
            ("Unchanged records", dto.UnchangedRecords), ("Rows with errors", dto.ErrorRows),
            ("Field changes", dto.FieldChanges)
        };
        for (var index = 0; index < metrics.Length; index++)
        {
            sheet.Cell(index + 3, 1).Value = metrics[index].Item1;
            sheet.Cell(index + 3, 2).Value = metrics[index].Item2;
        }
        sheet.Cell("A10").Value = "Errors";
        sheet.Cell("A10").Style.Font.Bold = true;
        sheet.Cell("A11").Value = "Row";
        sheet.Cell("B11").Value = "Column";
        sheet.Cell("C11").Value = "Explanation";
        sheet.Range("A11:C11").Style.Font.Bold = true;
        var row = 12;
        foreach (var issue in review.Errors)
        {
            sheet.Cell(row, 1).Value = issue.Row;
            sheet.Cell(row, 2).Value = issue.Column ?? "Row / workbook";
            sheet.Cell(row, 3).Value = issue.Message;
            row++;
        }
        if (review.Errors.Count == 0) sheet.Cell(row, 1).Value = "No validation errors were found.";
        sheet.Columns("A:C").AdjustToContents();
        sheet.Column("C").Width = Math.Clamp(sheet.Column("C").Width, 30, 90);
        sheet.Column("C").Style.Alignment.WrapText = true;
        sheet.ShowGridLines = false;
    }

    private static bool ValidateHeaders(IXLWorksheet sheet, List<ToolCatalogIssueDto> errors)
    {
        var valid = true;
        for (var column = 1; column <= Headers.Length; column++)
        {
            var actual = sheet.Cell(1, column).GetString().Trim();
            if (actual == Headers[column - 1]) continue;
            errors.Add(new ToolCatalogIssueDto(1, Headers[column - 1], $"Column {ColumnLetter(column)} must be '{Headers[column - 1]}'. Download a fresh catalogue instead of moving or renaming columns."));
            valid = false;
        }
        return valid;
    }

    private static int? ParseId(IXLCell cell, int row, List<ToolCatalogIssueDto> errors)
    {
        var value = Text(cell);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0) return parsed;
        errors.Add(new ToolCatalogIssueDto(row, Headers[0], $"'{value}' is not a valid existing Record ID. Leave it blank for a new tool."));
        return null;
    }

    private static ToolCustodyStatus? ParseStatus(IXLCell cell, int row, List<ToolCatalogIssueDto> errors)
    {
        var value = Text(cell);
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = Normalize(value);
        if (normalized == "INSTORAGE") return ToolCustodyStatus.InStorage;
        if (normalized == "CHECKEDOUT") return ToolCustodyStatus.CheckedOut;
        if (normalized == "OUTSIDEPROCESSING") return ToolCustodyStatus.OutsideProcessing;
        errors.Add(new ToolCatalogIssueDto(row, Headers[5], "Status must be In Storage, Checked Out, or Outside Processing."));
        return null;
    }

    private static bool? ParseBoolean(IXLCell cell, int row, List<ToolCatalogIssueDto> errors)
    {
        var value = Text(cell);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Equals("Yes", StringComparison.OrdinalIgnoreCase) || value.Equals("True", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("No", StringComparison.OrdinalIgnoreCase) || value.Equals("False", StringComparison.OrdinalIgnoreCase)) return false;
        errors.Add(new ToolCatalogIssueDto(row, Headers[14], "Archived must be Yes or No."));
        return null;
    }

    private static DateTime? ParseDate(IXLCell cell, int row, string column, List<ToolCatalogIssueDto> errors)
    {
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.DateTime) return cell.GetDateTime().Date;
        var value = Text(cell);
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            || DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            return parsed.Date;
        errors.Add(new ToolCatalogIssueDto(row, column, $"'{value}' is not a valid date."));
        return null;
    }

    private static IReadOnlyList<string> ParseParts(string? value, int row, List<ToolCatalogIssueDto> errors)
    {
        var parts = (value ?? string.Empty).Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .GroupBy(Normalize, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .OrderBy(part => part, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var part in parts.Where(part => part.Length > 100))
            errors.Add(new ToolCatalogIssueDto(row, Headers[11], $"Part number '{part}' is longer than 100 characters."));
        return parts;
    }

    private static void ValidateLocation(
        string code,
        int? currentLocationId,
        int row,
        string column,
        IReadOnlyDictionary<string, ToolLocation> locations,
        List<ToolCatalogIssueDto> errors)
    {
        if (!locations.TryGetValue(Normalize(code), out var location))
            errors.Add(new ToolCatalogIssueDto(row, column, $"Location '{code}' is not in the location registry."));
        else if (!location.IsActive && location.Id != currentLocationId)
            errors.Add(new ToolCatalogIssueDto(row, column, $"Location '{code}' is inactive and cannot receive a new assignment."));
    }

    private static void RejectFormulas(IXLWorksheet sheet, int row, List<ToolCatalogIssueDto> errors)
    {
        foreach (var cell in sheet.Row(row).Cells(1, Headers.Length).Where(cell => cell.HasFormula))
            errors.Add(new ToolCatalogIssueDto(row, Headers[cell.Address.ColumnNumber - 1], "Formulas are not accepted in import fields. Replace the formula with its final value."));
    }

    private static bool IsBlankRow(IXLWorksheet sheet, int row) =>
        sheet.Row(row).Cells(1, Headers.Length).All(cell => string.IsNullOrWhiteSpace(Text(cell)));

    private static DateTime? DesiredAuditDate(ToolCatalogRow row, ToolRecord current)
    {
        if (row.NewAuditDate.HasValue) return row.NewAuditDate.Value.Date;
        if (row.ReferenceAuditDate?.Date != current.LastAuditDate?.Date) return row.ReferenceAuditDate?.Date;
        return current.LastAuditDate?.Date;
    }

    private static void AddChange(
        ICollection<ToolCatalogChangeDto> changes,
        int row,
        string key,
        string field,
        string? current,
        string? uploaded)
    {
        if (Same(current, uploaded)) return;
        changes.Add(new ToolCatalogChangeDto(row, key, "Modified", field, Clean(current), Clean(uploaded)));
    }

    private static string FriendlyStatus(ToolCustodyStatus status) => status switch
    {
        ToolCustodyStatus.InStorage => "In Storage",
        ToolCustodyStatus.CheckedOut => "Checked Out",
        _ => "Outside Processing"
    };

    private static string PhysicalAssignment(ToolRecord tool) =>
        tool.CustodyStatus == ToolCustodyStatus.OutsideProcessing
            ? tool.CurrentVendor ?? string.Empty
            : tool.CurrentLocation?.Code ?? string.Empty;

    private static string? Text(IXLCell cell) => Clean(cell.GetFormattedString());
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Normalize(string value) => string.Concat(value.ToUpperInvariant().Where(char.IsLetterOrDigit));
    private static string JoinParts(IEnumerable<string> parts) => string.Join("; ", parts.OrderBy(part => part, StringComparer.OrdinalIgnoreCase));
    private static string? DateValue(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static bool Same(string? left, string? right) => string.Equals(Clean(left), Clean(right), StringComparison.OrdinalIgnoreCase);
    private static string AuditValue(string? value) => string.IsNullOrWhiteSpace(value) ? "(blank)" : $"'{value}'";
    private static int FindColumn(string? header) => string.IsNullOrWhiteSpace(header) ? 0 : Array.IndexOf(Headers, header) + 1;
    private static string ColumnLetter(int column) => XLHelper.GetColumnLetterFromNumber(column);

    private static void SetDate(IXLCell cell, DateTime? value)
    {
        if (!value.HasValue) return;
        cell.Value = value.Value.Date;
        cell.Style.NumberFormat.Format = "yyyy-mm-dd";
    }

    private static void AddComment(IXLCell cell, string message)
    {
        var comment = cell.HasComment ? cell.GetComment() : cell.CreateComment();
        if (cell.HasComment && comment.Text.Length > 0) comment.AddText(Environment.NewLine);
        comment.AddText(message);
        comment.Author = "Son-Aero Engineering";
    }

    private static void StyleReviewHeader(IXLCell cell)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontColor = XLColor.White;
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#C83F31");
        cell.Style.Alignment.WrapText = true;
    }
}
