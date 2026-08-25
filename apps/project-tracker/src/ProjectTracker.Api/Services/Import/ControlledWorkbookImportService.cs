using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services.Import;

public sealed class ControlledWorkbookImportService(
    ControlledImportReviewStore reviews,
    ProjectMetricsService metrics,
    ProjectAuditService audit)
{
    public const string ProjectsSheet = "Projects";
    public const string OperationsSheet = "Operations";
    public const string TemplateFileName = "Project-Tracker-Controlled-Import.xlsx";
    public const string PackagedTemplateFileName = "Project-Tracker-Controlled-Import-Template.xlsx";
    public const string PackagedTemplateResourceName =
        "ProjectTracker.Api.Assets.Templates.Project-Tracker-Controlled-Import-Template.xlsx";
    private const int EditableTemplateRowLimit = 10_000;

    internal static readonly string[] ProjectHeaders =
    [
        "Project ID (Required)",
        "Part Number (Required)",
        "Customer (Required)",
        "Contact Lead",
        "Engineer",
        "Sales Order",
        "Job Number",
        "Priority",
        "Completed On",
        "Program Start (Read Only)",
        "Target Delivery (Read Only)",
        "Current Status (Read Only)",
        "Progress (Read Only)",
        "Current Operation (Read Only)"
    ];

    internal static readonly string[] OperationHeaders =
    [
        "Project ID (Required)",
        "Operation ID (System)",
        "Sequence (Required)",
        "Operation Name (Required)",
        "Phase",
        "Work Station",
        "Dependency Operation ID",
        "Start Date Locked",
        "Start Date",
        "Original Start Date",
        "End Date",
        "Original End Date",
        "Estimated Duration",
        "Actual Duration",
        "Completion %",
        "Notes",
        "Current Status (Read Only)",
        "External Operation ID"
    ];

    public async Task<byte[]> ExportTemplateAsync(
        ProjectTrackerDbContext db,
        CancellationToken cancellationToken = default)
    {
        var projects = await db.Projects
            .AsNoTracking()
            .Include(project => project.Tasks)
            .OrderBy(project => project.Id)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        using var workbook = OpenPackagedTemplate();
        var projectSheet = workbook.Worksheet(ProjectsSheet);
        var operationSheet = workbook.Worksheet(OperationsSheet);

        var projectRow = 2;
        foreach (var project in projects)
        {
            projectSheet.Cell(projectRow, 1).Value = project.Id;
            projectSheet.Cell(projectRow, 2).Value = project.ProgramName;
            projectSheet.Cell(projectRow, 3).Value = project.CustomerName ?? string.Empty;
            projectSheet.Cell(projectRow, 4).Value = project.ProgramManager ?? string.Empty;
            projectSheet.Cell(projectRow, 5).Value = project.Engineer ?? string.Empty;
            projectSheet.Cell(projectRow, 6).Value = project.SalesOrderNumber ?? string.Empty;
            projectSheet.Cell(projectRow, 7).Value = project.JobNumber ?? string.Empty;
            projectSheet.Cell(projectRow, 8).Value = project.PriorityRank;
            SetDate(projectSheet.Cell(projectRow, 9), project.CompletedOn);
            SetDate(projectSheet.Cell(projectRow, 10), project.ProgramStart);
            SetDate(projectSheet.Cell(projectRow, 11), project.TargetDelivery);
            projectSheet.Cell(projectRow, 12).Value = Friendly(project.Status);
            projectSheet.Cell(projectRow, 13).Value = project.Progress;
            projectSheet.Cell(projectRow, 13).Style.NumberFormat.Format = "0%";
            projectSheet.Cell(projectRow, 14).Value = project.CurrentTask ?? string.Empty;
            projectRow++;
        }

        var operationRow = 2;
        foreach (var project in projects)
        {
            foreach (var operation in project.Tasks.OrderBy(task => task.Sequence).ThenBy(task => task.Id))
            {
                operationSheet.Cell(operationRow, 1).Value = project.Id;
                operationSheet.Cell(operationRow, 2).Value = operation.Id;
                operationSheet.Cell(operationRow, 3).Value = operation.Sequence;
                operationSheet.Cell(operationRow, 4).Value = operation.Title;
                operationSheet.Cell(operationRow, 5).Value = operation.Phase ?? string.Empty;
                operationSheet.Cell(operationRow, 6).Value = operation.WorkStation ?? string.Empty;
                operationSheet.Cell(operationRow, 7).Value = operation.DependencyTaskId;
                operationSheet.Cell(operationRow, 8).Value = operation.StartDateLocked ? "Yes" : "No";
                SetDate(operationSheet.Cell(operationRow, 9), operation.StartDate);
                SetDate(operationSheet.Cell(operationRow, 10), operation.OriginalStartDate);
                SetDate(operationSheet.Cell(operationRow, 11), operation.EndDate);
                SetDate(operationSheet.Cell(operationRow, 12), operation.OriginalEndDate);
                operationSheet.Cell(operationRow, 13).Value = operation.EstimatedDuration;
                operationSheet.Cell(operationRow, 14).Value = operation.ActualDuration;
                operationSheet.Cell(operationRow, 15).Value = operation.PercentComplete;
                operationSheet.Cell(operationRow, 15).Style.NumberFormat.Format = "0%";
                operationSheet.Cell(operationRow, 16).Value = operation.Notes ?? string.Empty;
                operationSheet.Cell(operationRow, 17).Value = Friendly(operation.Status);
                operationSheet.Cell(operationRow, 18).Value = operation.ExternalTaskId ?? string.Empty;
                operationRow++;
            }
        }

        FinishProjectSheet(projectSheet, projectRow - 1);
        FinishOperationSheet(operationSheet, operationRow - 1);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static XLWorkbook OpenPackagedTemplate()
    {
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Templates",
            PackagedTemplateFileName);
        if (File.Exists(templatePath))
        {
            return new XLWorkbook(templatePath);
        }

        using var templateStream = typeof(ControlledWorkbookImportService).Assembly
            .GetManifestResourceStream(PackagedTemplateResourceName)
            ?? throw new InvalidOperationException(
                $"The packaged import template '{PackagedTemplateFileName}' is missing.");
        return new XLWorkbook(templateStream);
    }

    public async Task<ImportValidationResultDto> ValidateAsync(
        ProjectTrackerDbContext db,
        byte[] workbookBytes,
        string fileName,
        string accountName,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<ImportIssueDto>();
        ControlledImportPayload payload;
        var reviewWorkbookBytes = workbookBytes;
        try
        {
            var parsed = ParseWorkbook(workbookBytes, fileName, errors);
            payload = parsed.Payload;
            reviewWorkbookBytes = parsed.ReviewWorkbook;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ControlledImportValidationException(
                $"The workbook could not be read. Upload the controlled Project Tracker template or one of the supported schedule workbooks. {exception.Message}");
        }

        var currentProjects = await db.Projects
            .AsNoTracking()
            .Include(project => project.Tasks)
            .OrderBy(project => project.Id)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var workCenterNames = await db.WorkCenters
            .AsNoTracking()
            .Select(workCenter => workCenter.Name)
            .ToListAsync(cancellationToken);
        var workCenters = workCenterNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        payload = ResolveImportedIdentifiers(payload, currentProjects);

        var changes = CompareAndValidate(payload, currentProjects, workCenters, errors);
        var projectVersions = currentProjects
            .Where(project => payload.Projects.Any(row => row.ExistingId == project.Id))
            .ToDictionary(project => project.Id, project => project.Version);
        var operationIds = payload.Operations
            .Where(row => row.ExistingId is not null)
            .Select(row => row.ExistingId!.Value)
            .ToHashSet();
        var operationVersions = currentProjects
            .SelectMany(project => project.Tasks)
            .Where(operation => operationIds.Contains(operation.Id))
            .ToDictionary(operation => operation.Id, operation => operation.Version);

        var review = ControlledImportReviewStore.Create(
            accountName,
            Path.GetFileName(fileName),
            reviewWorkbookBytes,
            payload,
            errors,
            changes,
            projectVersions,
            operationVersions);
        reviews.Save(review);
        return ToValidationDto(review);
    }

    public byte[] BuildReviewWorkbook(string reviewId, string accountName)
    {
        var review = reviews.Find(reviewId, accountName)
            ?? throw new ControlledImportValidationException("The import review expired or is not available for this account.");
        using var input = new MemoryStream(review.OriginalWorkbook);
        using var workbook = new XLWorkbook(input);
        AnnotateReviewSheet(workbook.Worksheet(ProjectsSheet), ProjectHeaders, review);
        AnnotateReviewSheet(workbook.Worksheet(OperationsSheet), OperationHeaders, review);
        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    public async Task<ImportApplyResultDto> ApplyAsync(
        ProjectTrackerDbContext db,
        string reviewId,
        string accountName,
        CancellationToken cancellationToken = default)
    {
        var review = reviews.Find(reviewId, accountName)
            ?? throw new ControlledImportValidationException("The import review expired or is not available for this account.");
        if (review.Errors.Count > 0)
            throw new ControlledImportValidationException("This workbook still has validation errors and cannot be confirmed.");
        if (review.Changes.Count == 0)
            throw new ControlledImportValidationException("The workbook does not contain any changes to apply.");

        await VerifyVersionsAsync(db, review, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var existingProjectIds = review.Payload.Projects
            .Where(row => row.ExistingId is not null)
            .Select(row => row.ExistingId!.Value)
            .ToList();
        var existingProjects = await db.Projects
            .Where(project => existingProjectIds.Contains(project.Id))
            .Include(project => project.Tasks)
                .ThenInclude(task => task.OvertimeDays)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var projectsById = existingProjects.ToDictionary(project => project.Id);
        var projectEntities = new Dictionary<string, Project>(StringComparer.Ordinal);
        var projectsNeedingMetrics = new HashSet<Project>();
        var pendingAudits = new List<PendingAudit>();
        var now = DateTimeOffset.UtcNow;

        foreach (var row in review.Payload.Projects)
        {
            if (row.ExistingId is not null)
            {
                var project = projectsById[row.ExistingId.Value];
                var before = ProjectAuditService.CaptureProject(project);
                ApplyProjectRow(project, row);
                var changes = ProjectAuditService.Diff(before, ProjectAuditService.CaptureProject(project));
                if (changes.Count > 0)
                {
                    project.Version++;
                    project.UpdatedAt = now;
                    projectsNeedingMetrics.Add(project);
                    pendingAudits.Add(new PendingAudit(project, null, "Workbook import", "Project fields updated through controlled import.", changes));
                }
                projectEntities[row.Key] = project;
            }
            else
            {
                var project = new Project();
                ApplyProjectRow(project, row);
                db.Projects.Add(project);
                projectEntities[row.Key] = project;
                projectsNeedingMetrics.Add(project);
                pendingAudits.Add(new PendingAudit(
                    project,
                    null,
                    "Workbook import",
                    "Project created through controlled import.",
                    ProjectAuditService.Diff(
                        new Dictionary<string, string?>(),
                        ProjectAuditService.CaptureProject(project))));
            }
        }

        var operationEntities = new Dictionary<string, ProjectTask>(StringComparer.Ordinal);
        foreach (var row in review.Payload.Operations)
        {
            var project = projectEntities[row.ProjectKey];
            ProjectTask operation;
            if (row.ExistingId is not null)
            {
                operation = project.Tasks.Single(task => task.Id == row.ExistingId.Value);
                var before = ProjectAuditService.CaptureTask(operation);
                ApplyOperationRow(operation, row);
                var changes = ProjectAuditService.Diff(before, ProjectAuditService.CaptureTask(operation));
                if (changes.Count > 0)
                {
                    operation.Version++;
                    operation.UpdatedAt = now;
                    projectsNeedingMetrics.Add(project);
                    pendingAudits.Add(new PendingAudit(project, operation, "Workbook import", "Operation updated through controlled import.", changes));
                }
            }
            else
            {
                operation = new ProjectTask();
                ApplyOperationRow(operation, row);
                project.Tasks.Add(operation);
                projectsNeedingMetrics.Add(project);
                pendingAudits.Add(new PendingAudit(
                    project,
                    operation,
                    "Workbook import",
                    "Operation created through controlled import.",
                    ProjectAuditService.Diff(
                        new Dictionary<string, string?>(),
                        ProjectAuditService.CaptureTask(operation))));
            }
            operationEntities[OperationMapKey(row.ProjectKey, row.Key)] = operation;
        }

        foreach (var row in review.Payload.Operations)
        {
            var operation = operationEntities[OperationMapKey(row.ProjectKey, row.Key)];
            if (string.IsNullOrWhiteSpace(row.DependencyKey))
            {
                operation.DependencyTask = null;
                operation.DependencyTaskId = null;
                continue;
            }

            if (!operationEntities.TryGetValue(
                    OperationMapKey(row.ProjectKey, row.DependencyKey),
                    out var dependency))
            {
                var project = projectEntities[row.ProjectKey];
                dependency = int.TryParse(row.DependencyKey, NumberStyles.None, CultureInfo.InvariantCulture, out var dependencyId)
                    ? project.Tasks.SingleOrDefault(task => task.Id == dependencyId)
                    : null;
            }

            operation.DependencyTask = dependency
                ?? throw new ControlledImportValidationException(
                    $"Dependency Operation ID '{row.DependencyKey}' is no longer available for Project ID '{row.ProjectKey}'.");
            operation.DependencyTaskId = dependency.Id > 0 ? dependency.Id : null;
        }

        var phaseNames = review.Payload.Operations
            .Select(row => row.Phase)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existingPhases = await db.Phases
            .Where(phase => phaseNames.Contains(phase.Name))
            .Select(phase => phase.Name)
            .ToListAsync(cancellationToken);
        var nextSortOrder = (await db.Phases.MaxAsync(phase => (int?)phase.SortOrder, cancellationToken) ?? 0) + 10;
        foreach (var phaseName in phaseNames.Where(name => !existingPhases.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            db.Phases.Add(new Phase { Name = phaseName, SortOrder = nextSortOrder });
            nextSortOrder += 10;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var pending in pendingAudits)
        {
            audit.Record(
                db,
                pending.Project,
                pending.Action,
                pending.Summary,
                pending.Changes,
                pending.Operation?.Id);
        }
        foreach (var project in projectsNeedingMetrics)
            await metrics.RefreshProjectAsync(db, project, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        reviews.Remove(review.Id);

        return new ImportApplyResultDto(
            CountRecords(review.Changes, ProjectsSheet, "Added"),
            CountRecords(review.Changes, ProjectsSheet, "Modified"),
            CountRecords(review.Changes, OperationsSheet, "Added"),
            CountRecords(review.Changes, OperationsSheet, "Modified"),
            review.Changes.Count);
    }

    private static ParsedWorkbook ParseWorkbook(
        byte[] workbookBytes,
        string fileName,
        List<ImportIssueDto> errors)
    {
        using var stream = new MemoryStream(workbookBytes);
        using var workbook = new XLWorkbook(stream);
        var hasControlledSheet = workbook.Worksheets.Any(sheet =>
            sheet.Name is ProjectsSheet or OperationsSheet);
        if (!hasControlledSheet
            && LegacyProjectWorkbookParser.TryParse(workbook, fileName, errors, out var legacy))
        {
            return new ParsedWorkbook(legacy.Payload, legacy.NormalizedWorkbook);
        }

        if (!hasControlledSheet)
        {
            errors.Add(new ImportIssueDto(
                "Workbook",
                1,
                null,
                "The workbook format was not recognized. Use the controlled Projects/Operations template, the multi-project tracker workbook, or the single-project Gantt schedule."));
            var empty = new ControlledImportPayload([], [], "Unrecognized workbook");
            return new ParsedWorkbook(empty, LegacyProjectWorkbookParser.BuildNormalizedWorkbook(empty));
        }

        var unexpectedSheets = workbook.Worksheets
            .Select(sheet => sheet.Name)
            .Where(name => name is not ProjectsSheet and not OperationsSheet)
            .ToList();
        foreach (var name in unexpectedSheets)
            errors.Add(new ImportIssueDto(name, 1, null, "Only the Projects and Operations template sheets are allowed."));

        var projectSheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name == ProjectsSheet);
        var operationSheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name == OperationsSheet);
        if (projectSheet is null)
            errors.Add(new ImportIssueDto(ProjectsSheet, 1, null, "The Projects sheet is missing."));
        if (operationSheet is null)
            errors.Add(new ImportIssueDto(OperationsSheet, 1, null, "The Operations sheet is missing."));

        var projects = projectSheet is not null && ValidateHeaders(projectSheet, ProjectHeaders, errors)
            ? ParseProjects(projectSheet, errors)
            : [];
        var operations = operationSheet is not null && ValidateHeaders(operationSheet, OperationHeaders, errors)
            ? ParseOperations(operationSheet, errors)
            : [];
        return new ParsedWorkbook(
            new ControlledImportPayload(projects, operations),
            workbookBytes);
    }

    private static ControlledImportPayload ResolveImportedIdentifiers(
        ControlledImportPayload payload,
        IReadOnlyList<Project> currentProjects)
    {
        var currentProjectsById = currentProjects.ToDictionary(project => project.Id);
        var containsNewNumericProjectIds = payload.Projects.Any(row =>
            row.ExistingId is not null
            && !currentProjectsById.ContainsKey(row.ExistingId.Value));
        var projects = payload.Projects
            .Select(row => row.ExistingId is not null
                && !currentProjectsById.ContainsKey(row.ExistingId.Value)
                    ? row with { ExistingId = null }
                    : row)
            .ToList();
        var projectRowsByKey = projects.ToDictionary(row => row.Key, StringComparer.Ordinal);
        var currentOperationsById = currentProjects
            .SelectMany(project => project.Tasks)
            .ToDictionary(operation => operation.Id);
        var operations = payload.Operations
            .Select(row =>
            {
                if (!projectRowsByKey.TryGetValue(row.ProjectKey, out var projectRow)
                    || projectRow.ExistingId is null
                    || !currentProjectsById.TryGetValue(projectRow.ExistingId.Value, out var project))
                {
                    return row with { ExistingId = null };
                }

                if (row.ExistingId is not null
                    && currentOperationsById.TryGetValue(row.ExistingId.Value, out var operation)
                    && operation.ProjectId == projectRow.ExistingId.Value)
                {
                    return row;
                }

                var matches = project.Tasks
                    .Where(operation => operation.Sequence == row.Sequence
                        && string.Equals(operation.Title, row.Title, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return matches.Count == 1
                    ? row with { ExistingId = matches[0].Id }
                    : row with { ExistingId = null };
            })
            .ToList();

        foreach (var projectRow in projects)
        {
            var uploaded = operations
                .Where(row => string.Equals(row.ProjectKey, projectRow.Key, StringComparison.Ordinal))
                .OrderBy(row => row.Row)
                .ToList();
            if (uploaded.Count == 0) continue;

            var uploadedExistingIds = uploaded
                .Where(row => row.ExistingId is not null)
                .Select(row => row.ExistingId!.Value)
                .ToHashSet();
            var reservedSequences = projectRow.ExistingId is not null
                && currentProjectsById.TryGetValue(projectRow.ExistingId.Value, out var currentProject)
                    ? currentProject.Tasks
                        .Where(operation => !uploadedExistingIds.Contains(operation.Id))
                        .Select(operation => operation.Sequence)
                        .ToHashSet()
                    : [];
            var desiredSequences = reservedSequences
                .Concat(uploaded.Select(row => row.Sequence))
                .ToList();
            if (desiredSequences.Distinct().Count() == desiredSequences.Count) continue;

            var usedSequences = reservedSequences.ToHashSet();
            var nextSequence = 1;
            foreach (var row in uploaded)
            {
                while (usedSequences.Contains(nextSequence)) nextSequence++;
                var index = operations.IndexOf(row);
                operations[index] = row with { Sequence = nextSequence };
                usedSequences.Add(nextSequence);
                nextSequence++;
            }
        }

        return payload with
        {
            Projects = projects,
            Operations = operations,
            SourceFormat = containsNewNumericProjectIds
                ? $"{payload.SourceFormat} (new numeric project IDs)"
                : payload.SourceFormat,
            UsesPortableIdentifiers = containsNewNumericProjectIds
        };
    }

    private static List<ControlledProjectRow> ParseProjects(IXLWorksheet sheet, List<ImportIssueDto> errors)
    {
        var rows = new List<ControlledProjectRow>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            if (IsBlankRow(sheet, row, ProjectHeaders.Length)) continue;
            RejectFormulas(sheet, row, ProjectHeaders.Length, errors);
            var key = RequiredText(sheet, row, 1, ProjectHeaders[0], errors);
            var partNumber = RequiredText(sheet, row, 2, ProjectHeaders[1], errors);
            var customer = RequiredText(sheet, row, 3, ProjectHeaders[2], errors);
            var existingId = ParseRecordKey(key, ProjectsSheet, row, ProjectHeaders[0], errors);
            var priority = ParseOptionalPositiveInteger(sheet.Cell(row, 8), ProjectsSheet, row, ProjectHeaders[7], errors);
            var completedOn = ParseDate(sheet.Cell(row, 9), ProjectsSheet, row, ProjectHeaders[8], errors);
            if (key is null || partNumber is null || customer is null) continue;

            rows.Add(new ControlledProjectRow(
                row,
                key,
                existingId,
                partNumber,
                customer,
                OptionalText(sheet.Cell(row, 4)),
                OptionalText(sheet.Cell(row, 5)),
                OptionalText(sheet.Cell(row, 6)),
                OptionalText(sheet.Cell(row, 7)),
                priority,
                completedOn));
        }
        return rows;
    }

    private static List<ControlledOperationRow> ParseOperations(IXLWorksheet sheet, List<ImportIssueDto> errors)
    {
        var rows = new List<ControlledOperationRow>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            if (IsBlankRow(sheet, row, OperationHeaders.Length)) continue;
            RejectFormulas(sheet, row, OperationHeaders.Length, errors);
            var projectKey = RequiredText(sheet, row, 1, OperationHeaders[0], errors);
            var uploadedKey = OptionalText(sheet.Cell(row, 2));
            var key = string.IsNullOrWhiteSpace(uploadedKey) ? $"AUTO-ROW-{row}" : uploadedKey;
            var sequence = ParseRequiredPositiveInteger(sheet.Cell(row, 3), OperationsSheet, row, OperationHeaders[2], errors);
            var title = RequiredText(sheet, row, 4, OperationHeaders[3], errors);
            var existingId = string.IsNullOrWhiteSpace(uploadedKey)
                ? null
                : ParseRecordKey(uploadedKey, OperationsSheet, row, OperationHeaders[1], errors);
            var locked = ParseBoolean(sheet.Cell(row, 8), OperationsSheet, row, OperationHeaders[7], errors);
            var start = ParseDate(sheet.Cell(row, 9), OperationsSheet, row, OperationHeaders[8], errors);
            var originalStart = ParseDate(sheet.Cell(row, 10), OperationsSheet, row, OperationHeaders[9], errors);
            var end = ParseDate(sheet.Cell(row, 11), OperationsSheet, row, OperationHeaders[10], errors);
            var originalEnd = ParseDate(sheet.Cell(row, 12), OperationsSheet, row, OperationHeaders[11], errors);
            var estimatedDuration = ParseOptionalPositiveInteger(sheet.Cell(row, 13), OperationsSheet, row, OperationHeaders[12], errors);
            var actualDuration = ParseOptionalPositiveInteger(sheet.Cell(row, 14), OperationsSheet, row, OperationHeaders[13], errors);
            var completion = ParsePercent(sheet.Cell(row, 15), OperationsSheet, row, OperationHeaders[14], errors);
            if (projectKey is null || sequence is null || title is null) continue;

            rows.Add(new ControlledOperationRow(
                row,
                projectKey,
                key,
                existingId,
                sequence.Value,
                title,
                OptionalText(sheet.Cell(row, 5)),
                OptionalText(sheet.Cell(row, 6)),
                OptionalText(sheet.Cell(row, 7)),
                locked,
                start,
                originalStart,
                end,
                originalEnd,
                estimatedDuration,
                actualDuration,
                completion,
                OptionalText(sheet.Cell(row, 16)),
                OptionalText(sheet.Cell(row, 18))));
        }
        return rows;
    }

    private static List<ImportChangeDto> CompareAndValidate(
        ControlledImportPayload payload,
        IReadOnlyList<Project> currentProjects,
        IReadOnlySet<string> workCenters,
        List<ImportIssueDto> errors)
    {
        var changes = new List<ImportChangeDto>();
        var projectsById = currentProjects.ToDictionary(project => project.Id);
        var operationsById = currentProjects.SelectMany(project => project.Tasks).ToDictionary(operation => operation.Id);
        var projectRows = new Dictionary<string, ControlledProjectRow>(StringComparer.Ordinal);
        var uploadedPartNumbers = new Dictionary<string, ControlledProjectRow>(StringComparer.OrdinalIgnoreCase);
        var existingPartNumbers = currentProjects.ToDictionary(project => project.ProgramName, project => project.Id, StringComparer.OrdinalIgnoreCase);
        var seenExistingProjectIds = new HashSet<int>();

        foreach (var row in payload.Projects)
        {
            if (!projectRows.TryAdd(row.Key, row))
                errors.Add(new ImportIssueDto(ProjectsSheet, row.Row, ProjectHeaders[0], $"Project ID '{row.Key}' is duplicated."));
            if (!uploadedPartNumbers.TryAdd(row.ProgramName, row))
                errors.Add(new ImportIssueDto(ProjectsSheet, row.Row, ProjectHeaders[1], $"Part Number '{row.ProgramName}' is duplicated in the upload."));
            if (row.ExistingId is not null && !seenExistingProjectIds.Add(row.ExistingId.Value))
                errors.Add(new ImportIssueDto(ProjectsSheet, row.Row, ProjectHeaders[0], $"Project {row.ExistingId} is represented more than once."));
            if (row.ExistingId is not null && !projectsById.ContainsKey(row.ExistingId.Value))
                errors.Add(new ImportIssueDto(ProjectsSheet, row.Row, ProjectHeaders[0], $"Project ID {row.ExistingId} does not exist or is archived."));
            if (existingPartNumbers.TryGetValue(row.ProgramName, out var existingPartId)
                && row.ExistingId != existingPartId)
                errors.Add(new ImportIssueDto(ProjectsSheet, row.Row, ProjectHeaders[1], $"Part Number '{row.ProgramName}' already belongs to Project ID {existingPartId}."));

            if (row.ExistingId is null)
            {
                changes.Add(Added(ProjectsSheet, row.Row, row.Key, "Record", null, row.ProgramName));
                continue;
            }
            if (!projectsById.TryGetValue(row.ExistingId.Value, out var project)) continue;
            Compare(changes, ProjectsSheet, row.Row, row.Key, ProjectHeaders[1], project.ProgramName, row.ProgramName);
            Compare(changes, ProjectsSheet, row.Row, row.Key, ProjectHeaders[2], project.CustomerName, row.CustomerName);
            Compare(changes, ProjectsSheet, row.Row, row.Key, ProjectHeaders[3], project.ProgramManager, row.ProgramManager);
            Compare(changes, ProjectsSheet, row.Row, row.Key, ProjectHeaders[4], project.Engineer, row.Engineer);
            Compare(changes, ProjectsSheet, row.Row, row.Key, ProjectHeaders[5], project.SalesOrderNumber, row.SalesOrderNumber);
            Compare(changes, ProjectsSheet, row.Row, row.Key, ProjectHeaders[6], project.JobNumber, row.JobNumber);
            Compare(changes, ProjectsSheet, row.Row, row.Key, ProjectHeaders[7], Number(project.PriorityRank), Number(row.PriorityRank));
            Compare(changes, ProjectsSheet, row.Row, row.Key, ProjectHeaders[8], Date(project.CompletedOn), Date(row.CompletedOn));
        }

        var operationRows = new Dictionary<string, ControlledOperationRow>(StringComparer.Ordinal);
        var seenExistingOperationIds = new HashSet<int>();
        foreach (var row in payload.Operations)
        {
            if (!projectRows.ContainsKey(row.ProjectKey))
                errors.Add(new ImportIssueDto(OperationsSheet, row.Row, OperationHeaders[0], $"Project ID '{row.ProjectKey}' does not exactly match a row on the Projects sheet."));
            var mapKey = OperationMapKey(row.ProjectKey, row.Key);
            if (!operationRows.TryAdd(mapKey, row))
                errors.Add(new ImportIssueDto(OperationsSheet, row.Row, OperationHeaders[1], $"Operation ID '{row.Key}' is duplicated for Project ID '{row.ProjectKey}'."));
            if (row.ExistingId is not null && !seenExistingOperationIds.Add(row.ExistingId.Value))
                errors.Add(new ImportIssueDto(OperationsSheet, row.Row, OperationHeaders[1], $"Operation {row.ExistingId} is represented more than once."));
            if (!payload.UsesPortableIdentifiers
                && !string.IsNullOrWhiteSpace(row.WorkStation)
                && !workCenters.Contains(row.WorkStation))
                errors.Add(new ImportIssueDto(OperationsSheet, row.Row, OperationHeaders[5], $"Work Station '{row.WorkStation}' is not an approved Project Tracker work center."));

            if (row.ExistingId is null)
            {
                ValidateDateOrder(row, null, errors);
                changes.Add(Added(OperationsSheet, row.Row, mapKey, "Record", null, row.Title));
                continue;
            }
            if (!operationsById.TryGetValue(row.ExistingId.Value, out var operation))
            {
                errors.Add(new ImportIssueDto(OperationsSheet, row.Row, OperationHeaders[1], $"Operation ID {row.ExistingId} does not exist."));
                continue;
            }
            if (projectRows.TryGetValue(row.ProjectKey, out var projectRow)
                && projectRow.ExistingId != operation.ProjectId)
                errors.Add(new ImportIssueDto(OperationsSheet, row.Row, OperationHeaders[1], $"Operation ID {row.ExistingId} does not belong to Project ID '{row.ProjectKey}'."));
            ValidateDateOrder(row, operation, errors);

            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[2], Number(operation.Sequence), Number(row.Sequence));
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[3], operation.Title, row.Title);
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[4], operation.Phase, row.Phase);
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[5], operation.WorkStation, row.WorkStation);
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[6], Number(operation.DependencyTaskId), row.DependencyKey);
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[7], YesNo(operation.StartDateLocked), YesNo(row.StartDateLocked));
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[8], Date(operation.StartDate), Date(row.StartDate));
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[9], Date(operation.OriginalStartDate), Date(row.OriginalStartDate));
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[10], Date(operation.EndDate), Date(row.EndDate));
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[11], Date(operation.OriginalEndDate), Date(row.OriginalEndDate));
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[12], Number(operation.EstimatedDuration), Number(row.EstimatedDuration));
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[13], Number(operation.ActualDuration), Number(row.ActualDuration));
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[14], Percent(operation.PercentComplete), Percent(row.PercentComplete));
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[15], operation.Notes, row.Notes);
            Compare(changes, OperationsSheet, row.Row, mapKey, OperationHeaders[17], operation.ExternalTaskId, row.ExternalTaskId);
        }

        ValidateSequences(payload, currentProjects, errors);
        ValidateDependencies(payload, operationRows, currentProjects, errors);
        return changes;
    }

    private static void ValidateDateOrder(
        ControlledOperationRow row,
        ProjectTask? current,
        List<ImportIssueDto> errors)
    {
        var scheduledDatesChanged = current is null
            || current.StartDate != row.StartDate
            || current.EndDate != row.EndDate;
        if (scheduledDatesChanged
            && row.StartDate is not null
            && row.EndDate is not null
            && row.StartDate > row.EndDate)
        {
            errors.Add(new ImportIssueDto(
                OperationsSheet,
                row.Row,
                OperationHeaders[10],
                "End Date cannot be before Start Date."));
        }

        var originalDatesChanged = current is null
            || current.OriginalStartDate != row.OriginalStartDate
            || current.OriginalEndDate != row.OriginalEndDate;
        if (originalDatesChanged
            && row.OriginalStartDate is not null
            && row.OriginalEndDate is not null
            && row.OriginalStartDate > row.OriginalEndDate)
        {
            errors.Add(new ImportIssueDto(
                OperationsSheet,
                row.Row,
                OperationHeaders[11],
                "Original End Date cannot be before Original Start Date."));
        }
    }

    private static void ValidateSequences(
        ControlledImportPayload payload,
        IReadOnlyList<Project> currentProjects,
        List<ImportIssueDto> errors)
    {
        foreach (var projectRow in payload.Projects)
        {
            var uploaded = payload.Operations.Where(row => row.ProjectKey == projectRow.Key).ToList();
            var desired = new List<(int Sequence, int Row)>();
            if (projectRow.ExistingId is not null)
            {
                var current = currentProjects.FirstOrDefault(project => project.Id == projectRow.ExistingId.Value);
                if (current is not null)
                {
                    var uploadedIds = uploaded.Where(row => row.ExistingId is not null).Select(row => row.ExistingId!.Value).ToHashSet();
                    desired.AddRange(current.Tasks
                        .Where(task => !uploadedIds.Contains(task.Id))
                        .Select(task => (task.Sequence, 0)));
                }
            }
            desired.AddRange(uploaded.Select(row => (row.Sequence, row.Row)));
            foreach (var duplicate in desired.GroupBy(item => item.Sequence).Where(group => group.Count() > 1))
            {
                foreach (var item in duplicate.Where(item => item.Row > 0))
                    errors.Add(new ImportIssueDto(OperationsSheet, item.Row, OperationHeaders[2], $"Sequence {duplicate.Key} is duplicated within Project ID '{projectRow.Key}'."));
            }
        }
    }

    private static void ValidateDependencies(
        ControlledImportPayload payload,
        IReadOnlyDictionary<string, ControlledOperationRow> operationRows,
        IReadOnlyList<Project> currentProjects,
        List<ImportIssueDto> errors)
    {
        var projectsById = currentProjects.ToDictionary(project => project.Id);
        foreach (var projectRow in payload.Projects)
        {
            var dependencyGraph = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (projectRow.ExistingId is not null
                && projectsById.TryGetValue(projectRow.ExistingId.Value, out var currentProject))
            {
                foreach (var operation in currentProject.Tasks)
                {
                    dependencyGraph[Number(operation.Id)!] = Number(operation.DependencyTaskId);
                }
            }

            var uploadedRows = payload.Operations
                .Where(row => string.Equals(row.ProjectKey, projectRow.Key, StringComparison.Ordinal))
                .ToList();
            foreach (var row in uploadedRows)
                dependencyGraph[row.Key] = row.DependencyKey;

            foreach (var row in uploadedRows.Where(row => !string.IsNullOrWhiteSpace(row.DependencyKey)))
            {
                var dependencyMapKey = OperationMapKey(row.ProjectKey, row.DependencyKey!);
                if (!operationRows.ContainsKey(dependencyMapKey)
                    && !dependencyGraph.ContainsKey(row.DependencyKey!))
                {
                    errors.Add(new ImportIssueDto(
                        OperationsSheet,
                        row.Row,
                        OperationHeaders[6],
                        $"Dependency Operation ID '{row.DependencyKey}' does not exist for Project ID '{row.ProjectKey}'."));
                }
                if (string.Equals(row.Key, row.DependencyKey, StringComparison.Ordinal))
                    errors.Add(new ImportIssueDto(OperationsSheet, row.Row, OperationHeaders[6], "An operation cannot depend on itself."));
            }

            foreach (var start in uploadedRows)
            {
                var path = new HashSet<string>(StringComparer.Ordinal);
                var currentKey = start.Key;
                while (dependencyGraph.TryGetValue(currentKey, out var dependencyKey)
                       && !string.IsNullOrWhiteSpace(dependencyKey)
                       && dependencyGraph.ContainsKey(dependencyKey))
                {
                    if (!path.Add(currentKey))
                    {
                        errors.Add(new ImportIssueDto(OperationsSheet, start.Row, OperationHeaders[6], "The dependency chain contains a cycle."));
                        break;
                    }
                    currentKey = dependencyKey;
                }
            }
        }
    }

    private static async Task VerifyVersionsAsync(
        ProjectTrackerDbContext db,
        ControlledImportReview review,
        CancellationToken cancellationToken)
    {
        var projectIds = review.ProjectVersions.Keys.ToList();
        var currentProjectVersions = await db.Projects
            .Where(project => projectIds.Contains(project.Id))
            .ToDictionaryAsync(project => project.Id, project => project.Version, cancellationToken);
        if (review.ProjectVersions.Any(pair => !currentProjectVersions.TryGetValue(pair.Key, out var version) || version != pair.Value))
            throw new ControlledImportConflictException("Project data changed after this workbook was validated. Upload the workbook again to create a fresh comparison.");

        var operationIds = review.OperationVersions.Keys.ToList();
        var currentOperationVersions = await db.Tasks
            .Where(operation => operationIds.Contains(operation.Id))
            .ToDictionaryAsync(operation => operation.Id, operation => operation.Version, cancellationToken);
        if (review.OperationVersions.Any(pair => !currentOperationVersions.TryGetValue(pair.Key, out var version) || version != pair.Value))
            throw new ControlledImportConflictException("Operation data changed after this workbook was validated. Upload the workbook again to create a fresh comparison.");
    }

    private static void ApplyProjectRow(Project project, ControlledProjectRow row)
    {
        project.ProgramName = row.ProgramName;
        project.CustomerName = row.CustomerName;
        project.ProgramManager = row.ProgramManager;
        project.Engineer = row.Engineer;
        project.SalesOrderNumber = row.SalesOrderNumber;
        project.JobNumber = row.JobNumber;
        project.PriorityRank = row.PriorityRank;
        project.CompletedOn = row.CompletedOn;
        if (row.ExistingId is null)
            project.ImportNeedsCompletion = row.RequiresCompletion;
        else if (project.ImportNeedsCompletion)
            ProjectImportCompletion.Refresh(project);
    }

    private static void ApplyOperationRow(ProjectTask operation, ControlledOperationRow row)
    {
        operation.Sequence = row.Sequence;
        operation.Title = row.Title;
        operation.Phase = row.Phase;
        operation.WorkStation = row.WorkStation;
        operation.StartDateLocked = row.StartDateLocked;
        operation.StartDate = row.StartDate;
        operation.OriginalStartDate = row.OriginalStartDate;
        operation.EndDate = row.EndDate;
        operation.OriginalEndDate = row.OriginalEndDate;
        operation.EstimatedDuration = row.EstimatedDuration;
        operation.ActualDuration = row.ActualDuration;
        operation.PercentComplete = row.PercentComplete;
        operation.PercentCompleteManual = true;
        operation.Notes = row.Notes;
        operation.NoteUpdatedAt = string.IsNullOrWhiteSpace(row.Notes) ? null : DateTimeOffset.UtcNow;
        operation.ExternalTaskId = row.ExternalTaskId;
    }

    private static ImportValidationResultDto ToValidationDto(ControlledImportReview review) => new(
        review.Id,
        review.ExpiresAt,
        review.FileName,
        review.Payload.Projects.Count,
        review.Payload.Operations.Count,
        CountRecords(review.Changes, ProjectsSheet, "Added"),
        CountRecords(review.Changes, ProjectsSheet, "Modified"),
        CountRecords(review.Changes, OperationsSheet, "Added"),
        CountRecords(review.Changes, OperationsSheet, "Modified"),
        review.Changes.Count,
        review.Errors,
        review.Changes.Take(250).ToList(),
        $"/api/import/reviews/{review.Id}/workbook",
        review.Errors.Count == 0 && review.Changes.Count > 0,
        review.Payload.SourceFormat,
        review.Payload.Projects.Count(project => project.RequiresCompletion));

    private static int CountRecords(IReadOnlyList<ImportChangeDto> changes, string sheet, string changeType) =>
        changes.Where(change => change.Sheet == sheet && change.ChangeType == changeType)
            .Select(change => change.RecordKey)
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static void AnnotateReviewSheet(
        IXLWorksheet sheet,
        IReadOnlyList<string> headers,
        ControlledImportReview review)
    {
        var statusColumn = headers.Count + 1;
        var detailColumn = headers.Count + 2;
        sheet.Cell(1, statusColumn).Value = "Review Status";
        sheet.Cell(1, detailColumn).Value = "Review Details";
        StyleHeader(sheet.Cell(1, statusColumn), false);
        StyleHeader(sheet.Cell(1, detailColumn), false);
        var lastRow = Math.Max(sheet.LastRowUsed()?.RowNumber() ?? 1, 2);
        var changesByRow = review.Changes
            .Where(change => change.Sheet == sheet.Name)
            .GroupBy(change => change.Row)
            .ToDictionary(group => group.Key, group => group.ToList());
        var errorsByRow = review.Errors
            .Where(error => error.Sheet == sheet.Name)
            .GroupBy(error => error.Row)
            .ToDictionary(group => group.Key, group => group.ToList());

        for (var row = 2; row <= lastRow; row++)
        {
            errorsByRow.TryGetValue(row, out var rowErrors);
            changesByRow.TryGetValue(row, out var rowChanges);
            if (rowErrors is { Count: > 0 })
            {
                sheet.Range(row, 1, row, headers.Count).Style.Fill.BackgroundColor = XLColor.LightSalmon;
                sheet.Cell(row, statusColumn).Value = "ERROR";
                sheet.Cell(row, detailColumn).Value = string.Join(" | ", rowErrors.Select(error => error.Message));
                continue;
            }
            if (rowChanges is not { Count: > 0 })
            {
                sheet.Cell(row, statusColumn).Value = "UNCHANGED";
                continue;
            }

            var added = rowChanges.Any(change => change.ChangeType == "Added");
            sheet.Cell(row, statusColumn).Value = added ? "NEW" : "CHANGED";
            sheet.Cell(row, detailColumn).Value = string.Join(" | ", rowChanges.Select(change =>
                change.ChangeType == "Added"
                    ? $"New record: {change.UploadedValue}"
                    : $"{change.Field}: '{change.CurrentValue}' -> '{change.UploadedValue}'"));
            if (added)
            {
                sheet.Range(row, 1, row, headers.Count).Style.Fill.BackgroundColor = XLColor.LightGreen;
                continue;
            }
            foreach (var change in rowChanges)
            {
                var column = headers.Select((header, index) => (header, index))
                    .FirstOrDefault(pair => pair.header == change.Field).index + 1;
                if (column > 0) sheet.Cell(row, column).Style.Fill.BackgroundColor = XLColor.LightYellow;
            }
        }
        sheet.Column(statusColumn).Width = 16;
        sheet.Column(detailColumn).Width = 80;
        sheet.Column(detailColumn).Style.Alignment.WrapText = true;
        sheet.SheetView.FreezeRows(1);
    }

    private static bool ValidateHeaders(IXLWorksheet sheet, IReadOnlyList<string> headers, List<ImportIssueDto> errors)
    {
        var valid = true;
        for (var column = 1; column <= headers.Count; column++)
        {
            var actual = sheet.Cell(1, column).GetString().Trim();
            if (string.Equals(actual, headers[column - 1], StringComparison.Ordinal)
                || sheet.Name == OperationsSheet
                && column == 2
                && string.Equals(actual, "Operation ID (Required)", StringComparison.Ordinal))
                continue;
            errors.Add(new ImportIssueDto(
                sheet.Name,
                1,
                headers[column - 1],
                $"Column {ColumnLetter(column)} must be '{headers[column - 1]}'. Download a fresh template instead of moving or renaming columns."));
            valid = false;
        }
        return valid;
    }

    private static void StyleHeader(IXLCell cell, bool required)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontColor = XLColor.White;
        cell.Style.Fill.BackgroundColor = required ? XLColor.FromHtml("#B53A2D") : XLColor.FromHtml("#17324D");
        cell.Style.Alignment.WrapText = true;
        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static void FinishProjectSheet(IXLWorksheet sheet, int lastExistingRow)
    {
        FinishSheet(sheet, ProjectHeaders.Length, lastExistingRow);
        sheet.Range(2, 1, EditableTemplateRowLimit, ProjectHeaders.Length).Style.Protection.Locked = true;
        if (lastExistingRow >= 2)
            sheet.Range(2, 2, lastExistingRow, 9).Style.Protection.Locked = false;
        var firstNewRow = Math.Max(2, lastExistingRow + 1);
        sheet.Range(firstNewRow, 1, EditableTemplateRowLimit, 9).Style.Protection.Locked = false;
        ProtectForDataEntry(sheet);
    }

    private static void FinishOperationSheet(IXLWorksheet sheet, int lastExistingRow)
    {
        FinishSheet(sheet, OperationHeaders.Length, lastExistingRow);
        sheet.Range(2, 1, EditableTemplateRowLimit, OperationHeaders.Length).Style.Protection.Locked = true;
        if (lastExistingRow >= 2)
        {
            sheet.Range(2, 3, lastExistingRow, 16).Style.Protection.Locked = false;
            sheet.Range(2, 18, lastExistingRow, 18).Style.Protection.Locked = false;
        }
        var firstNewRow = Math.Max(2, lastExistingRow + 1);
        sheet.Range(firstNewRow, 1, EditableTemplateRowLimit, 16).Style.Protection.Locked = false;
        sheet.Range(firstNewRow, 18, EditableTemplateRowLimit, 18).Style.Protection.Locked = false;
        sheet.Range(firstNewRow, 2, EditableTemplateRowLimit, 2).Style.Protection.Locked = true;
        sheet.Range(firstNewRow, 17, EditableTemplateRowLimit, 17).Style.Protection.Locked = true;
        sheet.Column(2).Hide();
        ProtectForDataEntry(sheet);
    }

    private static void FinishSheet(IXLWorksheet sheet, int columnCount, int lastExistingRow)
    {
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(lastExistingRow, 2), columnCount).SetAutoFilter();
        sheet.Columns(1, columnCount).AdjustToContents();
        foreach (var column in sheet.Columns(1, columnCount))
            column.Width = Math.Clamp(column.Width, 11, 36);
        sheet.Column(columnCount).Width = Math.Min(60, Math.Max(sheet.Column(columnCount).Width, 24));
    }

    private static void ProtectForDataEntry(IXLWorksheet sheet)
    {
        sheet.Protect()
            .AllowElement(XLSheetProtectionElements.AutoFilter)
            .AllowElement(XLSheetProtectionElements.Sort)
            .AllowElement(XLSheetProtectionElements.InsertRows)
            .AllowElement(XLSheetProtectionElements.SelectLockedCells)
            .AllowElement(XLSheetProtectionElements.SelectUnlockedCells);
    }

    private static string? RequiredText(
        IXLWorksheet sheet,
        int row,
        int column,
        string name,
        List<ImportIssueDto> errors)
    {
        var value = OptionalText(sheet.Cell(row, column));
        if (!string.IsNullOrWhiteSpace(value)) return value;
        errors.Add(new ImportIssueDto(sheet.Name, row, name, $"{name} is required."));
        return null;
    }

    private static int? ParseRecordKey(
        string? key,
        string sheet,
        int row,
        string column,
        List<ImportIssueDto> errors)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) return id;
        if (key.StartsWith("NEW-", StringComparison.OrdinalIgnoreCase)
            && key.Length is >= 5 and <= 80
            && key.Skip(4).All(character => char.IsLetterOrDigit(character) || character is '-' or '_'))
            return null;
        errors.Add(new ImportIssueDto(sheet, row, column, $"'{key}' is not a valid ID. Use an existing numeric ID or a unique NEW- identifier."));
        return null;
    }

    private static int? ParseRequiredPositiveInteger(
        IXLCell cell,
        string sheet,
        int row,
        string column,
        List<ImportIssueDto> errors)
    {
        var value = OptionalText(cell);
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new ImportIssueDto(sheet, row, column, $"{column} is required."));
            return null;
        }
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            return parsed;
        errors.Add(new ImportIssueDto(sheet, row, column, $"{column} must be a positive whole number."));
        return null;
    }

    private static int? ParseOptionalPositiveInteger(
        IXLCell cell,
        string sheet,
        int row,
        string column,
        List<ImportIssueDto> errors)
    {
        var value = OptionalText(cell);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            return parsed;
        errors.Add(new ImportIssueDto(sheet, row, column, $"{column} must be a positive whole number or blank."));
        return null;
    }

    private static decimal ParsePercent(
        IXLCell cell,
        string sheet,
        int row,
        string column,
        List<ImportIssueDto> errors)
    {
        var value = OptionalText(cell);
        if (string.IsNullOrWhiteSpace(value)) return 0m;
        var percentNotation = value.EndsWith('%');
        var numeric = percentNotation ? value[..^1].Trim() : value;
        if (!decimal.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            errors.Add(new ImportIssueDto(sheet, row, column, "Completion % must be between 0% and 100%."));
            return 0m;
        }
        if (percentNotation || parsed > 1m) parsed /= 100m;
        if (parsed is >= 0m and <= 1m) return parsed;
        errors.Add(new ImportIssueDto(sheet, row, column, "Completion % must be between 0% and 100%."));
        return 0m;
    }

    private static bool ParseBoolean(
        IXLCell cell,
        string sheet,
        int row,
        string column,
        List<ImportIssueDto> errors)
    {
        var value = OptionalText(cell);
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Equals("Yes", StringComparison.OrdinalIgnoreCase) || value.Equals("True", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("No", StringComparison.OrdinalIgnoreCase) || value.Equals("False", StringComparison.OrdinalIgnoreCase)) return false;
        errors.Add(new ImportIssueDto(sheet, row, column, "Start Date Locked must be Yes or No."));
        return false;
    }

    private static DateOnly? ParseDate(
        IXLCell cell,
        string sheet,
        int row,
        string column,
        List<ImportIssueDto> errors)
    {
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.DateTime) return DateOnly.FromDateTime(cell.GetDateTime());
        var value = OptionalText(cell);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) && serial > 0)
        {
            try { return DateOnly.FromDateTime(DateTime.FromOADate(serial)); }
            catch (ArgumentException) { }
        }
        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            || DateOnly.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            return parsed;
        errors.Add(new ImportIssueDto(sheet, row, column, $"'{value}' is not a valid date."));
        return null;
    }

    private static void RejectFormulas(IXLWorksheet sheet, int row, int columnCount, List<ImportIssueDto> errors)
    {
        foreach (var cell in sheet.Row(row).Cells(1, columnCount).Where(cell => cell.HasFormula))
            errors.Add(new ImportIssueDto(sheet.Name, row, sheet.Cell(1, cell.Address.ColumnNumber).GetString(), "Formulas are not accepted in import fields. Replace the formula with its final value."));
    }

    private static bool IsBlankRow(IXLWorksheet sheet, int row, int columnCount) =>
        sheet.Row(row).Cells(1, columnCount).All(cell => string.IsNullOrWhiteSpace(OptionalText(cell)));

    private static string? OptionalText(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        var value = cell.GetString().Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void SetDate(IXLCell cell, DateOnly? value)
    {
        if (value is null) return;
        cell.Value = value.Value.ToDateTime(TimeOnly.MinValue);
        cell.Style.NumberFormat.Format = "yyyy-mm-dd";
    }

    private static void Compare(
        List<ImportChangeDto> changes,
        string sheet,
        int row,
        string key,
        string field,
        string? current,
        string? uploaded)
    {
        current = Clean(current);
        uploaded = Clean(uploaded);
        if (!string.Equals(current, uploaded, StringComparison.Ordinal))
            changes.Add(new ImportChangeDto(sheet, row, key, "Modified", field, current, uploaded));
    }

    private static ImportChangeDto Added(
        string sheet,
        int row,
        string key,
        string field,
        string? current,
        string? uploaded) =>
        new(sheet, row, key, "Added", field, current, uploaded);

    private static string OperationMapKey(string projectKey, string operationKey) => $"{projectKey}\u001f{operationKey}";
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Percent(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string YesNo(bool value) => value ? "Yes" : "No";
    private static string Friendly(ProjectStatus status) => status switch
    {
        ProjectStatus.NotStarted => "Not Started",
        ProjectStatus.OnTrack => "On Track",
        _ => status.ToString()
    };
    private static string Friendly(TaskScheduleStatus status) => status switch
    {
        TaskScheduleStatus.NotStarted => "Not Started",
        TaskScheduleStatus.OnTrack => "On Track",
        TaskScheduleStatus.CompletedLate => "Completed Late",
        _ => status.ToString()
    };
    private static string ColumnLetter(int number)
    {
        var result = string.Empty;
        while (number > 0)
        {
            number--;
            result = (char)('A' + number % 26) + result;
            number /= 26;
        }
        return result;
    }

    private sealed record PendingAudit(
        Project Project,
        ProjectTask? Operation,
        string Action,
        string Summary,
        IReadOnlyCollection<ProjectAuditChange> Changes);

    private sealed record ParsedWorkbook(
        ControlledImportPayload Payload,
        byte[] ReviewWorkbook);
}
