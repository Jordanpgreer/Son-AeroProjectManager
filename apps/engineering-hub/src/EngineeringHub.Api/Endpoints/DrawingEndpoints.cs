using EngineeringHub.Api.Data;
using EngineeringHub.Api.Dtos;
using EngineeringHub.Api.Models;
using EngineeringHub.Api.Services;
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
        api.MapPost("/drawings", CreateAsync);
        api.MapDelete("/drawings/{id:int}", DeleteDrawingAsync);
        api.MapGet("/drawing-storage/status", (IDrawingFileStore files) => Results.Ok(files.GetStatus()));
        api.MapPost("/drawings/{id:int}/revisions", UploadRevisionAsync).DisableAntiforgery();
        api.MapPut("/drawing-revisions/{id:int}/status", UpdateRevisionStatusAsync);
        api.MapPost("/drawing-revisions/{id:int}/approve", ApproveRevisionAsync);
        api.MapDelete("/drawing-revisions/{id:int}", DeleteRevisionAsync);
        api.MapGet("/drawing-revisions/{id:int}/file", (int id, EngineeringDbContext db, IDrawingFileStore files, CancellationToken ct) => DownloadAsync(id, false, db, files, ct));
        api.MapGet("/drawing-revisions/{id:int}/source", (int id, EngineeringDbContext db, IDrawingFileStore files, CancellationToken ct) => DownloadAsync(id, true, db, files, ct));
        api.MapPost("/drawings/{id:int}/mylar/checkout", (int id, MylarActionDto dto, EngineeringDbContext db, HttpContext http, CancellationToken ct) => MylarAsync(id, dto, true, db, http, ct));
        api.MapPost("/drawings/{id:int}/mylar/return", (int id, MylarActionDto dto, EngineeringDbContext db, HttpContext http, CancellationToken ct) => MylarAsync(id, dto, false, db, http, ct));
        api.MapPost("/drawings/{id:int}/validations", AddValidationAsync);
    }

    private static async Task<IResult> ListAsync(string? query, EngineeringDbContext db, CancellationToken ct)
    {
        var drawings = db.Drawings.AsNoTracking()
            .Include(x => x.Parts)
            .Include(x => x.DocumentLinks)
            .Include(x => x.CurrentApprovedRevision)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var value = query.Trim();
            drawings = drawings.Where(x => x.DrawingNumber.Contains(value) || x.Title.Contains(value) ||
                x.Customer.Contains(value) || x.Parts.Any(p => p.PartNumber.Contains(value)) ||
                x.DocumentLinks.Any(link => link.ReferenceNumber.Contains(value) || (link.Title != null && link.Title.Contains(value))) ||
                (x.Notes != null && x.Notes.Contains(value)));
        }
        var records = await drawings.OrderBy(x => x.DrawingNumber).Select(x => new DrawingListDto(
            x.Id, x.DrawingNumber, x.Title, x.Customer, x.Parts.OrderBy(p => p.PartNumber).Select(p => p.PartNumber).ToList(),
            x.ApprovalStatus.ToString(),
            x.CurrentApprovedRevision != null
                ? x.CurrentApprovedRevision.RevisionNumber
                : x.Revisions.OrderByDescending(r => r.UploadedAt).Select(r => r.RevisionNumber).FirstOrDefault(),
            x.CurrentApprovedRevision != null
                ? x.CurrentApprovedRevision.RevisionDate
                : x.Revisions.OrderByDescending(r => r.UploadedAt).Select(r => (DateTime?)r.RevisionDate).FirstOrDefault(),
            x.EffectiveDate, x.IsObsolete, x.PhysicalMylarLocation, x.IsMylarCheckedOut, x.CreatedAt,
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

    private static async Task<IResult> GetAsync(int id, EngineeringDbContext db, CancellationToken ct)
    {
        var drawing = await db.Drawings.AsNoTracking()
            .Include(x => x.Parts).Include(x => x.Revisions).Include(x => x.DocumentLinks)
            .Include(x => x.Validations).Include(x => x.MylarTransactions).Include(x => x.AuditEntries)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        return drawing is null ? Results.NotFound() : Results.Ok(ToDetail(drawing));
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
            .Include(x => x.Validations)
            .Include(x => x.MylarTransactions)
            .Include(x => x.AuditEntries)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (drawing is null) return Results.NotFound();
        if (drawing.ApprovalStatus != DrawingApprovalStatus.Draft || drawing.Revisions.Count != 0 ||
            drawing.Validations.Count != 0 || drawing.MylarTransactions.Count != 0)
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
        if (pdf is null || pdf.Length == 0 || !await IsValidPdfAsync(pdf, ct))
            return Results.BadRequest(new ErrorDto("PdfRequired", "An approved-view PDF file is required."));
        if (pdf.Length > MaxFileBytes || source?.Length > MaxFileBytes)
            return Results.BadRequest(new ErrorDto("FileTooLarge", "Each file must be 100 MB or smaller."));
        if (string.IsNullOrWhiteSpace(revisionNumber) || string.IsNullOrWhiteSpace(changeDescription))
            return Results.BadRequest(new ErrorDto("RequiredFields", "Revision number and change description are required."));
        if (drawing.Revisions.Any(x => string.Equals(x.RevisionNumber, revisionNumber, StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new ErrorDto("DuplicateRevision", "That revision already exists for this drawing."));

        var incomingHash = await CalculateHashAsync(pdf, ct);
        if (drawing.Revisions.Any(x => string.Equals(x.FileHash, incomingHash, StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new ErrorDto("DuplicateFile", "This exact PDF content is already stored on another revision for this drawing."));

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
                detail: $"The PDF could not be stored: {exception.Message}");
        }
        var actor = Actor(http);
        var revision = new DrawingRevision
        {
            RevisionNumber = revisionNumber, RevisionDate = ParseDate(form["revisionDate"], DateTime.UtcNow.Date), UploadedAt = DateTime.UtcNow,
            EffectiveDate = ParseNullableDate(form["effectiveDate"]), ChangeDescription = changeDescription, Status = DrawingRevisionStatus.Draft,
            OriginalFileName = Path.GetFileName(pdf.FileName), StoredFilePath = stored.PdfRelativePath, FileType = "application/pdf",
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
        var old = revision.Status;
        revision.Status = status;
        revision.Drawing.ApprovalStatus = status == DrawingRevisionStatus.UnderReview
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
            return Results.Conflict(new ErrorDto("FileIntegrityFailure", "The drawing PDF is missing or its hash no longer matches the uploaded revision. Approval was blocked."));
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
            : Results.File(path, "application/pdf", enableRangeProcessing: true);
    }

    private static async Task<IResult> DeleteRevisionAsync(int id, [FromBody] RevisionDeleteDto dto, EngineeringDbContext db, IDrawingFileStore files, HttpContext http, CancellationToken ct)
    {
        if (!dto.Confirmed)
            return Results.BadRequest(new ErrorDto("ConfirmationRequired", "Permanent deletion must be explicitly confirmed."));

        var revision = await db.DrawingRevisions
            .Include(x => x.Drawing).ThenInclude(x => x.AuditEntries)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (revision is null) return Results.NotFound();
        if (revision.Status is not (DrawingRevisionStatus.Draft or DrawingRevisionStatus.UnderReview))
            return Results.Conflict(new ErrorDto("ProtectedRevision", "Approved and historical revisions cannot be deleted through the normal application workflow."));
        if (!string.Equals(dto.FileName, revision.OriginalFileName, StringComparison.Ordinal))
            return Results.BadRequest(new ErrorDto("FileNameMismatch", "The entered filename does not exactly match the uploaded PDF filename."));

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
        db.AllowControlledDraftRevisionDeletion = true;
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            if (stagedFiles is not null) await stagedFiles.CompleteAsync(ct);
        }
        finally
        {
            db.AllowControlledDraftRevisionDeletion = false;
        }
        return Results.NoContent();
    }

    private static async Task<IResult> MylarAsync(int id, MylarActionDto dto, bool checkout, EngineeringDbContext db, HttpContext http, CancellationToken ct)
    {
        var drawing = await db.Drawings.Include(x => x.MylarTransactions).Include(x => x.AuditEntries).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (drawing is null) return Results.NotFound();
        if (checkout == drawing.IsMylarCheckedOut)
            return Results.Conflict(new ErrorDto("MylarState", checkout ? "The Mylar is already checked out." : "The Mylar is not checked out."));
        if (string.IsNullOrWhiteSpace(dto.Person)) return Results.BadRequest(new ErrorDto("PersonRequired", "A responsible person is required."));
        var actor = Actor(http);
        drawing.IsMylarCheckedOut = checkout;
        drawing.MylarCheckedOutBy = checkout ? dto.Person.Trim() : null;
        drawing.MylarCheckedOutAt = checkout ? DateTime.UtcNow : null;
        if (!string.IsNullOrWhiteSpace(dto.Location)) drawing.PhysicalMylarLocation = dto.Location.Trim();
        drawing.MylarTransactions.Add(new MylarTransaction { Type = checkout ? MylarTransactionType.CheckedOut : MylarTransactionType.Returned, Person = dto.Person.Trim(), Purpose = Clean(dto.Purpose), Location = Clean(dto.Location), RecordedBy = actor, RecordedAt = DateTime.UtcNow });
        drawing.AuditEntries.Add(Audit(drawing, null, checkout ? "MylarCheckedOut" : "MylarReturned", $"Physical Mylar {(checkout ? "checked out to" : "returned by")} {dto.Person.Trim()}.", actor));
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

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

    private static DrawingDetailDto ToDetail(Drawing x) => new(
        x.Id, x.DrawingNumber, x.Title, x.Customer, x.Parts.OrderBy(p => p.PartNumber).Select(p => p.PartNumber).ToList(),
        x.ApprovalStatus.ToString(), x.EffectiveDate, x.IsObsolete, x.FileLocation, x.Notes, x.PhysicalMylarLocation,
        x.IsMylarCheckedOut, x.MylarCheckedOutBy, x.MylarCheckedOutAt, x.CreatedBy, x.CreatedAt, x.ApprovedBy, x.ApprovedAt,
        x.CurrentApprovedRevisionId,
        x.Revisions.OrderByDescending(r => r.UploadedAt).Select(r => new DrawingRevisionDto(r.Id, r.RevisionNumber, r.RevisionDate, r.UploadedAt, r.EffectiveDate, r.ApprovalDate, r.ChangeDescription, r.Status.ToString(), r.OriginalFileName, r.FileType, r.FileSize, r.FileHash, r.FileSize > 0 && r.StoredFilePath != string.Empty, r.SourceStoredFilePath != null, r.UploadedBy, r.ApprovedBy, r.ApprovalComments, r.SupersededOrObsoleteAt, r.Notes)).ToList(),
        x.DocumentLinks.Select(d => new DrawingDocumentLinkDto(d.Id, d.Kind.ToString(), d.ReferenceNumber, d.Title, d.Location)).ToList(),
        x.Validations.OrderByDescending(v => v.ValidatedAt).Select(v => new DrawingValidationDto(v.Id, v.ValidationType, v.Result, v.Notes, v.ValidatedBy, v.ValidatedAt)).ToList(),
        x.MylarTransactions.OrderByDescending(m => m.RecordedAt).Select(m => new MylarTransactionDto(m.Id, m.Type.ToString(), m.Person, m.Purpose, m.Location, m.RecordedBy, m.RecordedAt)).ToList(),
        x.AuditEntries.OrderByDescending(a => a.OccurredAt).Select(a => new DrawingAuditDto(a.Id, a.RevisionNumber, a.Action, a.Details, a.Actor, a.OccurredAt)).ToList());

    private static DrawingAuditEntry Audit(Drawing drawing, string? revision, string action, string details, string actor) => new() { Drawing = drawing, RevisionNumber = revision, Action = action, Details = details, Actor = actor, OccurredAt = DateTime.UtcNow };
    private static string Actor(HttpContext http) => http.User.Identity?.Name ?? "Unknown";
    private static string Normalize(string value) => string.Concat(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit));
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTime ParseDate(string? value, DateTime fallback) => DateTime.TryParse(value, out var parsed) ? parsed : fallback;
    private static DateTime? ParseNullableDate(string? value) => DateTime.TryParse(value, out var parsed) ? parsed : null;
    private static async Task<bool> IsValidPdfAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase)) return false;
        await using var stream = file.OpenReadStream();
        var signature = new byte[5];
        var read = await stream.ReadAsync(signature, cancellationToken);
        return read == signature.Length && signature.SequenceEqual("%PDF-"u8.ToArray());
    }
    private static async Task<string> CalculateHashAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken));
    }
}
