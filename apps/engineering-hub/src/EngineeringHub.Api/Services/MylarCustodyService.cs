using System.Data;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Services;

public sealed record MylarCustodyResult(
    DrawingMylar? Mylar,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int StatusCode = StatusCodes.Status200OK)
{
    public bool Succeeded => ErrorCode is null;

    public static MylarCustodyResult Fail(string code, string message, int statusCode) =>
        new(null, code, message, statusCode);
}

public sealed class MylarCustodyService(
    EngineeringDbContext db,
    ILogger<MylarCustodyService>? logger = null)
{
    private const string MylarNumberIndex = "IX_DrawingMylars_DrawingId_NormalizedMylarNumber";

    public async Task<MylarCustodyResult> RegisterAsync(
        int drawingId,
        string? mylarNumber,
        string? location,
        string? note,
        string actor,
        CancellationToken cancellationToken)
    {
        var number = mylarNumber?.Trim();
        var currentLocation = location?.Trim();
        if (string.IsNullOrWhiteSpace(number))
            return MylarCustodyResult.Fail("MylarNumberRequired", "A Mylar number is required.", StatusCodes.Status400BadRequest);
        if (string.IsNullOrWhiteSpace(currentLocation))
            return MylarCustodyResult.Fail("LocationRequired", "An initial storage location is required.", StatusCodes.Status400BadRequest);
        if (string.IsNullOrWhiteSpace(actor))
            return MylarCustodyResult.Fail("AuthenticatedUserRequired", "A signed-in Windows account is required.", StatusCodes.Status401Unauthorized);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var drawing = await db.Drawings
            .Include(x => x.Mylars)
            .Include(x => x.AuditEntries)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == drawingId, cancellationToken);
        if (drawing is null)
            return MylarCustodyResult.Fail("DrawingNotFound", "The drawing record was not found.", StatusCodes.Status404NotFound);
        if (drawing.IsObsolete)
            return MylarCustodyResult.Fail("ArchivedDrawing", "New Mylars cannot be registered for an archived drawing.", StatusCodes.Status409Conflict);

        var normalizedNumber = Normalize(number);
        if (normalizedNumber.Length == 0)
            return MylarCustodyResult.Fail("InvalidMylarNumber", "The Mylar number must contain at least one letter or number.", StatusCodes.Status400BadRequest);
        if (drawing.Mylars.Any(x => x.NormalizedMylarNumber == normalizedNumber))
            return MylarCustodyResult.Fail(
                "DuplicateMylarNumber",
                $"Mylar {number} is already registered for this drawing.",
                StatusCodes.Status409Conflict);

        var occurredAt = DateTime.UtcNow;
        var recordedActor = RecordedActor(actor);
        var mylar = new DrawingMylar
        {
            Drawing = drawing,
            MylarNumber = number,
            NormalizedMylarNumber = normalizedNumber,
            CurrentLocation = currentLocation,
            CreatedBy = recordedActor,
            CreatedAt = occurredAt
        };
        drawing.Mylars.Add(mylar);
        drawing.MylarTransactions.Add(new MylarTransaction
        {
            Drawing = drawing,
            Mylar = mylar,
            Type = MylarTransactionType.Registered,
            Person = recordedActor,
            Purpose = Clean(note),
            Location = currentLocation,
            RecordedBy = recordedActor,
            RecordedAt = occurredAt
        });
        drawing.AuditEntries.Add(Audit(
            drawing,
            "MylarRegistered",
            $"Registered Mylar {number} in {currentLocation}{NoteSuffix(note)}",
            recordedActor));
        SyncLegacyDrawingSummary(drawing);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new MylarCustodyResult(mylar, StatusCode: StatusCodes.Status201Created);
        }
        catch (DbUpdateException exception) when (IsDuplicateMylarNumber(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return MylarCustodyResult.Fail(
                "DuplicateMylarNumber",
                $"Mylar {number} is already registered for this drawing.",
                StatusCodes.Status409Conflict);
        }
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger?.LogError(
                exception,
                "Database rejected registration of Mylar {MylarNumber} for drawing {DrawingId}.",
                number,
                drawingId);
            return MylarCustodyResult.Fail(
                "MylarRegistrationFailed",
                "The Mylar record could not be saved. Refresh and try again. If the problem continues, contact an administrator.",
                StatusCodes.Status500InternalServerError);
        }
    }

    public async Task<MylarCustodyResult> RecordMovementAsync(
        int drawingId,
        int mylarId,
        bool checkingOut,
        string? location,
        string? note,
        string actor,
        CancellationToken cancellationToken)
    {
        var currentLocation = location?.Trim();
        if (string.IsNullOrWhiteSpace(currentLocation))
            return MylarCustodyResult.Fail("LocationRequired", "A location is required for every custody record.", StatusCodes.Status400BadRequest);
        if (string.IsNullOrWhiteSpace(actor))
            return MylarCustodyResult.Fail("AuthenticatedUserRequired", "A signed-in Windows account is required.", StatusCodes.Status401Unauthorized);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var drawing = await db.Drawings
            .Include(x => x.Mylars)
            .Include(x => x.AuditEntries)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == drawingId, cancellationToken);
        if (drawing is null)
            return MylarCustodyResult.Fail("DrawingNotFound", "The drawing record was not found.", StatusCodes.Status404NotFound);

        var mylar = drawing.Mylars.SingleOrDefault(x => x.Id == mylarId);
        if (mylar is null)
            return MylarCustodyResult.Fail("MylarNotFound", "That numbered Mylar does not belong to this drawing.", StatusCodes.Status404NotFound);
        if (drawing.IsObsolete && checkingOut)
            return MylarCustodyResult.Fail("ArchivedDrawing", "An archived drawing's Mylar cannot be checked out.", StatusCodes.Status409Conflict);
        if (checkingOut && mylar.IsCheckedOut)
            return MylarCustodyResult.Fail(
                "MylarAlreadyCheckedOut",
                $"Mylar {mylar.MylarNumber} is already checked out to {mylar.CheckedOutBy ?? "another user"} at {mylar.CurrentLocation ?? "an unrecorded location"}.",
                StatusCodes.Status409Conflict);
        if (!checkingOut && !mylar.IsCheckedOut)
            return MylarCustodyResult.Fail(
                "MylarAlreadyCheckedIn",
                $"Mylar {mylar.MylarNumber} is already checked in at {mylar.CurrentLocation ?? "an unrecorded location"}. No duplicate check-in was recorded.",
                StatusCodes.Status409Conflict);

        var occurredAt = DateTime.UtcNow;
        var recordedActor = RecordedActor(actor);
        mylar.IsCheckedOut = checkingOut;
        mylar.CurrentLocation = currentLocation;
        mylar.CheckedOutBy = checkingOut ? recordedActor : null;
        mylar.CheckedOutAt = checkingOut ? occurredAt : null;
        mylar.Version++;
        drawing.MylarTransactions.Add(new MylarTransaction
        {
            Drawing = drawing,
            Mylar = mylar,
            Type = checkingOut ? MylarTransactionType.CheckedOut : MylarTransactionType.Returned,
            Person = recordedActor,
            Purpose = Clean(note),
            Location = currentLocation,
            RecordedBy = recordedActor,
            RecordedAt = occurredAt
        });

        var action = checkingOut ? "MylarCheckedOut" : "MylarCheckedIn";
        var details = checkingOut
            ? $"Mylar {mylar.MylarNumber} checked out by {recordedActor}; destination: {currentLocation}{NoteSuffix(note)}"
            : $"Mylar {mylar.MylarNumber} checked in by {recordedActor}; storage location: {currentLocation}{NoteSuffix(note)}";
        drawing.AuditEntries.Add(Audit(drawing, action, details, recordedActor));
        SyncLegacyDrawingSummary(drawing);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new MylarCustodyResult(mylar);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return MylarCustodyResult.Fail(
                "MylarCustodyChanged",
                $"Mylar {mylar.MylarNumber} changed while this action was being recorded. Refresh the drawing and try again.",
                StatusCodes.Status409Conflict);
        }
    }

    private static void SyncLegacyDrawingSummary(Drawing drawing)
    {
        var checkedOut = drawing.Mylars.Where(x => x.IsCheckedOut).OrderBy(x => x.MylarNumber).ToList();
        drawing.IsMylarCheckedOut = checkedOut.Count > 0;
        drawing.MylarCheckedOutBy = checkedOut.Count == 1 ? checkedOut[0].CheckedOutBy : checkedOut.Count > 1 ? $"{checkedOut.Count} users" : null;
        drawing.MylarCheckedOutAt = checkedOut.Count > 0 ? checkedOut.Max(x => x.CheckedOutAt) : null;
        drawing.PhysicalMylarLocation = drawing.Mylars.Count == 1 ? drawing.Mylars[0].CurrentLocation : null;
    }

    private static DrawingAuditEntry Audit(Drawing drawing, string action, string details, string actor) => new()
    {
        Drawing = drawing,
        Action = action,
        Details = details,
        Actor = actor,
        OccurredAt = DateTime.UtcNow
    };

    private static string RecordedActor(string actor) => actor.Trim();

    private static string Normalize(string value) =>
        string.Concat(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit));

    private static bool IsDuplicateMylarNumber(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite &&
                sqlite.SqliteErrorCode == 19 &&
                sqlite.Message.Contains("DrawingMylars.DrawingId", StringComparison.OrdinalIgnoreCase) &&
                sqlite.Message.Contains("DrawingMylars.NormalizedMylarNumber", StringComparison.OrdinalIgnoreCase))
                return true;

            if (current is SqlException sqlServer &&
                sqlServer.Number is 2601 or 2627 &&
                sqlServer.Message.Contains(MylarNumberIndex, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NoteSuffix(string? note) =>
        string.IsNullOrWhiteSpace(note) ? "." : $"; note: {note.Trim()}.";
}
