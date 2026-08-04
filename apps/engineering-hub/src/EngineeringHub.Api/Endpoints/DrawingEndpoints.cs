using System.Text.Json;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Dtos;
using EngineeringHub.Api.Models;
using EngineeringHub.Api.Services;
using EngineeringHub.Api.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Endpoints;

public static class DrawingEndpoints
{
    private const long MaxFileBytes = 100 * 1024 * 1024;

    public static void MapDrawingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/drawings", ListAsync);
        api.MapGet("/drawings/{id:int}", GetAsync);
        api.MapPost("/drawings", CreateAsync).RequireAuthorization(EngineeringAuthorization.WritePolicy);
        api.MapDelete("/drawings/{id:int}", DeleteDrawingAsync).RequireAuthorization(EngineeringAuthorization.AdminPolicy);
        api.MapGet("/drawing-storage/status", (IDrawingFileStore files) => Results.Ok(files.GetStatus()));
        api.MapPost("/drawings/{id:int}/revisions", UploadRevisionAsync)
            .DisableAntiforgery()
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
        api.MapPost("/drawing-revisions/{id:int}/editable-draft", SaveEditableDraftAsync)
            .DisableAntiforgery()
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
        api.MapPut("/drawing-revisions/{id:int}/status", UpdateRevisionStatusAsync)
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
        api.MapPost("/drawing-revisions/{id:int}/approve", ApproveRevisionAsync)
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
        api.MapPost("/drawing-revisions/{id:int}/make-current", MakeRevisionCurrentAsync)
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
        api.MapDelete("/drawing-revisions/{id:int}", DeleteRevisionAsync)
            .RequireAuthorization(EngineeringAuthorization.AdminPolicy);
        api.MapGet("/drawing-revisions/{id:int}/file", (int id, EngineeringDbContext db, IDrawingFileStore files, CancellationToken ct) => DownloadAsync(id, false, db, files, ct));
        api.MapGet("/drawing-revisions/{id:int}/source", (int id, EngineeringDbContext db, IDrawingFileStore files, CancellationToken ct) => DownloadAsync(id, true, db, files, ct));
        api.MapPost("/drawings/{id:int}/mylars", RegisterMylarAsync)
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
        api.MapPost("/drawings/{id:int}/mylars/{mylarId:int}/checkout", (int id, int mylarId, MylarActionDto dto, MylarCustodyService custody, HttpContext http, CancellationToken ct) =>
            RecordMylarMovementAsync(id, mylarId, dto, true, custody, http, ct))
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
        api.MapPost("/drawings/{id:int}/mylars/{mylarId:int}/checkin", (int id, int mylarId, MylarActionDto dto, MylarCustodyService custody, HttpContext http, CancellationToken ct) =>
            RecordMylarMovementAsync(id, mylarId, dto, false, custody, http, ct))
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
        api.MapPost("/drawings/{id:int}/validations", AddValidationAsync)
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
    }

    private static async Task<IResult> ListAsync(string? query, EngineeringDbContext db, CancellationToken ct)
    {
        var drawings = db.Drawings.AsNoTracking()
            .Include(x => x.Parts)
            .Include(x => x.DocumentLinks)
            .Include(x => x.CurrentApprovedRevision)
            .Include(x => x.Mylars)
            .AsSplitQuery()
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var value = query.Trim();
            var pattern = $"%{EscapeLikePattern(value)}%";
            drawings = drawings.Where(x => EF.Functions.Like(x.DrawingNumber, pattern, "\\") ||
                EF.Functions.Like(x.Title, pattern, "\\") ||
                EF.Functions.Like(x.Customer, pattern, "\\") ||
                x.Parts.Any(p => EF.Functions.Like(p.PartNumber, pattern, "\\")) ||
                x.DocumentLinks.Any(link => EF.Functions.Like(link.ReferenceNumber, pattern, "\\") ||
                    (link.Title != null && EF.Functions.Like(link.Title, pattern, "\\"))) ||
                (x.Notes != null && EF.Functions.Like(x.Notes, pattern, "\\")));
        }
        var records = await drawings.OrderBy(x => x.DrawingNumber).Select(x => new DrawingListDto(
            x.Id, x.DrawingNumber, x.Title, x.Customer, x.Parts.OrderBy(p => p.PartNumber).Select(p => p.PartNumber).ToList(),
            x.DocumentLinks.Where(link => link.Kind == DrawingDocumentKind.Specification)
                .OrderBy(link => link.ReferenceNumber).Select(link => link.ReferenceNumber).ToList(),
            x.ApprovalStatus.ToString(),
            x.CurrentApprovedRevision != null
                ? x.CurrentApprovedRevision.RevisionNumber
                : x.Revisions.OrderByDescending(r => r.UploadedAt).Select(r => r.RevisionNumber).FirstOrDefault(),
            x.CurrentApprovedRevision != null
                ? x.CurrentApprovedRevision.RevisionDate
                : x.Revisions.OrderByDescending(r => r.UploadedAt).Select(r => (DateTime?)r.RevisionDate).FirstOrDefault(),
            x.EffectiveDate, x.IsObsolete,
            x.Mylars.Count == 1 ? x.Mylars.Select(m => m.CurrentLocation).FirstOrDefault() : null,
            x.Mylars.Any(m => m.IsCheckedOut),
            x.Mylars.Count,
            x.Mylars.Count(m => m.IsCheckedOut),
            x.CreatedAt,
            x.Revisions.Count,
            x.CurrentApprovedRevision != null && x.CurrentApprovedRevision.FileSize > 0 && x.CurrentApprovedRevision.StoredFilePath != string.Empty
                ? x.CurrentApprovedRevision.Id
                : x.Revisions.Where(r => r.FileSize > 0 && r.StoredFilePath != string.Empty)
                    .OrderByDescending(r => r.UploadedAt).Select(r => (int?)r.Id).FirstOrDefault(),
            x.CurrentApprovedRevision != null && x.CurrentApprovedRevision.FileSize > 0 && x.CurrentApprovedRevision.StoredFilePath != string.Empty
                ? x.CurrentApprovedRevision.OriginalFileName
                : x.Revisions.Where(r => r.FileSize > 0 && r.StoredFilePath != string.Empty)
                    .OrderByDescending(r => r.UploadedAt).Select(r => r.OriginalFileName).FirstOrDefault(),
            x.CurrentApprovedRevision != null && x.CurrentApprovedRevision.FileSize > 0 && x.CurrentApprovedRevision.StoredFilePath != string.Empty
                ? x.CurrentApprovedRevision.Status.ToString()
                : x.Revisions.Where(r => r.FileSize > 0 && r.StoredFilePath != string.Empty)
                    .OrderByDescending(r => r.UploadedAt).Select(r => r.Status.ToString()).FirstOrDefault()))
            .ToListAsync(ct);
        return Results.Ok(records);
    }

    private static async Task<IResult> GetAsync(int id, EngineeringDbContext db, IDrawingFileStore files, CancellationToken ct)
    {
        var drawing = await db.Drawings.AsNoTracking()
            .Include(x => x.Parts).Include(x => x.Revisions).Include(x => x.DocumentLinks)
            .Include(x => x.Validations).Include(x => x.Mylars).ThenInclude(x => x.Transactions)
            .Include(x => x.MylarTransactions).Include(x => x.AuditEntries)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        return drawing is null ? Results.NotFound() : Results.Ok(ToDetail(drawing, files));
    }

    private static async Task<IResult> CreateAsync(DrawingCreateDto dto, EngineeringDbContext db, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.DrawingNumber) || string.IsNullOrWhiteSpace(dto.Title) || string.IsNullOrWhiteSpace(dto.Customer))
            return Results.BadRequest(new ErrorDto("RequiredFields", "Drawing number, title, and customer are required."));

        var normalizedNumber = Normalize(dto.DrawingNumber);
        var normalizedCustomer = Normalize(dto.Customer);
        if (await db.Drawings.AnyAsync(x => x.NormalizedDrawingNumber == normalizedNumber && x.NormalizedCustomer == normalizedCustomer, ct))
            return Results.Conflict(new ErrorDto("DuplicateDrawing", "That drawing number already exists for this customer."));

        var actor = Actor(http);
        var drawing = new Drawing
        {
            DrawingNumber = dto.DrawingNumber.Trim(), NormalizedDrawingNumber = normalizedNumber,
            Title = dto.Title.Trim(), Customer = dto.Customer.Trim(), NormalizedCustomer = normalizedCustomer,
            Notes = Clean(dto.Notes), PhysicalMylarLocation = Clean(dto.PhysicalMylarLocation),
            CreatedBy = actor, CreatedAt = DateTime.UtcNow
        };
        foreach (var part in dto.PartNumbers?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase) ?? [])
            drawing.Parts.Add(new DrawingPart { PartNumber = part });
        foreach (var link in dto.RelatedDocuments ?? [])
        {
            if (!Enum.TryParse<DrawingDocumentKind>(link.Kind, true, out var kind) || string.IsNullOrWhiteSpace(link.ReferenceNumber)) continue;
            drawing.DocumentLinks.Add(new DrawingDocumentLink { Kind = kind, ReferenceNumber = link.ReferenceNumber.Trim(), Title = Clean(link.Title), Location = Clean(link.Location) });
        }
        if (!string.IsNullOrWhiteSpace(dto.PhysicalMylarLocation))
        {
            var mylar = new DrawingMylar
            {
                MylarNumber = "MYLAR-1",
                NormalizedMylarNumber = "MYLAR1",
                CurrentLocation = dto.PhysicalMylarLocation.Trim(),
                CreatedBy = actor,
                CreatedAt = drawing.CreatedAt
            };
            drawing.Mylars.Add(mylar);
            drawing.MylarTransactions.Add(new MylarTransaction
            {
                Mylar = mylar,
                Type = MylarTransactionType.Registered,
                Person = actor,
                Purpose = "Registered during drawing creation.",
                Location = mylar.CurrentLocation,
                RecordedBy = actor,
                RecordedAt = drawing.CreatedAt
            });
        }
        drawing.AuditEntries.Add(Audit(drawing, null, "DrawingCreated", $"Created drawing {drawing.DrawingNumber} for {drawing.Customer}.", actor));
        db.Drawings.Add(drawing);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/drawings/{drawing.Id}", ToDetail(drawing));
    }

    private static async Task<IResult> DeleteDrawingAsync(int id, [FromBody] DrawingDeleteDto dto, EngineeringDbContext db, CancellationToken ct)
    {
        if (!dto.Confirmed)
            return Results.BadRequest(new ErrorDto("ConfirmationRequired", "Permanent deletion must be explicitly confirmed."));

        var drawing = await db.Drawings
            .Include(x => x.Revisions)
            .Include(x => x.DocumentLinks)
            .Include(x => x.Validations)
            .Include(x => x.Mylars)
            .Include(x => x.MylarTransactions)
            .Include(x => x.AuditEntries)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (drawing is null) return Results.NotFound();
        if (drawing.ApprovalStatus != DrawingApprovalStatus.Draft || drawing.Revisions.Count != 0 ||
            drawing.Validations.Count != 0 || drawing.Mylars.Count != 0 || drawing.MylarTransactions.Count != 0 ||
            drawing.DocumentLinks.Any(link => link.Kind == DrawingDocumentKind.SupplementalDocument && link.Location != null))
            return Results.Conflict(new ErrorDto("ProtectedDrawing", "Only an empty draft drawing can be deleted. Delete eligible draft files first; approved and historical records remain permanent."));
        if (!string.Equals(dto.DrawingNumber, drawing.DrawingNumber, StringComparison.Ordinal))
            return Results.BadRequest(new ErrorDto("DrawingNumberMismatch", "The entered drawing number does not exactly match this draft."));

        db.DrawingAuditEntries.RemoveRange(drawing.AuditEntries);
        db.Drawings.Remove(drawing);
        db.AllowControlledEmptyDraftDrawingDeletion = true;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            db.AllowControlledEmptyDraftDrawingDeletion = false;
        }
        return Results.NoContent();
    }

    private static async Task<IResult> UploadRevisionAsync(int id, HttpRequest request, EngineeringDbContext db, IDrawingFileStore files, HttpContext http, CancellationToken ct)
    {
        if (!request.HasFormContentType) return Results.BadRequest(new ErrorDto("FormRequired", "Use multipart form data."));
        var drawing = await db.Drawings.Include(x => x.Revisions).Include(x => x.AuditEntries).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (drawing is null) return Results.NotFound();
        var form = await request.ReadFormAsync(ct);
        var pdf = form.Files.GetFile("pdf");
        var source = form.Files.GetFile("source");
        var revisionNumber = form["revisionNumber"].ToString().Trim();
        var changeDescription = form["changeDescription"].ToString().Trim();
        var drawingFile = pdf is { Length: > 0 }
            ? await DrawingFileValidation.InspectAsync(pdf, ct)
            : null;
        if (drawingFile is null)
            return Results.BadRequest(new ErrorDto("DrawingFileRequired", "A valid PDF or supported image file is required."));
        if (pdf!.Length > MaxFileBytes || source?.Length > MaxFileBytes)
            return Results.BadRequest(new ErrorDto("FileTooLarge", "Each file must be 100 MB or smaller."));
        if (string.IsNullOrWhiteSpace(revisionNumber) || string.IsNullOrWhiteSpace(changeDescription))
            return Results.BadRequest(new ErrorDto("RequiredFields", "Revision number and change description are required."));
        if (drawing.Revisions.Any(x => string.Equals(x.RevisionNumber, revisionNumber, StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new ErrorDto("DuplicateRevision", "That revision already exists for this drawing."));

        var incomingHash = await CalculateHashAsync(pdf, ct);
        if (drawing.Revisions.Any(x => string.Equals(x.FileHash, incomingHash, StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new ErrorDto("DuplicateFile", "This exact file content is already stored on another revision for this drawing."));

        StoredRevisionFiles stored;
        try
        {
            stored = await files.StoreRevisionAsync(
                drawing.Id, drawing.Customer, drawing.DrawingNumber, revisionNumber, pdf, source, ct);
        }
        catch (Exception exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Drawing storage unavailable",
                detail: $"The drawing file could not be stored: {exception.Message}");
        }
        var actor = Actor(http);
        var revision = new DrawingRevision
        {
            RevisionNumber = revisionNumber, RevisionDate = ParseDate(form["revisionDate"], DateTime.UtcNow.Date), UploadedAt = DateTime.UtcNow,
            EffectiveDate = ParseNullableDate(form["effectiveDate"]), ChangeDescription = changeDescription, Status = DrawingRevisionStatus.Draft,
            OriginalFileName = Path.GetFileName(pdf.FileName), StoredFilePath = stored.PdfRelativePath, FileType = drawingFile.ContentType,
            FileSize = pdf.Length, FileHash = stored.PdfHash, SourceOriginalFileName = source is null ? null : Path.GetFileName(source.FileName),
            SourceStoredFilePath = stored.SourceRelativePath, UploadedBy = actor, Notes = Clean(form["notes"])
        };
        drawing.Revisions.Add(revision);
        drawing.AuditEntries.Add(Audit(drawing, revisionNumber, "RevisionUploaded", $"Stored revision {revisionNumber} on the controlled drawing share; SHA-256 {stored.PdfHash}.", actor));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await using var staged = await files.StageDeletionAsync(stored.PdfRelativePath, ct);
            await staged.CompleteAsync(ct);
            throw;
        }
        return Results.Created($"/api/drawings/{id}", new { revision.Id });
    }

    private static async Task<IResult> SaveEditableDraftAsync(int id, HttpRequest request, EngineeringDbContext db, IDrawingFileStore files, HttpContext http, CancellationToken ct)
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new ErrorDto("FormRequired", "Use multipart form data."));

        var revision = await db.DrawingRevisions
            .Include(x => x.Drawing).ThenInclude(x => x.Revisions)
            .Include(x => x.Drawing).ThenInclude(x => x.AuditEntries)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (revision is null) return Results.NotFound();

        var form = await request.ReadFormAsync(ct);
        var pdf = form.Files.GetFile("pdf");
        var revisionNumber = form["revisionNumber"].ToString().Trim();
        var revisionDateValue = form["revisionDate"].ToString().Trim();
        var changeDescription = form["changeDescription"].ToString().Trim();

        if (string.IsNullOrWhiteSpace(revisionNumber) ||
            string.IsNullOrWhiteSpace(revisionDateValue) ||
            string.IsNullOrWhiteSpace(changeDescription))
            return Results.BadRequest(new ErrorDto("RequiredFields", "Revision number, revision date, and change description are required."));
        if (!DateTime.TryParse(revisionDateValue, out var revisionDate))
            return Results.BadRequest(new ErrorDto("InvalidRevisionDate", "Enter a valid revision date."));
        if (revision.Drawing.Revisions.Any(x =>
            x.Id != revision.Id &&
            string.Equals(x.RevisionNumber, revisionNumber, StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new ErrorDto("DuplicateRevision", "That revision number already exists for this drawing."));

        DrawingFileMetadata? replacementFile = null;
        if (pdf is { Length: > 0 })
        {
            if (pdf.Length > MaxFileBytes)
                return Results.BadRequest(new ErrorDto("FileTooLarge", "The drawing file must be 100 MB or smaller."));
            replacementFile = await DrawingFileValidation.InspectAsync(pdf, ct);
            if (replacementFile is null)
                return Results.BadRequest(new ErrorDto("InvalidDrawingFile", "Select a valid PDF or supported image file."));
        }

        var existingHasPdf = revision.FileSize > 0 && !string.IsNullOrWhiteSpace(revision.StoredFilePath);
        if (existingHasPdf &&
            !await files.VerifyHashAsync(revision.StoredFilePath, revision.FileHash, ct))
            return Results.Conflict(new ErrorDto(
                "FileIntegrityFailure",
                "The revision file is missing or no longer matches its controlled hash. No revision changes were saved."));

        StoredRevisionFiles? stored = null;
        IStagedFileDeletion? stagedOldPackage = null;
        var committed = false;
        try
        {
            if (pdf is { Length: > 0 })
            {
                var incomingHash = await CalculateHashAsync(pdf, ct);
                var isUnchangedPdf = existingHasPdf &&
                    string.Equals(incomingHash, revision.FileHash, StringComparison.OrdinalIgnoreCase);
                if (!isUnchangedPdf)
                {
                    if (revision.Drawing.Revisions.Any(x =>
                        x.Id != revision.Id &&
                        string.Equals(x.FileHash, incomingHash, StringComparison.OrdinalIgnoreCase)))
                        return Results.Conflict(new ErrorDto("DuplicateFile", "This exact file content is already stored on another revision for this drawing."));

                    stored = await files.StoreRevisionAsync(
                        revision.DrawingId,
                        revision.Drawing.Customer,
                        revision.Drawing.DrawingNumber,
                        revisionNumber,
                        pdf,
                        null,
                        ct);
                }
            }

            if (stored is not null && existingHasPdf)
                stagedOldPackage = await files.StageDeletionAsync(revision.StoredFilePath, ct);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var actor = Actor(http);
            var priorNumber = revision.RevisionNumber;
            var priorStatus = revision.Status;
            var priorFileHash = revision.FileHash;
            var wasCurrent = revision.Drawing.CurrentApprovedRevisionId == revision.Id;
            var priorControlledState = JsonSerializer.Serialize(new
            {
                Status = priorStatus.ToString(),
                revision.ApprovedBy,
                revision.ApprovalDate,
                revision.ApprovalComments,
                revision.EffectiveDate,
                revision.SupersededOrObsoleteAt,
                WasCurrent = wasCurrent
            });

            revision.RevisionNumber = revisionNumber;
            revision.RevisionDate = revisionDate.Date;
            revision.EffectiveDate = ParseNullableDate(form["effectiveDate"]);
            revision.ChangeDescription = changeDescription;
            revision.Notes = Clean(form["notes"]);
            if (stored is not null)
            {
                revision.OriginalFileName = Path.GetFileName(pdf!.FileName);
                revision.StoredFilePath = stored.PdfRelativePath;
                revision.FileType = replacementFile!.ContentType;
                revision.FileSize = pdf.Length;
                revision.FileHash = stored.PdfHash;
                revision.SourceOriginalFileName = null;
                revision.SourceStoredFilePath = null;
            }
            revision.Status = DrawingRevisionStatus.Draft;
            revision.ApprovalDate = null;
            revision.ApprovedBy = null;
            revision.ApprovalComments = null;
            revision.SupersededOrObsoleteAt = null;

            RecomputeDrawingApprovalAfterRevisionEdit(revision.Drawing, revision, wasCurrent);

            var action = priorStatus is DrawingRevisionStatus.Approved
                or DrawingRevisionStatus.Superseded
                or DrawingRevisionStatus.Obsolete
                ? "RevisionReopened"
                : "RevisionDraftUpdated";
            var fileChange = stored is not null
                ? $"The controlled drawing file was replaced; prior SHA-256 {priorFileHash}, new SHA-256 {stored.PdfHash}."
                : existingHasPdf
                    ? "The controlled drawing file package and hash were preserved."
                    : "The revision remains a metadata-only Draft with no drawing file attached.";
            revision.Drawing.AuditEntries.Add(Audit(
                revision.Drawing,
                revisionNumber,
                action,
                $"Updated existing revision ID {revision.Id} ({priorNumber}{(priorNumber == revisionNumber ? string.Empty : $" to {revisionNumber}")}) and saved it as Draft. " +
                $"Prior controlled state: {priorControlledState}. {fileChange}",
                actor));

            db.AllowControlledRevisionReopen = priorStatus is DrawingRevisionStatus.Approved
                or DrawingRevisionStatus.Superseded
                or DrawingRevisionStatus.Obsolete;
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            finally
            {
                db.AllowControlledRevisionReopen = false;
            }
            committed = true;
            if (stagedOldPackage is not null) await stagedOldPackage.CompleteAsync(ct);
            return Results.Ok(new RevisionEditResultDto(
                revision.Id,
                Created: false,
                HasPdf: revision.FileSize > 0 && !string.IsNullOrWhiteSpace(revision.StoredFilePath)));
        }
        catch
        {
            if (!committed && stored is not null)
            {
                await using var stagedNewPackage = await files.StageDeletionAsync(stored.PdfRelativePath, ct);
                await stagedNewPackage.CompleteAsync(ct);
            }
            throw;
        }
        finally
        {
            if (stagedOldPackage is not null) await stagedOldPackage.DisposeAsync();
        }
    }

    private static void RecomputeDrawingApprovalAfterRevisionEdit(
        Drawing drawing,
        DrawingRevision editedRevision,
        bool wasCurrent)
    {
        DrawingRevision? current = null;
        if (!wasCurrent && drawing.CurrentApprovedRevisionId is int currentId)
            current = drawing.Revisions.SingleOrDefault(x =>
                x.Id == currentId && x.Status == DrawingRevisionStatus.Approved);

        if (wasCurrent)
        {
            current = drawing.Revisions
                .Where(x => x.Id != editedRevision.Id && x.Status == DrawingRevisionStatus.Approved)
                .OrderByDescending(x => x.ApprovalDate)
                .ThenByDescending(x => x.Id)
                .FirstOrDefault();
            drawing.CurrentApprovedRevisionId = current?.Id;
        }
        else if (current is null && !drawing.IsObsolete)
        {
            current = drawing.Revisions
                .Where(x => x.Id != editedRevision.Id && x.Status == DrawingRevisionStatus.Approved)
                .OrderByDescending(x => x.ApprovalDate)
                .ThenByDescending(x => x.Id)
                .FirstOrDefault();
            drawing.CurrentApprovedRevisionId = current?.Id;
        }

        if (current is not null)
        {
            drawing.EffectiveDate = current.EffectiveDate;
            drawing.FileLocation = current.StoredFilePath;
            drawing.ApprovedBy = current.ApprovedBy;
            drawing.ApprovedAt = current.ApprovalDate;
        }
        else if (wasCurrent)
        {
            drawing.EffectiveDate = null;
            drawing.FileLocation = null;
            drawing.ApprovedBy = null;
            drawing.ApprovedAt = null;
        }

        drawing.ApprovalStatus = drawing.IsObsolete
            ? DrawingApprovalStatus.Obsolete
            : drawing.Revisions.Any(x => x.Status == DrawingRevisionStatus.UnderReview)
                ? DrawingApprovalStatus.UnderReview
                : current is not null
                    ? DrawingApprovalStatus.Approved
                    : DrawingApprovalStatus.Draft;
    }

    private static async Task<IResult> UpdateRevisionStatusAsync(int id, RevisionStatusUpdateDto dto, EngineeringDbContext db, HttpContext http, CancellationToken ct)
    {
        if (!Enum.TryParse<DrawingRevisionStatus>(dto.Status, true, out var status) || status is DrawingRevisionStatus.Approved or DrawingRevisionStatus.Superseded or DrawingRevisionStatus.Obsolete)
            return Results.BadRequest(new ErrorDto("InvalidStatus", "Draft or UnderReview are the allowed pre-approval statuses."));
        var revision = await db.DrawingRevisions
            .Include(x => x.Drawing).ThenInclude(x => x.AuditEntries)
            .Include(x => x.Drawing).ThenInclude(x => x.Revisions)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (revision is null) return Results.NotFound();
        if (revision.Status is DrawingRevisionStatus.Approved or DrawingRevisionStatus.Superseded or DrawingRevisionStatus.Obsolete)
            return Results.Conflict(new ErrorDto("ImmutableRevision", "Approved and historical revisions cannot be edited."));
        if (status == DrawingRevisionStatus.UnderReview &&
            (revision.FileSize <= 0 || string.IsNullOrWhiteSpace(revision.StoredFilePath)))
            return Results.Conflict(new ErrorDto("DrawingFileRequired", "A revision drawing file is required before submitting for review."));
        var old = revision.Status;
        revision.Status = status;
        revision.Drawing.ApprovalStatus = status == DrawingRevisionStatus.UnderReview
            ? DrawingApprovalStatus.UnderReview
            : revision.Drawing.Revisions.Any(x =>
                x.Id != revision.Id && x.Status == DrawingRevisionStatus.UnderReview)
                ? DrawingApprovalStatus.UnderReview
            : revision.Drawing.Revisions.Any(x => x.Status == DrawingRevisionStatus.Approved)
                ? DrawingApprovalStatus.Approved
                : DrawingApprovalStatus.Draft;
        var comments = string.IsNullOrWhiteSpace(dto.Comments) ? string.Empty : $" Comments: {dto.Comments.Trim()}";
        revision.Drawing.AuditEntries.Add(Audit(revision.Drawing, revision.RevisionNumber, "RevisionStatusChanged", $"Changed status from {old} to {status}.{comments}", Actor(http)));
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ApproveRevisionAsync(int id, RevisionApprovalDto dto, EngineeringDbContext db, IDrawingFileStore files, HttpContext http, CancellationToken ct)
    {
        var revision = await db.DrawingRevisions.Include(x => x.Drawing).ThenInclude(x => x.Revisions).Include(x => x.Drawing).ThenInclude(x => x.AuditEntries).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (revision is null) return Results.NotFound();
        if (revision.Status != DrawingRevisionStatus.UnderReview)
            return Results.Conflict(new ErrorDto("ReviewRequired", "Only a revision under review can be approved."));
        if (!await files.VerifyHashAsync(revision.StoredFilePath, revision.FileHash, ct))
            return Results.Conflict(new ErrorDto("FileIntegrityFailure", "The drawing file is missing or its hash no longer matches the uploaded revision. Approval was blocked."));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var actor = Actor(http);
        var prior = revision.Drawing.Revisions.SingleOrDefault(x => x.Status == DrawingRevisionStatus.Approved);
        if (prior is not null)
        {
            prior.Status = DrawingRevisionStatus.Superseded;
            prior.SupersededOrObsoleteAt = now;
        }
        revision.Status = DrawingRevisionStatus.Approved;
        revision.ApprovalDate = now;
        revision.ApprovedBy = actor;
        revision.ApprovalComments = Clean(dto.Comments);
        revision.EffectiveDate = dto.EffectiveDate ?? revision.EffectiveDate ?? now.Date;
        revision.Drawing.CurrentApprovedRevisionId = revision.Id;
        revision.Drawing.ApprovalStatus = DrawingApprovalStatus.Approved;
        revision.Drawing.IsObsolete = false;
        revision.Drawing.EffectiveDate = revision.EffectiveDate;
        revision.Drawing.FileLocation = revision.StoredFilePath;
        revision.Drawing.ApprovedBy = actor;
        revision.Drawing.ApprovedAt = now;
        revision.Drawing.AuditEntries.Add(Audit(revision.Drawing, revision.RevisionNumber, "RevisionApproved", $"Approved revision {revision.RevisionNumber}{(prior is null ? "" : $"; superseded {prior.RevisionNumber}")}.", actor));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MakeRevisionCurrentAsync(int id, EngineeringDbContext db, IDrawingFileStore files, HttpContext http, CancellationToken ct)
    {
        var revision = await db.DrawingRevisions
            .Include(x => x.Drawing).ThenInclude(x => x.Revisions)
            .Include(x => x.Drawing).ThenInclude(x => x.AuditEntries)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (revision is null) return Results.NotFound();
        if (revision.Drawing.CurrentApprovedRevisionId == revision.Id &&
            revision.Status == DrawingRevisionStatus.Approved &&
            !revision.Drawing.IsObsolete)
            return Results.Conflict(new ErrorDto("AlreadyCurrent", $"Revision {revision.RevisionNumber} is already the current revision."));
        if (revision.Status is not (DrawingRevisionStatus.Superseded or DrawingRevisionStatus.Obsolete))
            return Results.Conflict(new ErrorDto("HistoricalRevisionRequired", "Only a previously approved historical revision can be made current."));
        if (!await files.VerifyHashAsync(revision.StoredFilePath, revision.FileHash, ct))
            return Results.Conflict(new ErrorDto("FileIntegrityFailure", "The drawing file is missing or its hash no longer matches the stored revision. Activation was blocked."));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var actor = Actor(http);
        var prior = revision.Drawing.Revisions.SingleOrDefault(x =>
            x.Id != revision.Id && x.Status == DrawingRevisionStatus.Approved);
        if (prior is not null)
        {
            prior.Status = DrawingRevisionStatus.Superseded;
            prior.SupersededOrObsoleteAt = now;
        }

        revision.Status = DrawingRevisionStatus.Approved;
        revision.SupersededOrObsoleteAt = null;
        revision.Drawing.CurrentApprovedRevisionId = revision.Id;
        revision.Drawing.ApprovalStatus = DrawingApprovalStatus.Approved;
        revision.Drawing.IsObsolete = false;
        revision.Drawing.EffectiveDate = revision.EffectiveDate ?? revision.RevisionDate;
        revision.Drawing.FileLocation = revision.StoredFilePath;
        revision.Drawing.ApprovedBy = actor;
        revision.Drawing.ApprovedAt = now;
        revision.Drawing.AuditEntries.Add(Audit(
            revision.Drawing,
            revision.RevisionNumber,
            "RevisionReactivated",
            $"Made revision {revision.RevisionNumber} current{(prior is null ? "" : $"; superseded revision {prior.RevisionNumber}")}.",
            actor));

        db.AllowControlledHistoricalRevisionActivation = true;
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            db.AllowControlledHistoricalRevisionActivation = false;
        }
        return Results.NoContent();
    }

    private static async Task<IResult> DownloadAsync(int id, bool source, EngineeringDbContext db, IDrawingFileStore files, CancellationToken ct)
    {
        var revision = await db.DrawingRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (revision is null) return Results.NotFound();
        var relative = source ? revision.SourceStoredFilePath : revision.StoredFilePath;
        if (string.IsNullOrWhiteSpace(relative)) return Results.NotFound();
        var path = files.ResolvePath(relative);
        if (!File.Exists(path)) return Results.NotFound();
        return source
            ? Results.File(path, "application/octet-stream", revision.SourceOriginalFileName, enableRangeProcessing: true)
            : Results.File(path, revision.FileType, enableRangeProcessing: true);
    }

    private static async Task<IResult> DeleteRevisionAsync(int id, [FromBody] RevisionDeleteDto dto, EngineeringDbContext db, IDrawingFileStore files, HttpContext http, CancellationToken ct)
    {
        if (!dto.Confirmed)
            return Results.BadRequest(new ErrorDto("ConfirmationRequired", "Permanent deletion must be explicitly confirmed."));

        var revision = await db.DrawingRevisions
            .Include(x => x.Drawing).ThenInclude(x => x.AuditEntries)
            .Include(x => x.Drawing).ThenInclude(x => x.Revisions)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (revision is null) return Results.NotFound();
        if (revision.Drawing.CurrentApprovedRevisionId == revision.Id || revision.Status == DrawingRevisionStatus.Approved)
            return Results.Conflict(new ErrorDto("CurrentRevisionProtected", "The current approved revision cannot be deleted. Make another revision current first."));
        if (revision.Status is not (DrawingRevisionStatus.Draft or DrawingRevisionStatus.UnderReview or DrawingRevisionStatus.Superseded or DrawingRevisionStatus.Obsolete))
            return Results.Conflict(new ErrorDto("ProtectedRevision", "This revision cannot be deleted through the controlled workflow."));
        await using var stagedFiles = string.IsNullOrWhiteSpace(revision.StoredFilePath)
            ? null
            : await files.StageDeletionAsync(revision.StoredFilePath, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var actor = Actor(http);
        revision.Drawing.AuditEntries.Add(Audit(
            revision.Drawing,
            revision.RevisionNumber,
            "RevisionPermanentlyDeleted",
            $"Permanently deleted {revision.OriginalFileName} and its revision package. Recorded SHA-256 was {revision.FileHash}.",
            actor));
        db.DrawingRevisions.Remove(revision);
        var historicalRevision = revision.Status is DrawingRevisionStatus.Superseded or DrawingRevisionStatus.Obsolete;
        db.AllowControlledDraftRevisionDeletion = !historicalRevision;
        db.AllowControlledHistoricalRevisionDeletion = historicalRevision;
        revision.Drawing.ApprovalStatus = revision.Drawing.IsObsolete
            ? DrawingApprovalStatus.Obsolete
            : revision.Drawing.Revisions.Any(x => x.Id != revision.Id && x.Status == DrawingRevisionStatus.UnderReview)
                ? DrawingApprovalStatus.UnderReview
                : revision.Drawing.CurrentApprovedRevisionId.HasValue
                    ? DrawingApprovalStatus.Approved
                    : DrawingApprovalStatus.Draft;
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            if (stagedFiles is not null) await stagedFiles.CompleteAsync(ct);
        }
        finally
        {
            db.AllowControlledDraftRevisionDeletion = false;
            db.AllowControlledHistoricalRevisionDeletion = false;
        }
        return Results.NoContent();
    }

    private static async Task<IResult> RegisterMylarAsync(
        int id,
        MylarRegisterDto dto,
        MylarCustodyService custody,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await custody.RegisterAsync(id, dto.MylarNumber, dto.Location, dto.Note, Actor(http), ct);
        if (!result.Succeeded) return MylarError(result);
        var mylar = result.Mylar!;
        return Results.Created(
            $"/api/drawings/{id}/mylars/{mylar.Id}",
            ToMylarDto(mylar));
    }

    private static async Task<IResult> RecordMylarMovementAsync(
        int id,
        int mylarId,
        MylarActionDto dto,
        bool checkingOut,
        MylarCustodyService custody,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await custody.RecordMovementAsync(id, mylarId, checkingOut, dto.Location, dto.Note, Actor(http), ct);
        return result.Succeeded ? Results.NoContent() : MylarError(result);
    }

    private static IResult MylarError(MylarCustodyResult result) =>
        Results.Json(new ErrorDto(result.ErrorCode!, result.ErrorMessage!), statusCode: result.StatusCode);

    private static async Task<IResult> AddValidationAsync(int id, ValidationCreateDto dto, EngineeringDbContext db, HttpContext http, CancellationToken ct)
    {
        var drawing = await db.Drawings.Include(x => x.Validations).Include(x => x.AuditEntries).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (drawing is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(dto.ValidationType) || string.IsNullOrWhiteSpace(dto.Result)) return Results.BadRequest(new ErrorDto("RequiredFields", "Validation type and result are required."));
        var actor = Actor(http);
        drawing.Validations.Add(new DrawingValidation { ValidationType = dto.ValidationType.Trim(), Result = dto.Result.Trim(), Notes = Clean(dto.Notes), ValidatedBy = actor, ValidatedAt = DateTime.UtcNow });
        drawing.AuditEntries.Add(Audit(drawing, null, "ValidationRecorded", $"Recorded {dto.ValidationType.Trim()} validation: {dto.Result.Trim()}.", actor));
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static DrawingDetailDto ToDetail(Drawing x, IDrawingFileStore? files = null) => new(
        x.Id, x.DrawingNumber, x.Title, x.Customer, x.Parts.OrderBy(p => p.PartNumber).Select(p => p.PartNumber).ToList(),
        x.ApprovalStatus.ToString(),
        x.CurrentApprovedRevisionId is int currentRevisionId
            ? x.Revisions.SingleOrDefault(r => r.Id == currentRevisionId)?.RevisionNumber
            : x.Revisions.OrderByDescending(r => r.UploadedAt).Select(r => r.RevisionNumber).FirstOrDefault(),
        x.EffectiveDate, x.IsObsolete, x.FileLocation, x.Notes,
        x.Mylars.Count == 1 ? x.Mylars[0].CurrentLocation : null,
        x.Mylars.Any(m => m.IsCheckedOut),
        x.Mylars.Count(m => m.IsCheckedOut) == 1 ? x.Mylars.Single(m => m.IsCheckedOut).CheckedOutBy : null,
        x.Mylars.Where(m => m.IsCheckedOut).Max(m => (DateTime?)m.CheckedOutAt),
        x.Mylars.Count,
        x.Mylars.Count(m => m.IsCheckedOut),
        x.CreatedBy, x.CreatedAt, x.ApprovedBy, x.ApprovedAt,
        x.CurrentApprovedRevisionId,
        x.Revisions.OrderByDescending(r => r.UploadedAt).Select(r => new DrawingRevisionDto(
            r.Id, r.RevisionNumber, r.RevisionDate, r.UploadedAt, r.EffectiveDate, r.ApprovalDate,
            r.ChangeDescription, r.Status.ToString(), r.OriginalFileName, r.FileType, r.FileSize, r.FileHash,
            r.FileSize > 0 && r.StoredFilePath != string.Empty,
            ControlledFilePath(r.StoredFilePath, files),
            r.SourceStoredFilePath != null, r.UploadedBy, r.ApprovedBy, r.ApprovalComments,
            r.SupersededOrObsoleteAt, r.Notes)).ToList(),
        x.DocumentLinks.Select(d => new DrawingDocumentLinkDto(d.Id, d.Kind.ToString(), d.ReferenceNumber, d.Title, d.Location)).ToList(),
        x.Validations.OrderByDescending(v => v.ValidatedAt).Select(v => new DrawingValidationDto(v.Id, v.ValidationType, v.Result, v.Notes, v.ValidatedBy, v.ValidatedAt)).ToList(),
        x.Mylars.OrderBy(m => m.MylarNumber).Select(ToMylarDto).ToList(),
        x.MylarTransactions.OrderByDescending(m => m.RecordedAt).Select(m => new MylarTransactionDto(
            m.Id,
            m.DrawingMylarId,
            x.Mylars.SingleOrDefault(mylar => mylar.Id == m.DrawingMylarId)?.MylarNumber ?? "Legacy Mylar",
            m.Type.ToString(),
            m.RecordedBy,
            m.Purpose,
            m.Location,
            m.RecordedAt)).ToList(),
        x.AuditEntries.OrderByDescending(a => a.OccurredAt).Select(a => new DrawingAuditDto(a.Id, a.RevisionNumber, a.Action, a.Details, a.Actor, a.OccurredAt)).ToList());

    private static DrawingMylarDto ToMylarDto(DrawingMylar mylar) => new(
        mylar.Id,
        mylar.MylarNumber,
        mylar.IsCheckedOut,
        mylar.CurrentLocation,
        mylar.CheckedOutBy,
        mylar.CheckedOutAt,
        mylar.CreatedBy,
        mylar.CreatedAt,
        mylar.Transactions.Count);

    private static DrawingAuditEntry Audit(Drawing drawing, string? revision, string action, string details, string actor) => new() { Drawing = drawing, RevisionNumber = revision, Action = action, Details = details, Actor = actor, OccurredAt = DateTime.UtcNow };
    private static string? ControlledFilePath(string? relativePath, IDrawingFileStore? files)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        if (files is not null)
        {
            try
            {
                return files.ResolvePath(relativePath);
            }
            catch (InvalidOperationException)
            {
                // Keep the record readable while the controlled share is being configured or is temporarily unavailable.
            }
        }
        return $"Engineering Drawings/{relativePath.Replace('\\', '/')}";
    }
    private static string Actor(HttpContext http) => http.User.Identity?.Name ?? "Unknown";
    private static string EscapeLikePattern(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private static string Normalize(string value) => string.Concat(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit));
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTime ParseDate(string? value, DateTime fallback) => DateTime.TryParse(value, out var parsed) ? parsed : fallback;
    private static DateTime? ParseNullableDate(string? value) => DateTime.TryParse(value, out var parsed) ? parsed : null;
    private static async Task<string> CalculateHashAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken));
    }
}
