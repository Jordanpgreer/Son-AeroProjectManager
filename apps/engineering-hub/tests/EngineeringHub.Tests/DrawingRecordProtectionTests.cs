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

    [Fact]
    public async Task ApprovedRevisionCanOnlyTransitionToSuperseded()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing("DRW-300", "ACME");
        var revision = CreateRevision(DrawingRevisionStatus.Approved);
        drawing.Revisions.Add(revision);
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();

        revision.Status = DrawingRevisionStatus.Superseded;
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
