using System.Text.Json;
using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Dtos;
using EngineeringHub.Api.Models;
using EngineeringHub.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

namespace EngineeringHub.Api.Endpoints;

public static class DrawingOperationalEndpoints
{
    private const long MaxFileBytes = 100 * 1024 * 1024;

    public static void MapDrawingOperationalEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/drawings/create-with-revision", CreateWithRevisionAsync)
            .DisableAntiforgery()
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
        api.MapPut("/drawings/{id:int}", UpdateDrawingAsync)
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
        api.MapGet("/drawing-review-queue", GetReviewQueueAsync);
        api.MapPost("/drawings/{id:int}/archive", ArchiveDrawingAsync)
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
        api.MapPost("/drawings/{id:int}/obsolete", ArchiveDrawingAsync)
            .RequireAuthorization(EngineeringAuthorization.WritePolicy);
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
            return Results.BadRequest(new ErrorDto("RequiredFields", "Drawing number, title, and customer are required."));

        var normalizedNumber = Normalize(drawingNumber);
        var normalizedCustomer = Normalize(customer);
        if (await db.Drawings.AnyAsync(
                x => x.NormalizedDrawingNumber == normalizedNumber && x.NormalizedCustomer == normalizedCustomer,
                cancellationToken))
            return Results.Conflict(new ErrorDto("DuplicateDrawing", "That drawing number already exists for this customer."));

        var pdf = form.Files.GetFile("pdf");
        var source = form.Files.GetFile("source");
        var hasPdf = pdf is { Length: > 0 };
        var revisionNumber = form["revisionNumber"].ToString().Trim();
        var changeDescription = form["changeDescription"].ToString().Trim();
        if (hasPdf && (string.IsNullOrWhiteSpace(revisionNumber) || string.IsNullOrWhiteSpace(changeDescription)))
            return Results.BadRequest(new ErrorDto("RevisionRequired", "Revision number and change description are required when an initial PDF is attached."));
        if (hasPdf && !await IsValidPdfAsync(pdf!, cancellationToken))
            return Results.BadRequest(new ErrorDto("InvalidPdf", "The initial drawing file must be a valid PDF."));
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
            null,
            "DrawingCreated",
            hasPdf ? "Created drawing record with an initial revision package." : "Created metadata-only draft drawing.",
            actor));

        StoredRevisionFiles? stored = null;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            db.Drawings.Add(drawing);
            await db.SaveChangesAsync(cancellationToken);
            if (hasPdf)
            {
                stored = await files.StoreRevisionAsync(
                    drawing.Id,
                    drawing.Customer,
                    drawing.DrawingNumber,
                    revisionNumber,
                    pdf!,
                    source,
                    cancellationToken);
                var revision = new DrawingRevision
                {
                    RevisionNumber = revisionNumber,
                    RevisionDate = ParseDate(form["revisionDate"], DateTime.UtcNow.Date),
                    UploadedAt = DateTime.UtcNow,
                    EffectiveDate = ParseNullableDate(form["effectiveDate"]),
                    ChangeDescription = changeDescription,
                    Status = DrawingRevisionStatus.Draft,
                    OriginalFileName = SafeFileName(pdf!.FileName),
                    StoredFilePath = stored.PdfRelativePath,
                    FileType = "application/pdf",
                    FileSize = pdf.Length,
                    FileHash = stored.PdfHash,
                    SourceOriginalFileName = source is { Length: > 0 } ? SafeFileName(source.FileName) : null,
                    SourceStoredFilePath = stored.SourceRelativePath,
                    UploadedBy = actor,
                    Notes = Clean(form["revisionNotes"])
                };
                drawing.Revisions.Add(revision);
                drawing.AuditEntries.Add(Audit(
                    drawing,
                    revisionNumber,
                    "InitialRevisionUploaded",
                    $"Stored initial PDF {revision.OriginalFileName}; SHA-256 {stored.PdfHash}.",
                    actor));
                await db.SaveChangesAsync(cancellationToken);
            }

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
            return Results.BadRequest(new ErrorDto("RequiredFields", "Title and customer are required."));

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
            return Results.Conflict(new ErrorDto("DuplicateDrawing", "That drawing number already exists for the selected customer."));

        var before = Snapshot(drawing);
        drawing.Title = dto.Title.Trim();
        drawing.Customer = newCustomer;
        drawing.NormalizedCustomer = normalizedCustomer;
        drawing.Notes = Clean(dto.Notes);
        drawing.PhysicalMylarLocation = Clean(dto.PhysicalMylarLocation);
        drawing.Parts.Clear();
        AddParts(drawing, dto.PartNumbers ?? []);
        drawing.DocumentLinks.Clear();
        AddLinks(drawing, dto.RelatedDocuments ?? []);
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

    private static async Task<bool> IsValidPdfAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            return false;
        await using var stream = file.OpenReadStream();
        var signature = new byte[5];
        var read = await stream.ReadAsync(signature, cancellationToken);
        return read == signature.Length && signature.SequenceEqual("%PDF-"u8.ToArray());
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
