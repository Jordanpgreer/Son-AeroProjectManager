using EngineeringHub.Api.Data;
using EngineeringHub.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EngineeringHub.Tests;

public sealed class RevisionSupportingDocumentTests
{
    [Fact]
    public async Task InitializerBackfillsLegacySupplementalDocumentToCurrentRevision()
    {
        await using var fixture = await RevisionFixture.CreateAsync();
        var drawing = fixture.CreateDrawing("DA-100");
        var approved = fixture.CreateRevision("A", DrawingRevisionStatus.Approved);
        drawing.Revisions.Add(approved);
        drawing.DocumentLinks.Add(new DrawingDocumentLink
        {
            Kind = DrawingDocumentKind.SupplementalDocument,
            ReferenceNumber = "LOAD-CALC",
            Title = "load-calculation.pdf",
            Location = "support/load-calculation.pdf"
        });
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();

        drawing.CurrentApprovedRevisionId = approved.Id;
        drawing.ApprovalStatus = DrawingApprovalStatus.Approved;
        await fixture.Context.SaveChangesAsync();

        await new EngineeringSchemaInitializer(fixture.Context).InitializeAsync(CancellationToken.None);
        fixture.Context.ChangeTracker.Clear();

        var document = await fixture.Context.DrawingDocumentLinks.SingleAsync();
        Assert.Equal(approved.Id, document.DrawingRevisionId);
    }

    [Fact]
    public async Task RevisionConstraintRejectsDocumentOwnedByAnotherDrawing()
    {
        await using var fixture = await RevisionFixture.CreateAsync();
        await new EngineeringSchemaInitializer(fixture.Context).InitializeAsync(CancellationToken.None);
        var firstDrawing = fixture.CreateDrawing("DA-200");
        var secondDrawing = fixture.CreateDrawing("DA-201");
        var secondRevision = fixture.CreateRevision("A", DrawingRevisionStatus.Draft);
        secondDrawing.Revisions.Add(secondRevision);
        fixture.Context.Drawings.AddRange(firstDrawing, secondDrawing);
        await fixture.Context.SaveChangesAsync();

        fixture.Context.DrawingDocumentLinks.Add(new DrawingDocumentLink
        {
            DrawingId = firstDrawing.Id,
            DrawingRevisionId = secondRevision.Id,
            Kind = DrawingDocumentKind.SupplementalDocument,
            ReferenceNumber = "INVALID-LINK",
            Title = "invalid.pdf",
            Location = "support/invalid.pdf"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Context.SaveChangesAsync());
    }

    private sealed class RevisionFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public EngineeringDbContext Context { get; }

        private RevisionFixture(SqliteConnection connection, EngineeringDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public static async Task<RevisionFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<EngineeringDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new EngineeringDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new RevisionFixture(connection, context);
        }

        public Drawing CreateDrawing(string number) => new()
        {
            DrawingNumber = number,
            NormalizedDrawingNumber = number.Replace("-", string.Empty),
            Title = $"Drawing {number}",
            Customer = "Design Authority",
            NormalizedCustomer = "DESIGNAUTHORITY",
            CreatedBy = "test-user",
            CreatedAt = DateTime.UtcNow
        };

        public DrawingRevision CreateRevision(string number, DrawingRevisionStatus status) => new()
        {
            RevisionNumber = number,
            RevisionDate = DateTime.UtcNow.Date,
            UploadedAt = DateTime.UtcNow,
            ChangeDescription = "Test revision",
            Status = status,
            OriginalFileName = "drawing.pdf",
            StoredFilePath = $"revision-{number}/drawing.pdf",
            FileType = "application/pdf",
            FileSize = 1,
            FileHash = Guid.NewGuid().ToString("N"),
            UploadedBy = "test-user"
        };

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
