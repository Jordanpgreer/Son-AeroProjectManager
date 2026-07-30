using EngineeringHub.Api.Data;
using EngineeringHub.Api.Models;
using EngineeringHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EngineeringHub.Tests;

public sealed class MylarCustodyTests
{
    [Fact]
    public async Task RegistersOneMylarPerDrawingAndRetainsTheSignedInActorAndNote()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing();
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();
        var service = new MylarCustodyService(fixture.Context);

        var registered = await service.RegisterAsync(
            drawing.Id,
            "MY-001",
            "Cabinet A / Slot 4",
            "Primary shop Mylar",
            @"SONAERO\alex",
            CancellationToken.None);
        var second = await service.RegisterAsync(
            drawing.Id,
            "MY-002",
            "Cabinet B / Slot 1",
            null,
            @"SONAERO\alex",
            CancellationToken.None);

        Assert.True(registered.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, second.StatusCode);
        Assert.Equal("MylarAlreadyRegistered", second.ErrorCode);
        Assert.Single(await fixture.Context.DrawingMylars.ToListAsync());
        var transaction = await fixture.Context.MylarTransactions.SingleAsync();
        Assert.Equal(MylarTransactionType.Registered, transaction.Type);
        Assert.Equal(@"SONAERO\alex", transaction.Person);
        Assert.Equal(@"SONAERO\alex", transaction.RecordedBy);
        Assert.Equal("Primary shop Mylar", transaction.Purpose);
        Assert.Single(await fixture.Context.DrawingAuditEntries.ToListAsync());
    }

    [Fact]
    public async Task SameNormalizedNumberReturnsTheOnePerDrawingConflict()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing();
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();
        var service = new MylarCustodyService(fixture.Context);

        var registered = await service.RegisterAsync(
            drawing.Id,
            "MY-001",
            "Cabinet A",
            null,
            "registrar",
            CancellationToken.None);
        var duplicate = await service.RegisterAsync(
            drawing.Id,
            "my 001",
            "Cabinet B",
            null,
            "registrar",
            CancellationToken.None);

        Assert.True(registered.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, duplicate.StatusCode);
        Assert.Equal("MylarAlreadyRegistered", duplicate.ErrorCode);
        Assert.Single(await fixture.Context.DrawingMylars.ToListAsync());
        Assert.Single(await fixture.Context.MylarTransactions.ToListAsync());
    }

    [Fact]
    public async Task SameMylarNumberCanBeRegisteredForDifferentDrawings()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var firstDrawing = CreateDrawing("1");
        var secondDrawing = CreateDrawing("2");
        fixture.Context.Drawings.AddRange(firstDrawing, secondDrawing);
        await fixture.Context.SaveChangesAsync();
        var service = new MylarCustodyService(fixture.Context);

        var first = await service.RegisterAsync(
            firstDrawing.Id,
            "MY-001",
            "Cabinet A",
            null,
            "registrar",
            CancellationToken.None);
        var second = await service.RegisterAsync(
            secondDrawing.Id,
            "MY-001",
            "Cabinet B",
            null,
            "registrar",
            CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(2, await fixture.Context.DrawingMylars.CountAsync());
        Assert.Equal(
            2,
            await fixture.Context.DrawingMylars
                .Select(mylar => mylar.DrawingId)
                .Distinct()
                .CountAsync());
    }

    [Fact]
    public async Task DoesNotMisreportAnUnrelatedDatabaseFailureAsADuplicateNumber()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing();
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();
        await fixture.Context.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER "TR_Test_MylarRegistrationFailure"
            BEFORE INSERT ON "DrawingMylars"
            BEGIN
                SELECT RAISE(ABORT, 'simulated storage failure');
            END;
            """);
        var service = new MylarCustodyService(fixture.Context);

        var result = await service.RegisterAsync(
            drawing.Id,
            "MY-001",
            "Cabinet A",
            null,
            @"SONAERO\alex",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Equal("MylarRegistrationFailed", result.ErrorCode);
        Assert.DoesNotContain("duplicate", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordsOneMylarLifecycleAndRejectsDuplicateCheckInOrCheckOut()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing();
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();
        var service = new MylarCustodyService(fixture.Context);
        var registered = await service.RegisterAsync(
            drawing.Id,
            "MY-001",
            "Cabinet A",
            "Initial registration",
            "registrar",
            CancellationToken.None);

        var checkout = await service.RecordMovementAsync(
            drawing.Id,
            registered.Mylar!.Id,
            true,
            "Inspection Lab",
            "First article review",
            @"SONAERO\jordan",
            CancellationToken.None);
        var duplicateCheckout = await service.RecordMovementAsync(
            drawing.Id,
            registered.Mylar.Id,
            true,
            "Machine Shop",
            null,
            @"SONAERO\other",
            CancellationToken.None);
        var checkin = await service.RecordMovementAsync(
            drawing.Id,
            registered.Mylar.Id,
            false,
            "Cabinet A / Slot 4",
            "Returned undamaged",
            @"SONAERO\taylor",
            CancellationToken.None);
        var duplicateCheckin = await service.RecordMovementAsync(
            drawing.Id,
            registered.Mylar.Id,
            false,
            "Cabinet C",
            null,
            @"SONAERO\other",
            CancellationToken.None);

        Assert.True(checkout.Succeeded);
        Assert.True(checkin.Succeeded);
        Assert.Equal("MylarAlreadyCheckedOut", duplicateCheckout.ErrorCode);
        Assert.Equal("MylarAlreadyCheckedIn", duplicateCheckin.ErrorCode);

        var mylar = await fixture.Context.DrawingMylars.SingleAsync();
        Assert.False(mylar.IsCheckedOut);
        Assert.Equal("Cabinet A / Slot 4", mylar.CurrentLocation);
        Assert.Null(mylar.CheckedOutBy);
        Assert.Null(mylar.CheckedOutAt);
        Assert.Equal(2, mylar.Version);

        var summary = await fixture.Context.Drawings.SingleAsync();
        Assert.False(summary.IsMylarCheckedOut);
        Assert.Equal("Cabinet A / Slot 4", summary.PhysicalMylarLocation);
        Assert.Null(summary.MylarCheckedOutBy);
        Assert.Null(summary.MylarCheckedOutAt);

        var movements = await fixture.Context.MylarTransactions.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(
            [
                MylarTransactionType.Registered,
                MylarTransactionType.CheckedOut,
                MylarTransactionType.Returned
            ],
            movements.Select(movement => movement.Type).ToArray());
        Assert.All(movements, movement => Assert.Equal(mylar.Id, movement.DrawingMylarId));
        Assert.Equal("registrar", movements[0].RecordedBy);
        Assert.Equal("Initial registration", movements[0].Purpose);
        Assert.Equal(@"SONAERO\jordan", movements[1].RecordedBy);
        Assert.Equal("First article review", movements[1].Purpose);
        Assert.Equal("Inspection Lab", movements[1].Location);
        Assert.Equal(@"SONAERO\taylor", movements[2].RecordedBy);
        Assert.Equal("Returned undamaged", movements[2].Purpose);
        Assert.Equal("Cabinet A / Slot 4", movements[2].Location);

        Assert.Equal(
            ["MylarRegistered", "MylarCheckedOut", "MylarCheckedIn"],
            await fixture.Context.DrawingAuditEntries
                .OrderBy(entry => entry.Id)
                .Select(entry => entry.Action)
                .ToArrayAsync());
    }

    [Fact]
    public async Task ArchivedDrawingCannotCheckOutAnAvailableMylar()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing();
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();
        var service = new MylarCustodyService(fixture.Context);
        var registered = await service.RegisterAsync(drawing.Id, "MY-001", "Archive Cabinet", null, "registrar", CancellationToken.None);
        drawing.IsObsolete = true;
        drawing.ApprovalStatus = DrawingApprovalStatus.Obsolete;
        await fixture.Context.SaveChangesAsync();

        var result = await service.RecordMovementAsync(
            drawing.Id,
            registered.Mylar!.Id,
            true,
            "Shop",
            null,
            "user",
            CancellationToken.None);

        Assert.Equal("ArchivedDrawing", result.ErrorCode);
        Assert.False((await fixture.Context.DrawingMylars.SingleAsync()).IsCheckedOut);
    }

    [Fact]
    public async Task ArchivedDrawingCanCheckInItsCheckedOutMylar()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing();
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();
        var service = new MylarCustodyService(fixture.Context);
        var registered = await service.RegisterAsync(
            drawing.Id,
            "MY-001",
            "Archive Cabinet",
            null,
            "registrar",
            CancellationToken.None);
        var checkout = await service.RecordMovementAsync(
            drawing.Id,
            registered.Mylar!.Id,
            true,
            "Quality Lab",
            "Review before archive",
            "user",
            CancellationToken.None);
        Assert.True(checkout.Succeeded);

        drawing.IsObsolete = true;
        drawing.ApprovalStatus = DrawingApprovalStatus.Obsolete;
        await fixture.Context.SaveChangesAsync();

        var checkin = await service.RecordMovementAsync(
            drawing.Id,
            registered.Mylar.Id,
            false,
            "Archive Cabinet / Slot 2",
            "Returned after archive",
            "user",
            CancellationToken.None);

        Assert.True(checkin.Succeeded);
        var mylar = await fixture.Context.DrawingMylars.SingleAsync();
        Assert.False(mylar.IsCheckedOut);
        Assert.Equal("Archive Cabinet / Slot 2", mylar.CurrentLocation);
        Assert.Equal(
            MylarTransactionType.Returned,
            (await fixture.Context.MylarTransactions.OrderBy(item => item.Id).LastAsync()).Type);
    }

    [Fact]
    public async Task RequiresAuthenticatedActorAndKeepsCustodyHistoryAppendOnly()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing();
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();
        var service = new MylarCustodyService(fixture.Context);

        var unauthenticated = await service.RegisterAsync(
            drawing.Id,
            "MY-001",
            "Cabinet A",
            null,
            " ",
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, unauthenticated.StatusCode);
        Assert.Empty(await fixture.Context.DrawingMylars.ToListAsync());

        await service.RegisterAsync(drawing.Id, "MY-001", "Cabinet A", "Initial record", "test-user", CancellationToken.None);
        var transaction = await fixture.Context.MylarTransactions.SingleAsync();
        transaction.Purpose = "Attempted rewrite";

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task LegacySingleMylarDataIsBackfilledOnceUsingTheLatestLocation()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing();
        drawing.IsMylarCheckedOut = true;
        drawing.MylarCheckedOutBy = "legacy-user";
        drawing.MylarTransactions.Add(new MylarTransaction
        {
            Type = MylarTransactionType.CheckedOut,
            Person = "legacy-user",
            Location = "Old location",
            RecordedBy = "legacy-user",
            RecordedAt = DateTime.UtcNow.AddHours(-2)
        });
        drawing.MylarTransactions.Add(new MylarTransaction
        {
            Type = MylarTransactionType.CheckedOut,
            Person = "legacy-user",
            Location = "Latest location",
            RecordedBy = "legacy-user",
            RecordedAt = DateTime.UtcNow.AddHours(-1)
        });
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();

        var initializer = new EngineeringSchemaInitializer(fixture.Context);
        await initializer.InitializeAsync(CancellationToken.None);
        await initializer.InitializeAsync(CancellationToken.None);

        var mylar = await fixture.Context.DrawingMylars.SingleAsync();
        Assert.Equal("MYLAR-1", mylar.MylarNumber);
        Assert.Equal("Latest location", mylar.CurrentLocation);
        Assert.True(mylar.IsCheckedOut);
        Assert.All(await fixture.Context.MylarTransactions.ToListAsync(), item => Assert.Equal(mylar.Id, item.DrawingMylarId));
    }

    [Fact]
    public async Task SchemaUpgradeStopsBeforeChangingLegacyDuplicateMylarRows()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing();
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();
        await fixture.Context.Database.ExecuteSqlRawAsync(
            """
            DROP INDEX "IX_DrawingMylars_DrawingId";
            CREATE UNIQUE INDEX "IX_DrawingMylars_DrawingId_NormalizedMylarNumber"
                ON "DrawingMylars" ("DrawingId", "NormalizedMylarNumber");
            """);
        fixture.Context.DrawingMylars.AddRange(
            new DrawingMylar
            {
                DrawingId = drawing.Id,
                MylarNumber = "MY-001",
                NormalizedMylarNumber = "MY001",
                CurrentLocation = "Cabinet A",
                CreatedBy = "legacy-user",
                CreatedAt = DateTime.UtcNow.AddMinutes(-2)
            },
            new DrawingMylar
            {
                DrawingId = drawing.Id,
                MylarNumber = "MY-002",
                NormalizedMylarNumber = "MY002",
                CurrentLocation = "Cabinet B",
                CreatedBy = "legacy-user",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1)
            });
        await fixture.Context.SaveChangesAsync();

        var initializer = new EngineeringSchemaInitializer(fixture.Context);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => initializer.InitializeAsync(CancellationToken.None));

        Assert.Contains($"drawing IDs: {drawing.Id}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no duplicate Mylar rows were modified", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, await fixture.Context.DrawingMylars.CountAsync());
    }

    private static Drawing CreateDrawing(string suffix = "1") => new()
    {
        DrawingNumber = $"DRW-MYLAR-{suffix}",
        NormalizedDrawingNumber = $"DRWMYLAR{suffix}",
        Title = "Mylar custody test",
        Customer = "SON-AERO",
        NormalizedCustomer = "SONAERO",
        CreatedBy = "test-user",
        CreatedAt = DateTime.UtcNow
    };

    private sealed class ContextFixture(SqliteConnection connection, EngineeringDbContext context) : IAsyncDisposable
    {
        public EngineeringDbContext Context { get; } = context;

        public static async Task<ContextFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new EngineeringDbContext(
                new DbContextOptionsBuilder<EngineeringDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new ContextFixture(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
