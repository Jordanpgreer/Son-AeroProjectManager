using EngineeringHub.Api.Data;
using EngineeringHub.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EngineeringHub.Tests;

public sealed class DrawingRecordProtectionTests
{
    [Fact]
    public async Task DrawingNumberIsUniqueWithinCustomer()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        fixture.Context.Drawings.Add(CreateDrawing("DRW-100", "ACME"));
        fixture.Context.Drawings.Add(CreateDrawing("drw 100", "acme"));

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task ApprovedRevisionCannotBeOverwrittenOrDeleted()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing("DRW-200", "ACME");
        var revision = CreateRevision(DrawingRevisionStatus.Approved);
        drawing.Revisions.Add(revision);
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();

        revision.Notes = "Attempted overwrite";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Context.SaveChangesAsync());
        fixture.Context.Entry(revision).State = EntityState.Unchanged;
        fixture.Context.DrawingRevisions.Remove(revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Context.SaveChangesAsync());
    }

    [Theory]
    [InlineData(DrawingRevisionStatus.Superseded)]
    [InlineData(DrawingRevisionStatus.Obsolete)]
    public async Task ApprovedRevisionCanOnlyTransitionToHistoricalStatus(DrawingRevisionStatus historicalStatus)
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing("DRW-300", "ACME");
        var revision = CreateRevision(DrawingRevisionStatus.Approved);
        drawing.Revisions.Add(revision);
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();

        revision.Status = historicalStatus;
        revision.SupersededOrObsoleteAt = DateTime.UtcNow;
        await fixture.Context.SaveChangesAsync();

        revision.Status = DrawingRevisionStatus.Draft;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task DraftRevisionRequiresControlledDeletionFlag()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing("DRW-400", "ACME");
        var revision = CreateRevision(DrawingRevisionStatus.Draft);
        drawing.Revisions.Add(revision);
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();

        fixture.Context.DrawingRevisions.Remove(revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Context.SaveChangesAsync());
        fixture.Context.AllowControlledDraftRevisionDeletion = true;
        await fixture.Context.SaveChangesAsync();

        Assert.Empty(await fixture.Context.DrawingRevisions.ToListAsync());
    }

    [Theory]
    [InlineData(DrawingRevisionStatus.Superseded)]
    [InlineData(DrawingRevisionStatus.Obsolete)]
    public async Task HistoricalRevisionRequiresControlledDeletionFlag(DrawingRevisionStatus status)
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing("DRW-450", "ACME");
        var revision = CreateRevision(status);
        drawing.Revisions.Add(revision);
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();

        fixture.Context.DrawingRevisions.Remove(revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Context.SaveChangesAsync());
        fixture.Context.AllowControlledHistoricalRevisionDeletion = true;
        await fixture.Context.SaveChangesAsync();

        Assert.Empty(await fixture.Context.DrawingRevisions.ToListAsync());
    }

    [Theory]
    [InlineData(DrawingRevisionStatus.Superseded)]
    [InlineData(DrawingRevisionStatus.Obsolete)]
    public async Task HistoricalRevisionRequiresControlledActivationFlag(DrawingRevisionStatus status)
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing("DRW-475", "ACME");
        var revision = CreateRevision(status);
        revision.SupersededOrObsoleteAt = DateTime.UtcNow;
        drawing.Revisions.Add(revision);
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();

        revision.Status = DrawingRevisionStatus.Approved;
        revision.SupersededOrObsoleteAt = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Context.SaveChangesAsync());
        fixture.Context.AllowControlledHistoricalRevisionActivation = true;
        await fixture.Context.SaveChangesAsync();

        Assert.Equal(DrawingRevisionStatus.Approved, revision.Status);
        Assert.Null(revision.SupersededOrObsoleteAt);
    }

    [Fact]
    public async Task UnderReviewRevisionCanBeEditedAndReturnedToDraft()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing("DRW-480", "ACME");
        var revision = CreateRevision(DrawingRevisionStatus.UnderReview);
        drawing.Revisions.Add(revision);
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();

        revision.RevisionNumber = "B";
        revision.ChangeDescription = "Updated before resubmission";
        revision.Status = DrawingRevisionStatus.Draft;
        await fixture.Context.SaveChangesAsync();

        Assert.Equal("B", revision.RevisionNumber);
        Assert.Equal("Updated before resubmission", revision.ChangeDescription);
        Assert.Equal(DrawingRevisionStatus.Draft, revision.Status);
    }

    [Fact]
    public async Task ApprovedRevisionCanOnlyReopenInPlaceThroughTheControlledWorkflow()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing("DRW-490", "ACME");
        var revision = CreateRevision(DrawingRevisionStatus.Approved);
        revision.ApprovedBy = "approver";
        revision.ApprovalDate = DateTime.UtcNow.AddDays(-2);
        drawing.Revisions.Add(revision);
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();
        var originalId = revision.Id;

        revision.RevisionNumber = "B";
        revision.ChangeDescription = "Corrected controlled issue";
        revision.Status = DrawingRevisionStatus.Draft;
        revision.ApprovedBy = null;
        revision.ApprovalDate = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Context.SaveChangesAsync());

        fixture.Context.AllowControlledRevisionReopen = true;
        try
        {
            await fixture.Context.SaveChangesAsync();
        }
        finally
        {
            fixture.Context.AllowControlledRevisionReopen = false;
        }

        var saved = await fixture.Context.DrawingRevisions.SingleAsync();
        Assert.Equal(originalId, saved.Id);
        Assert.Equal("B", saved.RevisionNumber);
        Assert.Equal("Corrected controlled issue", saved.ChangeDescription);
        Assert.Equal(DrawingRevisionStatus.Draft, saved.Status);
        Assert.Null(saved.ApprovedBy);
        Assert.Null(saved.ApprovalDate);
    }

    [Fact]
    public async Task DraftDrawingRequiresControlledDeletionFlag()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing("DRW-500", "ACME");
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();

        fixture.Context.Drawings.Remove(drawing);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Context.SaveChangesAsync());
        fixture.Context.AllowControlledEmptyDraftDrawingDeletion = true;
        await fixture.Context.SaveChangesAsync();

        Assert.Empty(await fixture.Context.Drawings.ToListAsync());
    }

    private static Drawing CreateDrawing(string number, string customer) => new()
    {
        DrawingNumber = number,
        NormalizedDrawingNumber = string.Concat(number.ToUpperInvariant().Where(char.IsLetterOrDigit)),
        Title = "Test drawing",
        Customer = customer,
        NormalizedCustomer = string.Concat(customer.ToUpperInvariant().Where(char.IsLetterOrDigit)),
        CreatedBy = "test-user",
        CreatedAt = DateTime.UtcNow
    };

    private static DrawingRevision CreateRevision(DrawingRevisionStatus status) => new()
    {
        RevisionNumber = "A", RevisionDate = DateTime.UtcNow.Date, UploadedAt = DateTime.UtcNow,
        ChangeDescription = "Initial issue", Status = status, OriginalFileName = "drawing.pdf",
        StoredFilePath = "1/a/drawing.pdf", FileType = "application/pdf", FileSize = 100,
        FileHash = new string('A', 64), UploadedBy = "test-user"
    };

    private sealed class ContextFixture(SqliteConnection connection, EngineeringDbContext context) : IAsyncDisposable
    {
        public EngineeringDbContext Context { get; } = context;
        public static async Task<ContextFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new EngineeringDbContext(new DbContextOptionsBuilder<EngineeringDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new ContextFixture(connection, context);
        }
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
