using System.Text.Json;
using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Dtos;
using EngineeringHub.Api.Models;
using EngineeringHub.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using SonAero.Platform.Security;

namespace EngineeringHub.Api.Endpoints;

public static class DrawingOperationalEndpoints
{
    private const long MaxFileBytes = 100 * 1024 * 1024;

    public static void MapDrawingOperationalEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/drawings/create-with-revision", CreateWithRevisionAsync)
            .DisableAntiforgery()
            .RequireAuthorization(EngineeringPermissions.DrawingCreate);
        api.MapPut("/drawings/{id:int}", UpdateDrawingAsync);
        api.MapPost("/drawings/{id:int}/supplemental-documents", AddSupplementalDocumentAsync)
            .DisableAntiforgery()
            .RequireAuthorization(EngineeringPermissions.SupportingDocumentsManage);
        api.MapGet("/drawing-documents/{id:int}/file", DownloadSupplementalDocumentAsync)
            .RequireAuthorization(EngineeringPermissions.SupportingDocumentsView);
        api.MapDelete("/drawing-documents/{id:int}", DeleteSupplementalDocumentAsync)
            .RequireAuthorization(EngineeringPermissions.SupportingDocumentsManage);
        api.MapGet("/drawing-review-queue", GetReviewQueueAsync)
            .RequireAuthorization(EngineeringPermissions.PendingRevisionsView);
        api.MapPost("/drawings/{id:int}/archive", ArchiveDrawingAsync)
            .RequireAuthorization(EngineeringPermissions.DrawingArchive);
        api.MapPost("/drawings/{id:int}/obsolete", ArchiveDrawingAsync)
            .RequireAuthorization(EngineeringPermissions.DrawingArchive);
    }

    private static async Task<IResult> AddSupplementalDocumentAsync(
        int id,
        HttpRequest request,
        EngineeringDbContext db,
        IDrawingFileStore files,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new ErrorDto("FormRequired", "Use multipart form data."));

        var drawing = await db.Drawings
            .Include(x => x.DocumentLinks)
            .Include(x => x.Revisions)
            .Include(x => x.AuditEntries)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (drawing is null) return Results.NotFound();
        if (drawing.IsObsolete)
            return Results.Conflict(new ErrorDto("ObsoleteDrawing", "Archived drawing attachments are locked."));

        var form = await request.ReadFormAsync(cancellationToken);
        var label = form["label"].ToString().Trim();
        if (!int.TryParse(form["revisionId"], out var revisionId))
            return Results.BadRequest(new ErrorDto("RevisionRequired", "Select the revision this supporting document belongs to."));
        var revision = drawing.Revisions.SingleOrDefault(item => item.Id == revisionId);
        if (revision is null)
            return Results.BadRequest(new ErrorDto("InvalidRevision", "The selected revision does not belong to this drawing."));
        if (revision.Status is not (DrawingRevisionStatus.Draft or DrawingRevisionStatus.UnderReview))
            return Results.Conflict(new ErrorDto("RevisionLocked", "Supporting documents can only be changed while a revision is draft or under review."));
        var document = form.Files.GetFile("document");
        if (string.IsNullOrWhiteSpace(label))
            return Results.BadRequest(new ErrorDto("LabelRequired", "Enter a label for the supplemental document."));
        if (label.Length > 120)
            return Results.BadRequest(new ErrorDto("LabelTooLong", "Supplemental document labels must be 120 characters or fewer."));
        if (document is null || document.Length == 0)
            return Results.BadRequest(new ErrorDto("DocumentRequired", "Select a supplemental document to upload."));
        if (document.Length > MaxFileBytes)
            return Results.BadRequest(new ErrorDto("FileTooLarge", "The supplemental document must be 100 MB or smaller."));
        if (drawing.DocumentLinks.Any(link =>
                link.Kind == DrawingDocumentKind.SupplementalDocument &&
                link.DrawingRevisionId == revisionId &&
                string.Equals(link.ReferenceNumber, label, StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new ErrorDto("DuplicateLabel", "That supporting document label is already in use on this revision."));

        var metadata = await SupplementalFileValidation.InspectAsync(document, cancellationToken);
        if (metadata is null)
            return Results.BadRequest(new ErrorDto(
                "UnsupportedDocument",
                "Select a supported PDF, image, Office, text, CAD, STEP/IGES, or ZIP document."));

        StoredSupplementalFile stored;
        try
        {
            stored = await files.StoreSupplementalAsync(
                drawing.Id,
                drawing.Customer,
                drawing.DrawingNumber,
                document,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Supplemental document storage unavailable",
                detail: $"The supplemental document could not be stored: {exception.Message}");
        }

        var actor = Actor(http);
        var link = new DrawingDocumentLink
        {
            DrawingRevisionId = revisionId,
            Kind = DrawingDocumentKind.SupplementalDocument,
            ReferenceNumber = label,
            Title = SafeFileName(document.FileName),
            Location = stored.RelativePath
        };
        drawing.DocumentLinks.Add(link);
        drawing.AuditEntries.Add(Audit(
            drawing,
            revision.RevisionNumber,
            "SupplementalDocumentUploaded",
            $"Uploaded supporting document '{label}' ({link.Title}) to revision {revision.RevisionNumber}; SHA-256 {stored.Hash}.",
            actor));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await using var staged = await files.StageDeletionAsync(stored.RelativePath, cancellationToken);
            await staged.CompleteAsync(cancellationToken);
            throw;
        }

        return Results.Created(
            $"/api/drawing-documents/{link.Id}/file",
            new DrawingDocumentLinkDto(link.Id, link.DrawingRevisionId, link.Kind.ToString(), link.ReferenceNumber, link.Title, link.Location));
    }

    private static async Task<IResult> DownloadSupplementalDocumentAsync(
        int id,
        EngineeringDbContext db,
        IDrawingFileStore files,
        CancellationToken cancellationToken)
    {
        var document = await db.DrawingDocumentLinks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.Kind == DrawingDocumentKind.SupplementalDocument, cancellationToken);
        if (document is null || string.IsNullOrWhiteSpace(document.Location)) return Results.NotFound();
        var path = files.ResolvePath(document.Location);
        if (!File.Exists(path)) return Results.NotFound();
        var fileName = document.Title ?? $"supplemental{Path.GetExtension(path)}";
        var contentType = SupplementalFileValidation.GetContentType(fileName);
        return contentType == "application/pdf" || contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? Results.File(path, contentType, enableRangeProcessing: true)
            : Results.File(path, contentType, fileName, enableRangeProcessing: true);
    }

    private static async Task<IResult> DeleteSupplementalDocumentAsync(
        int id,
        [FromBody] RevisionDeleteDto dto,
        EngineeringDbContext db,
        IDrawingFileStore files,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!dto.Confirmed)
            return Results.BadRequest(new ErrorDto("ConfirmationRequired", "Permanent deletion must be explicitly confirmed."));
        var document = await db.DrawingDocumentLinks
            .Include(x => x.Drawing).ThenInclude(x => x.AuditEntries)
            .Include(x => x.DrawingRevision)
            .SingleOrDefaultAsync(x => x.Id == id && x.Kind == DrawingDocumentKind.SupplementalDocument, cancellationToken);
        if (document is null) return Results.NotFound();
        if (document.Drawing.IsObsolete)
            return Results.Conflict(new ErrorDto("ObsoleteDrawing", "Archived drawing attachments are locked."));
        if (document.DrawingRevision is not null &&
            document.DrawingRevision.Status is not (DrawingRevisionStatus.Draft or DrawingRevisionStatus.UnderReview))
            return Results.Conflict(new ErrorDto("RevisionLocked", "Supporting documents cannot be changed after a revision is approved or superseded."));

        var usedElsewhere = !string.IsNullOrWhiteSpace(document.Location) &&
            await db.DrawingDocumentLinks.AnyAsync(
                link => link.Id != document.Id && link.Location == document.Location,
                cancellationToken);
        await using var staged = string.IsNullOrWhiteSpace(document.Location) || usedElsewhere
            ? null
            : await files.StageDeletionAsync(document.Location, cancellationToken);
        document.Drawing.AuditEntries.Add(Audit(
            document.Drawing,
            document.DrawingRevision?.RevisionNumber,
            "SupplementalDocumentDeleted",
            $"Removed supporting document '{document.ReferenceNumber}' ({document.Title ?? "unnamed file"}) from revision {document.DrawingRevision?.RevisionNumber ?? "legacy"}.",
            Actor(http)));
        db.DrawingDocumentLinks.Remove(document);
        await db.SaveChangesAsync(cancellationToken);
        if (staged is not null) await staged.CompleteAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateWithRevisionAsync(
        HttpRequest request,
        EngineeringDbContext db,
        IDrawingFileStore files,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new ErrorDto("FormRequired", "Use multipart form data."));

        var form = await request.ReadFormAsync(cancellationToken);
        var drawingNumber = form["drawingNumber"].ToString().Trim();
        var title = form["title"].ToString().Trim();
        var customer = form["customer"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(drawingNumber) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(customer))
            return Results.BadRequest(new ErrorDto("RequiredFields", "Drawing number, title / description, and design authority are required."));

        var normalizedNumber = Normalize(drawingNumber);
        var normalizedCustomer = Normalize(customer);
        if (await db.Drawings.AnyAsync(
                x => x.NormalizedDrawingNumber == normalizedNumber && x.NormalizedCustomer == normalizedCustomer,
                cancellationToken))
            return Results.Conflict(new ErrorDto("DuplicateDrawing", "That drawing number already exists for this design authority."));

        var pdf = form.Files.GetFile("pdf");
        var source = form.Files.GetFile("source");
        var hasDrawingFile = pdf is { Length: > 0 };
        var revisionNumber = form["revisionNumber"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(revisionNumber))
            return Results.BadRequest(new ErrorDto("CurrentRevisionRequired", "Current revision is required."));
        var drawingFile = hasDrawingFile
            ? await DrawingFileValidation.InspectAsync(pdf!, cancellationToken)
            : null;
        if (hasDrawingFile && drawingFile is null)
            return Results.BadRequest(new ErrorDto("InvalidDrawingFile", "Select a valid PDF or supported image file."));
        if ((pdf?.Length ?? 0) > MaxFileBytes || (source?.Length ?? 0) > MaxFileBytes)
            return Results.BadRequest(new ErrorDto("FileTooLarge", "Each file must be 100 MB or smaller."));

        var actor = Actor(http);
        var drawing = new Drawing
        {
            DrawingNumber = drawingNumber,
            NormalizedDrawingNumber = normalizedNumber,
            Title = title,
            Customer = customer,
            NormalizedCustomer = normalizedCustomer,
            Notes = Clean(form["notes"]),
            PhysicalMylarLocation = Clean(form["mylarLocation"]),
            CreatedBy = actor,
            CreatedAt = DateTime.UtcNow
        };
        AddPartsFromCsv(drawing, form["partNumbers"]);
        AddLinks(drawing, ParseLinks(form["relatedDocumentsJson"]));
        drawing.AuditEntries.Add(Audit(
            drawing,
            revisionNumber,
            "DrawingCreated",
            hasDrawingFile
                ? $"Created drawing record at current revision {revisionNumber} with a controlled drawing file."
                : $"Created metadata-only drawing record at current revision {revisionNumber}.",
            actor));

        StoredRevisionFiles? stored = null;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            db.Drawings.Add(drawing);
            await db.SaveChangesAsync(cancellationToken);
            if (hasDrawingFile)
            {
                stored = await files.StoreRevisionAsync(
                    drawing.Id,
                    drawing.Customer,
                    drawing.DrawingNumber,
                    revisionNumber,
                    pdf!,
                    source,
                    cancellationToken);
            }

            var revision = new DrawingRevision
            {
                RevisionNumber = revisionNumber,
                RevisionDate = DateTime.UtcNow.Date,
                UploadedAt = DateTime.UtcNow,
                EffectiveDate = ParseNullableDate(form["effectiveDate"]),
                ChangeDescription = $"Drawing record created at current revision {revisionNumber}.",
                Status = DrawingRevisionStatus.Draft,
                OriginalFileName = hasDrawingFile ? SafeFileName(pdf!.FileName) : string.Empty,
                StoredFilePath = stored?.PdfRelativePath ?? string.Empty,
                FileType = drawingFile?.ContentType ?? "application/octet-stream",
                FileSize = pdf?.Length ?? 0,
                FileHash = stored?.PdfHash ?? string.Empty,
                SourceOriginalFileName = source is { Length: > 0 } ? SafeFileName(source.FileName) : null,
                SourceStoredFilePath = stored?.SourceRelativePath,
                UploadedBy = actor,
                Notes = null
            };
            drawing.Revisions.Add(revision);
            if (hasDrawingFile)
            {
                drawing.AuditEntries.Add(Audit(
                    drawing,
                    revisionNumber,
                    "CurrentDrawingFileUploaded",
                    $"Stored current drawing file {revision.OriginalFileName}; SHA-256 {stored!.PdfHash}.",
                    actor));
            }
            await db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return Results.Created($"/api/drawings/{drawing.Id}", new { drawing.Id });
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (stored is not null)
            {
                await using var staged = await files.StageDeletionAsync(stored.PdfRelativePath, cancellationToken);
                await staged.CompleteAsync(cancellationToken);
            }
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Drawing creation failed",
                detail: $"The drawing and file package were rolled back: {exception.Message}");
        }
    }

    private static async Task<IResult> UpdateDrawingAsync(
        int id,
        [FromBody] DrawingUpdateDto dto,
        EngineeringDbContext db,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Title) || string.IsNullOrWhiteSpace(dto.Customer))
            return Results.BadRequest(new ErrorDto("RequiredFields", "Title / description and design authority are required."));

        var drawing = await db.Drawings
            .Include(x => x.Parts)
            .Include(x => x.DocumentLinks)
            .Include(x => x.AuditEntries)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (drawing is null) return Results.NotFound();
        if (drawing.IsObsolete)
            return Results.Conflict(new ErrorDto("ObsoleteDrawing", "Archived drawing metadata is locked."));

        var newCustomer = dto.Customer.Trim();
        var normalizedCustomer = Normalize(newCustomer);
        if (await db.Drawings.AnyAsync(
                x => x.Id != id &&
                    x.NormalizedCustomer == normalizedCustomer &&
                    x.NormalizedDrawingNumber == drawing.NormalizedDrawingNumber,
                cancellationToken))
            return Results.Conflict(new ErrorDto("DuplicateDrawing", "That drawing number already exists for the selected design authority."));

        var requestedParts = (dto.PartNumbers ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentParts = drawing.Parts.Select(part => part.PartNumber)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentSpecifications = drawing.DocumentLinks
            .Where(link => link.Kind == DrawingDocumentKind.Specification)
            .Select(link => LinkIdentity(new DrawingDocumentLinkCreateDto(
                link.Kind.ToString(), link.ReferenceNumber, link.Title, link.Location)))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requestedSpecifications = dto.RelatedDocuments is null
            ? currentSpecifications
            : dto.RelatedDocuments
                .Where(link => Enum.TryParse<DrawingDocumentKind>(link.Kind, true, out var kind) && kind == DrawingDocumentKind.Specification)
                .Select(LinkIdentity)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var metadataChanged =
            !string.Equals(drawing.Title, dto.Title.Trim(), StringComparison.Ordinal) ||
            !string.Equals(drawing.Customer, newCustomer, StringComparison.Ordinal) ||
            !string.Equals(drawing.Notes, Clean(dto.Notes), StringComparison.Ordinal) ||
            !string.Equals(drawing.PhysicalMylarLocation, Clean(dto.PhysicalMylarLocation), StringComparison.Ordinal) ||
            !currentParts.SequenceEqual(requestedParts, StringComparer.OrdinalIgnoreCase);
        var specificationsChanged = !currentSpecifications.SequenceEqual(requestedSpecifications, StringComparer.OrdinalIgnoreCase);
        if (metadataChanged && !HasPermission(http, EngineeringPermissions.DrawingMetadataEdit)) return Results.Forbid();
        if (specificationsChanged && !HasPermission(http, EngineeringPermissions.SpecificationsEdit)) return Results.Forbid();
        if (!metadataChanged && !specificationsChanged) return Results.NoContent();

        var before = Snapshot(drawing);
        drawing.Title = dto.Title.Trim();
        drawing.Customer = newCustomer;
        drawing.NormalizedCustomer = normalizedCustomer;
        drawing.Notes = Clean(dto.Notes);
        drawing.PhysicalMylarLocation = Clean(dto.PhysicalMylarLocation);
        drawing.Parts.Clear();
        AddParts(drawing, dto.PartNumbers ?? []);
        if (dto.RelatedDocuments is not null)
        {
            foreach (var editableLink in drawing.DocumentLinks.Where(link =>
                         link.Kind is DrawingDocumentKind.Specification).ToList())
                drawing.DocumentLinks.Remove(editableLink);
            AddLinks(drawing, dto.RelatedDocuments.Where(link =>
                Enum.TryParse<DrawingDocumentKind>(link.Kind, true, out var kind) &&
                kind is DrawingDocumentKind.Specification));
        }
        var after = Snapshot(drawing);
        if (before == after) return Results.NoContent();

        drawing.AuditEntries.Add(Audit(
            drawing,
            null,
            "DrawingMetadataUpdated",
            MetadataChangeDetails(before, after),
            Actor(http)));
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetReviewQueueAsync(
        EngineeringDbContext db,
        CancellationToken cancellationToken)
    {
        var queue = await db.DrawingRevisions.AsNoTracking()
            .Where(x => x.Status == DrawingRevisionStatus.UnderReview)
            .OrderBy(x => x.UploadedAt)
            .Select(x => new DrawingReviewQueueDto(
                x.Id,
                x.DrawingId,
                x.Drawing.DrawingNumber,
                x.Drawing.Title,
                x.Drawing.Customer,
                x.RevisionNumber,
                x.RevisionDate,
                x.UploadedAt,
                x.UploadedBy,
                x.ChangeDescription,
                x.Notes,
                x.FileSize > 0 && x.StoredFilePath != string.Empty))
            .ToListAsync(cancellationToken);
        return Results.Ok(queue);
    }

    private static async Task<IResult> ArchiveDrawingAsync(
        int id,
        DrawingArchiveDto dto,
        EngineeringDbContext db,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Reason))
            return Results.BadRequest(new ErrorDto("ReasonRequired", "An archive reason is required."));
        var drawing = await db.Drawings
            .Include(x => x.Revisions)
            .Include(x => x.AuditEntries)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (drawing is null) return Results.NotFound();
        if (drawing.IsObsolete)
            return Results.Conflict(new ErrorDto("AlreadyObsolete", "This drawing is already archived."));

        var now = DateTime.UtcNow;
        foreach (var revision in drawing.Revisions.Where(x =>
                     x.Status is DrawingRevisionStatus.Draft or DrawingRevisionStatus.UnderReview or DrawingRevisionStatus.Approved))
        {
            revision.Status = DrawingRevisionStatus.Obsolete;
            revision.SupersededOrObsoleteAt = now;
        }
        drawing.ApprovalStatus = DrawingApprovalStatus.Obsolete;
        drawing.IsObsolete = true;
        drawing.AuditEntries.Add(Audit(
            drawing,
            null,
            "DrawingArchived",
            $"Drawing archived. Reason: {dto.Reason.Trim()}",
            Actor(http)));
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static void AddPartsFromCsv(Drawing drawing, StringValues values) =>
        AddParts(drawing, values.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).AsEnumerable());

    private static void AddParts(Drawing drawing, IEnumerable<string> values)
    {
        foreach (var part in values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            drawing.Parts.Add(new DrawingPart { PartNumber = part });
    }

    private static void AddLinks(Drawing drawing, IEnumerable<DrawingDocumentLinkCreateDto> links)
    {
        foreach (var link in links)
        {
            if (!Enum.TryParse<DrawingDocumentKind>(link.Kind, true, out var kind) ||
                string.IsNullOrWhiteSpace(link.ReferenceNumber))
                continue;
            drawing.DocumentLinks.Add(new DrawingDocumentLink
            {
                Kind = kind,
                ReferenceNumber = link.ReferenceNumber.Trim(),
                Title = Clean(link.Title),
                Location = Clean(link.Location)
            });
        }
    }

    private static IReadOnlyList<DrawingDocumentLinkCreateDto> ParseLinks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<DrawingDocumentLinkCreateDto>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Snapshot(Drawing drawing) =>
        JsonSerializer.Serialize(new
        {
            drawing.Title,
            drawing.Customer,
            Parts = drawing.Parts.Select(x => x.PartNumber).OrderBy(x => x).ToArray(),
            drawing.Notes,
            drawing.PhysicalMylarLocation,
            Links = drawing.DocumentLinks
                .Select(x => $"{x.Kind}:{x.ReferenceNumber}:{x.Title}:{x.Location}")
                .OrderBy(x => x)
                .ToArray()
        });

    private static string MetadataChangeDetails(string before, string after) =>
        JsonSerializer.Serialize(new
        {
            schema = "DrawingMetadataChange/v1",
            before = JsonSerializer.Deserialize<JsonElement>(before),
            after = JsonSerializer.Deserialize<JsonElement>(after)
        });

    private static string LinkIdentity(DrawingDocumentLinkCreateDto link) =>
        $"{link.ReferenceNumber.Trim()}:{Clean(link.Title)}:{Clean(link.Location)}";

    private static bool HasPermission(HttpContext http, string permission) =>
        http.User.HasClaim(EngineeringAuthorization.PermissionClaimType, permission);

    private static DrawingAuditEntry Audit(
        Drawing drawing,
        string? revision,
        string action,
        string details,
        string actor) => new()
        {
            Drawing = drawing,
            RevisionNumber = revision,
            Action = action,
            Details = details,
            Actor = actor,
            OccurredAt = DateTime.UtcNow
        };

    private static string Actor(HttpContext http) => http.User.Identity?.Name ?? "Unknown";
    private static string Normalize(string value) =>
        string.Concat(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit));
    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string SafeFileName(string value) =>
        Path.GetFileName(value).Replace("\0", string.Empty, StringComparison.Ordinal);
    private static DateTime ParseDate(string? value, DateTime fallback) =>
        DateTime.TryParse(value, out var parsed) ? parsed : fallback;
    private static DateTime? ParseNullableDate(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : null;
}
