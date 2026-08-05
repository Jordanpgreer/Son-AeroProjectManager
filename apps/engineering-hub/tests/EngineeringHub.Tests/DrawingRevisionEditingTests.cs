using System.Security.Claims;
using System.Text;
using System.Text.Json;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Dtos;
using EngineeringHub.Api.Endpoints;
using EngineeringHub.Api.Models;
using EngineeringHub.Api.Services;
using EngineeringHub.Api.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace EngineeringHub.Tests;

public sealed class DrawingRevisionEditingTests
{
    [Theory]
    [InlineData(DrawingRevisionStatus.Approved)]
    [InlineData(DrawingRevisionStatus.Superseded)]
    [InlineData(DrawingRevisionStatus.Obsolete)]
    public async Task ControlledRevisionEditKeepsTheSameIdAndNeverCreatesAnotherRevision(
        DrawingRevisionStatus initialStatus)
    {
        await using var fixture = await RevisionFixture.CreateAsync(initialStatus, hasPdf: true);
        var originalId = fixture.Revision.Id;
        var originalPath = fixture.Revision.StoredFilePath;
        var originalHash = fixture.Revision.FileHash;

        var response = await fixture.InvokeFormAsync(
            "/api/drawing-revisions/{id:int}/editable-draft",
            fixture.Revision.Id,
            EditFields("B", "Updated controlled revision"));

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<RevisionEditResultDto>(
            response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(result);
        Assert.Equal(originalId, result.RevisionId);
        Assert.False(result.Created);
        Assert.True(result.HasPdf);

        var revision = await fixture.Db.DrawingRevisions.SingleAsync();
        Assert.Equal(originalId, revision.Id);
        Assert.Equal("B", revision.RevisionNumber);
        Assert.Equal("Updated controlled revision", revision.ChangeDescription);
        Assert.Equal(DrawingRevisionStatus.Draft, revision.Status);
        Assert.Null(revision.ApprovedBy);
        Assert.Null(revision.ApprovalDate);
        Assert.Null(revision.ApprovalComments);
        Assert.Null(revision.SupersededOrObsoleteAt);
        Assert.Equal(originalPath, revision.StoredFilePath);
        Assert.Equal(originalHash, revision.FileHash);
        Assert.Equal(1, await fixture.Db.DrawingRevisions.CountAsync());

        var audit = await fixture.Db.DrawingAuditEntries.SingleAsync();
        Assert.Equal("RevisionReopened", audit.Action);
        Assert.Contains($"existing revision ID {originalId}", audit.Details, StringComparison.Ordinal);
        Assert.Contains($"\"status\":\"{initialStatus}\"", audit.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"approvedBy\":\"approval-user\"", audit.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReopenedApprovedRevisionCanBeReviewedAndApprovedUsingTheSameId()
    {
        await using var fixture = await RevisionFixture.CreateAsync(
            DrawingRevisionStatus.Approved,
            hasPdf: true);
        var revisionId = fixture.Revision.Id;

        var edit = await fixture.InvokeFormAsync(
            "/api/drawing-revisions/{id:int}/editable-draft",
            revisionId,
            EditFields("A", "Corrected before reapproval"));
        Assert.Equal(StatusCodes.Status200OK, edit.StatusCode);

        var drawingAfterEdit = await fixture.Db.Drawings.SingleAsync();
        Assert.Null(drawingAfterEdit.CurrentApprovedRevisionId);
        Assert.Equal(DrawingApprovalStatus.Draft, drawingAfterEdit.ApprovalStatus);
        Assert.Null(drawingAfterEdit.FileLocation);
        Assert.Null(drawingAfterEdit.ApprovedBy);
        Assert.Null(drawingAfterEdit.ApprovedAt);

        var review = await fixture.InvokeJsonAsync(
            "PUT",
            "/api/drawing-revisions/{id:int}/status",
            revisionId,
            new RevisionStatusUpdateDto("UnderReview", "Ready for controlled review"));
        Assert.True(
            review.StatusCode == StatusCodes.Status204NoContent,
            $"Expected review submission to succeed, but received {review.StatusCode}: {review.Body}");
        Assert.Equal(
            DrawingRevisionStatus.UnderReview,
            (await fixture.Db.DrawingRevisions.SingleAsync()).Status);

        var approval = await fixture.InvokeJsonAsync(
            "POST",
            "/api/drawing-revisions/{id:int}/approve",
            revisionId,
            new RevisionApprovalDto(new DateTime(2026, 8, 1), "Reapproved after correction"));
        Assert.Equal(StatusCodes.Status204NoContent, approval.StatusCode);

        var revision = await fixture.Db.DrawingRevisions.SingleAsync();
        var drawing = await fixture.Db.Drawings.SingleAsync();
        Assert.Equal(revisionId, revision.Id);
        Assert.Equal(1, await fixture.Db.DrawingRevisions.CountAsync());
        Assert.Equal(DrawingRevisionStatus.Approved, revision.Status);
        Assert.Equal(revisionId, drawing.CurrentApprovedRevisionId);
        Assert.Equal(DrawingApprovalStatus.Approved, drawing.ApprovalStatus);
        Assert.Equal(
            ["RevisionReopened", "RevisionStatusChanged", "RevisionApproved"],
            await fixture.Db.DrawingAuditEntries
                .OrderBy(entry => entry.Id)
                .Select(entry => entry.Action)
                .ToArrayAsync());
    }

    [Fact]
    public async Task MetadataOnlyRevisionCanBeSavedAsDraftButCannotEnterReview()
    {
        await using var fixture = await RevisionFixture.CreateAsync(
            DrawingRevisionStatus.Draft,
            hasPdf: false);
        var revisionId = fixture.Revision.Id;

        var edit = await fixture.InvokeFormAsync(
            "/api/drawing-revisions/{id:int}/editable-draft",
            revisionId,
            EditFields("A", "Metadata correction without PDF"));

        Assert.Equal(StatusCodes.Status200OK, edit.StatusCode);
        var result = JsonSerializer.Deserialize<RevisionEditResultDto>(
            edit.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(result);
        Assert.Equal(revisionId, result.RevisionId);
        Assert.False(result.Created);
        Assert.False(result.HasPdf);
        Assert.Equal(1, await fixture.Db.DrawingRevisions.CountAsync());

        var review = await fixture.InvokeJsonAsync(
            "PUT",
            "/api/drawing-revisions/{id:int}/status",
            revisionId,
            new RevisionStatusUpdateDto("UnderReview"));

        Assert.True(
            review.StatusCode == StatusCodes.Status409Conflict,
            $"Expected PDF gate conflict, but received {review.StatusCode}: {review.Body}");
        Assert.Equal(
            DrawingRevisionStatus.Draft,
            (await fixture.Db.DrawingRevisions.SingleAsync()).Status);
        Assert.Single(await fixture.Db.DrawingAuditEntries.ToListAsync());
    }

    [Fact]
    public async Task FailedIntegrityCheckLeavesRevisionApprovalAndAuditUntouched()
    {
        await using var fixture = await RevisionFixture.CreateAsync(
            DrawingRevisionStatus.Approved,
            hasPdf: true);
        var revisionId = fixture.Revision.Id;
        var originalDescription = fixture.Revision.ChangeDescription;
        await File.WriteAllTextAsync(
            fixture.Files.ResolvePath(fixture.Revision.StoredFilePath),
            "tampered");

        var response = await fixture.InvokeFormAsync(
            "/api/drawing-revisions/{id:int}/editable-draft",
            revisionId,
            EditFields("A", "This must not be saved"));

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        var revision = await fixture.Db.DrawingRevisions.SingleAsync();
        var drawing = await fixture.Db.Drawings.SingleAsync();
        Assert.Equal(revisionId, revision.Id);
        Assert.Equal(originalDescription, revision.ChangeDescription);
        Assert.Equal(DrawingRevisionStatus.Approved, revision.Status);
        Assert.Equal("approval-user", revision.ApprovedBy);
        Assert.Equal(revisionId, drawing.CurrentApprovedRevisionId);
        Assert.Empty(await fixture.Db.DrawingAuditEntries.ToListAsync());
    }

    [Fact]
    public async Task FailedDatabaseSaveRollsBackRevisionAndAuditChanges()
    {
        await using var fixture = await RevisionFixture.CreateAsync(
            DrawingRevisionStatus.Approved,
            hasPdf: true);
        var revisionId = fixture.Revision.Id;
        var originalDescription = fixture.Revision.ChangeDescription;
        await fixture.Db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER "TR_Test_RevisionEditAuditFailure"
            BEFORE INSERT ON "DrawingAuditEntries"
            BEGIN
                SELECT RAISE(ABORT, 'simulated audit storage failure');
            END;
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.InvokeFormAsync(
            "/api/drawing-revisions/{id:int}/editable-draft",
            revisionId,
            EditFields("A", "This transaction must roll back")));

        fixture.Db.ChangeTracker.Clear();
        var revision = await fixture.Db.DrawingRevisions.SingleAsync();
        var drawing = await fixture.Db.Drawings.SingleAsync();
        Assert.Equal(revisionId, revision.Id);
        Assert.Equal(originalDescription, revision.ChangeDescription);
        Assert.Equal(DrawingRevisionStatus.Approved, revision.Status);
        Assert.Equal("approval-user", revision.ApprovedBy);
        Assert.Equal(revisionId, drawing.CurrentApprovedRevisionId);
        Assert.Empty(await fixture.Db.DrawingAuditEntries.ToListAsync());
    }

    [Fact]
    public async Task DeletingRevisionRemovesOnlyItsSupportingDocumentAssociation()
    {
        await using var fixture = await RevisionFixture.CreateAsync(
            DrawingRevisionStatus.Draft,
            hasPdf: true);
        var retainedRevision = new DrawingRevision
        {
            RevisionNumber = "B",
            RevisionDate = new DateTime(2026, 8, 1),
            UploadedAt = new DateTime(2026, 8, 1),
            ChangeDescription = "Retained draft revision",
            Status = DrawingRevisionStatus.Draft,
            OriginalFileName = string.Empty,
            StoredFilePath = string.Empty,
            FileType = string.Empty,
            FileSize = 0,
            FileHash = string.Empty,
            UploadedBy = "upload-user"
        };
        fixture.Drawing.Revisions.Add(retainedRevision);
        await fixture.Db.SaveChangesAsync();

        const string sharedLocation = "support/shared-document.pdf";
        var sharedPath = fixture.Files.ResolvePath(sharedLocation);
        Directory.CreateDirectory(Path.GetDirectoryName(sharedPath)!);
        await File.WriteAllTextAsync(sharedPath, "%PDF-1.4\nshared supporting document");
        fixture.Drawing.DocumentLinks.AddRange([
            new DrawingDocumentLink
            {
                DrawingRevisionId = fixture.Revision.Id,
                Kind = DrawingDocumentKind.SupplementalDocument,
                ReferenceNumber = "CALC",
                Title = "calculation.pdf",
                Location = sharedLocation
            },
            new DrawingDocumentLink
            {
                DrawingRevisionId = retainedRevision.Id,
                Kind = DrawingDocumentKind.SupplementalDocument,
                ReferenceNumber = "CALC",
                Title = "calculation.pdf",
                Location = sharedLocation
            }]);
        await fixture.Db.SaveChangesAsync();

        var response = await fixture.InvokeJsonAsync(
            "DELETE",
            "/api/drawing-revisions/{id:int}",
            fixture.Revision.Id,
            new RevisionDeleteDto(true));

        Assert.Equal(StatusCodes.Status204NoContent, response.StatusCode);
        Assert.Equal([retainedRevision.Id], await fixture.Db.DrawingRevisions.Select(item => item.Id).ToArrayAsync());
        var retainedDocument = await fixture.Db.DrawingDocumentLinks.SingleAsync();
        Assert.Equal(retainedRevision.Id, retainedDocument.DrawingRevisionId);
        Assert.True(File.Exists(sharedPath));
    }

    private static Dictionary<string, StringValues> EditFields(
        string revisionNumber,
        string changeDescription) => new()
    {
        ["revisionNumber"] = revisionNumber,
        ["revisionDate"] = "2026-07-30",
        ["effectiveDate"] = "2026-08-01",
        ["changeDescription"] = changeDescription,
        ["notes"] = "Edited through the existing revision workflow."
    };

    private sealed class RevisionFixture(
        SqliteConnection connection,
        EngineeringDbContext db,
        DrawingFileStore files,
        WebApplication app,
        string root,
        Drawing drawing,
        DrawingRevision revision) : IAsyncDisposable
    {
        public EngineeringDbContext Db { get; } = db;
        public DrawingFileStore Files { get; } = files;
        public Drawing Drawing { get; } = drawing;
        public DrawingRevision Revision { get; } = revision;

        public static async Task<RevisionFixture> CreateAsync(
            DrawingRevisionStatus status,
            bool hasPdf)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "engineering-revision-edit-tests",
                Guid.NewGuid().ToString("N"));
            var files = new DrawingFileStore(Options.Create(new DrawingStorageOptions
            {
                RootPath = root,
                RequireUncPath = false
            }));
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new EngineeringDbContext(
                new DbContextOptionsBuilder<EngineeringDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var drawing = new Drawing
            {
                DrawingNumber = "DRW-EDIT-1",
                NormalizedDrawingNumber = "DRWEDIT1",
                Title = "Editable drawing revision",
                Customer = "SON-AERO",
                NormalizedCustomer = "SONAERO",
                CreatedBy = "test-user",
                CreatedAt = DateTime.UtcNow
            };
            db.Drawings.Add(drawing);
            await db.SaveChangesAsync();

            StoredRevisionFiles? stored = null;
            byte[]? pdfBytes = null;
            if (hasPdf)
            {
                pdfBytes = Encoding.UTF8.GetBytes("%PDF-1.4\ncontrolled revision");
                await using var stream = new MemoryStream(pdfBytes);
                var pdf = new FormFile(stream, 0, stream.Length, "pdf", "drawing.pdf")
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "application/pdf"
                };
                stored = await files.StoreRevisionAsync(
                    drawing.Id,
                    drawing.Customer,
                    drawing.DrawingNumber,
                    "A",
                    pdf,
                    null,
                    CancellationToken.None);
            }

            var revision = new DrawingRevision
            {
                RevisionNumber = "A",
                RevisionDate = new DateTime(2026, 7, 1),
                UploadedAt = new DateTime(2026, 7, 1),
                EffectiveDate = new DateTime(2026, 7, 15),
                ChangeDescription = "Initial controlled issue",
                Status = status,
                OriginalFileName = hasPdf ? "drawing.pdf" : string.Empty,
                StoredFilePath = stored?.PdfRelativePath ?? string.Empty,
                FileType = hasPdf ? "application/pdf" : string.Empty,
                FileSize = pdfBytes?.Length ?? 0,
                FileHash = stored?.PdfHash ?? string.Empty,
                UploadedBy = "upload-user",
                ApprovedBy = "approval-user",
                ApprovalDate = new DateTime(2026, 7, 2),
                ApprovalComments = "Original approval evidence",
                SupersededOrObsoleteAt = status is DrawingRevisionStatus.Superseded or DrawingRevisionStatus.Obsolete
                    ? new DateTime(2026, 7, 20)
                    : null
            };
            drawing.Revisions.Add(revision);
            await db.SaveChangesAsync();

            if (status == DrawingRevisionStatus.Approved)
            {
                drawing.CurrentApprovedRevisionId = revision.Id;
                drawing.ApprovalStatus = DrawingApprovalStatus.Approved;
                drawing.EffectiveDate = revision.EffectiveDate;
                drawing.FileLocation = revision.StoredFilePath;
                drawing.ApprovedBy = revision.ApprovedBy;
                drawing.ApprovedAt = revision.ApprovalDate;
                await db.SaveChangesAsync();
            }

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton<IDrawingFileStore>(files);
            builder.Services.AddScoped<MylarCustodyService>();
            var app = builder.Build();
            app.MapGroup("/api").MapDrawingEndpoints();

            return new RevisionFixture(connection, db, files, app, root, drawing, revision);
        }

        public Task<HttpResponseSnapshot> InvokeFormAsync(
            string route,
            int id,
            Dictionary<string, StringValues> fields) =>
            InvokeAsync("POST", route, id, context =>
            {
                context.Request.ContentType = "multipart/form-data; boundary=test-boundary";
                context.Features.Set<IFormFeature>(
                    new StaticFormFeature(new FormCollection(fields, new FormFileCollection())));
            });

        public Task<HttpResponseSnapshot> InvokeJsonAsync<T>(
            string method,
            string route,
            int id,
            T body) =>
            InvokeAsync(method, route, id, context =>
            {
                var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                    body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = content.Length;
                context.Request.Body = new MemoryStream(content);
                context.Features.Set<IHttpRequestBodyDetectionFeature>(
                    new StaticRequestBodyDetectionFeature());
            });

        private async Task<HttpResponseSnapshot> InvokeAsync(
            string method,
            string route,
            int id,
            Action<DefaultHttpContext> configure)
        {
            var endpoint = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Single(candidate =>
                    candidate.RoutePattern.RawText == route &&
                    candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
            await using var scope = app.Services.CreateAsyncScope();
            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, @"SONAERO\revision.editor"),
                        .. EngineeringPermissions.All.Select(permission =>
                            new Claim(EngineeringAuthorization.PermissionClaimType, permission.Key))
                    ],
                    "Test"))
            };
            context.Request.Method = method;
            context.Request.RouteValues["id"] = id.ToString();
            context.Response.Body = new MemoryStream();
            configure(context);

            await endpoint.RequestDelegate!(context);

            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body);
            return new HttpResponseSnapshot(
                context.Response.StatusCode,
                await reader.ReadToEndAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            await Db.DisposeAsync();
            await connection.DisposeAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticFormFeature(IFormCollection form) : IFormFeature
    {
        public bool HasFormContentType => true;
        public IFormCollection? Form { get; set; } = form;
        public IFormCollection ReadForm() => Form!;
        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Form!);
    }

    private sealed class StaticRequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed record HttpResponseSnapshot(int StatusCode, string Body);
}
