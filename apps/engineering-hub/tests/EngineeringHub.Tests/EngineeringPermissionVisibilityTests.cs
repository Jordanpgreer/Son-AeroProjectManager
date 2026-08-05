using System.Security.Claims;
using System.Text.Json;
using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Dtos;
using EngineeringHub.Api.Endpoints;
using EngineeringHub.Api.Models;
using EngineeringHub.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonAero.Platform.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace EngineeringHub.Tests;

public sealed class EngineeringPermissionVisibilityTests
{
    [Fact]
    public async Task BasicViewerReceivesOnlyCurrentControlledRevision()
    {
        await using var fixture = await VisibilityFixture.CreateAsync();

        var response = await fixture.GetDrawingAsync(
            EngineeringPermissions.ModuleView,
            EngineeringPermissions.DrawingsView,
            EngineeringPermissions.DrawingFilesView);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        var drawing = JsonSerializer.Deserialize<DrawingDetailDto>(response.Body, JsonOptions());
        Assert.NotNull(drawing);
        Assert.Equal("Approved", drawing.ApprovalStatus);
        Assert.Equal("A", drawing.CurrentRevision);
        Assert.Equal(["A"], drawing.Revisions.Select(revision => revision.RevisionNumber));
        Assert.Empty(drawing.RelatedDocuments);
        Assert.Empty(drawing.Validations);
        Assert.Empty(drawing.Mylars);
        Assert.Empty(drawing.MylarHistory);
        Assert.Empty(drawing.AuditHistory);
        Assert.Null(drawing.PhysicalMylarLocation);
    }

    [Fact]
    public async Task EngineeringVisibilityPermissionsExposeInternalRevisionData()
    {
        await using var fixture = await VisibilityFixture.CreateAsync();

        var response = await fixture.GetDrawingAsync(
            EngineeringPermissions.ModuleView,
            EngineeringPermissions.DrawingsView,
            EngineeringPermissions.DrawingFilesView,
            EngineeringPermissions.PendingRevisionsView,
            EngineeringPermissions.RevisionHistoryView,
            EngineeringPermissions.SpecificationsView,
            EngineeringPermissions.SupportingDocumentsView,
            EngineeringPermissions.MylarView,
            EngineeringPermissions.ValidationsView,
            EngineeringPermissions.AuditView);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        var drawing = JsonSerializer.Deserialize<DrawingDetailDto>(response.Body, JsonOptions());
        Assert.NotNull(drawing);
        Assert.Equal("UnderReview", drawing.ApprovalStatus);
        Assert.Equal(2, drawing.Revisions.Count);
        Assert.Equal(2, drawing.RelatedDocuments.Count);
        Assert.Single(drawing.Validations);
        Assert.Single(drawing.Mylars);
        Assert.Single(drawing.MylarHistory);
        Assert.Single(drawing.AuditHistory);
        Assert.Equal("Vault A", drawing.PhysicalMylarLocation);
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);

    private sealed class VisibilityFixture(
        SqliteConnection connection,
        EngineeringDbContext db,
        WebApplication app,
        string root,
        int drawingId) : IAsyncDisposable
    {
        public static async Task<VisibilityFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new EngineeringDbContext(new DbContextOptionsBuilder<EngineeringDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            var drawing = new Drawing
            {
                DrawingNumber = "VIS-100",
                NormalizedDrawingNumber = "VIS100",
                Title = "Visibility test drawing",
                Customer = "Design Authority",
                NormalizedCustomer = "DESIGNAUTHORITY",
                ApprovalStatus = DrawingApprovalStatus.UnderReview,
                EffectiveDate = new DateTime(2026, 8, 1),
                FileLocation = "rev-a/drawing.pdf",
                Notes = "Visible drawing note",
                PhysicalMylarLocation = "Vault A",
                CreatedBy = "test-user",
                CreatedAt = DateTime.UtcNow,
                ApprovedBy = "approver",
                ApprovedAt = DateTime.UtcNow
            };
            var approved = Revision("A", DrawingRevisionStatus.Approved, "rev-a/drawing.pdf");
            var pending = Revision("B", DrawingRevisionStatus.UnderReview, "rev-b/drawing.pdf");
            drawing.Revisions.AddRange([approved, pending]);
            drawing.DocumentLinks.Add(new DrawingDocumentLink
            {
                Kind = DrawingDocumentKind.Specification,
                ReferenceNumber = "SPEC-100"
            });
            drawing.Validations.Add(new DrawingValidation
            {
                ValidationType = "QA",
                Result = "Pass",
                ValidatedBy = "inspector",
                ValidatedAt = DateTime.UtcNow
            });
            var mylar = new DrawingMylar
            {
                MylarNumber = "MYLAR-100",
                NormalizedMylarNumber = "MYLAR100",
                CurrentLocation = "Vault A",
                CreatedBy = "test-user",
                CreatedAt = DateTime.UtcNow
            };
            drawing.Mylars.Add(mylar);
            drawing.MylarTransactions.Add(new MylarTransaction
            {
                DrawingMylarId = null,
                Type = MylarTransactionType.Registered,
                Person = "test-user",
                Location = "Vault A",
                RecordedBy = "test-user",
                RecordedAt = DateTime.UtcNow
            });
            drawing.AuditEntries.Add(new DrawingAuditEntry
            {
                Action = "DrawingCreated",
                Details = "Created visibility test drawing.",
                Actor = "test-user",
                OccurredAt = DateTime.UtcNow
            });
            db.Drawings.Add(drawing);
            await db.SaveChangesAsync();
            drawing.CurrentApprovedRevisionId = approved.Id;
            drawing.DocumentLinks.Add(new DrawingDocumentLink
            {
                DrawingRevisionId = pending.Id,
                Kind = DrawingDocumentKind.SupplementalDocument,
                ReferenceNumber = "SUPPORT-100",
                Title = "analysis.pdf",
                Location = "support/analysis.pdf"
            });
            await db.SaveChangesAsync();

            var root = Path.Combine(Path.GetTempPath(), "engineering-visibility-tests", Guid.NewGuid().ToString("N"));
            var files = new DrawingFileStore(Options.Create(new DrawingStorageOptions { RootPath = root }));
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton<IDrawingFileStore>(files);
            builder.Services.AddScoped<MylarCustodyService>();
            var app = builder.Build();
            app.MapGroup("/api").MapDrawingEndpoints();
            return new VisibilityFixture(connection, db, app, root, drawing.Id);
        }

        public async Task<ResponseSnapshot> GetDrawingAsync(params string[] permissions)
        {
            var endpoint = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Single(candidate => candidate.RoutePattern.RawText == "/api/drawings/{id:int}" &&
                                     candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
            await using var scope = app.Services.CreateAsyncScope();
            var claims = permissions.Select(permission =>
                new Claim(EngineeringAuthorization.PermissionClaimType, permission)).ToList();
            claims.Add(new Claim(ClaimTypes.Name, "SONAERO\\viewer"));
            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            };
            context.Request.Method = "GET";
            context.Request.RouteValues["id"] = drawingId.ToString();
            context.Response.Body = new MemoryStream();
            await endpoint.RequestDelegate!(context);
            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body);
            return new ResponseSnapshot(context.Response.StatusCode, await reader.ReadToEndAsync());
        }

        private static DrawingRevision Revision(string number, DrawingRevisionStatus status, string path) => new()
        {
            RevisionNumber = number,
            RevisionDate = DateTime.UtcNow.Date,
            UploadedAt = DateTime.UtcNow,
            ChangeDescription = $"Revision {number}",
            Status = status,
            OriginalFileName = "drawing.pdf",
            StoredFilePath = path,
            FileType = "application/pdf",
            FileSize = 10,
            FileHash = Guid.NewGuid().ToString("N"),
            UploadedBy = "test-user"
        };

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            await db.DisposeAsync();
            await connection.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed record ResponseSnapshot(int StatusCode, string Body);
}
