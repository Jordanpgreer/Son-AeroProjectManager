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
    public async Task RegistersUniqueNumberedMylarsAndRetainsTheSignedInActorAndNote()
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
        var duplicate = await service.RegisterAsync(
            drawing.Id,
            "my 001",
            "Cabinet B / Slot 1",
            null,
            @"SONAERO\alex",
            CancellationToken.None);

        Assert.True(registered.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, duplicate.StatusCode);
        Assert.Equal("DuplicateMylarNumber", duplicate.ErrorCode);
        var transaction = await fixture.Context.MylarTransactions.SingleAsync();
        Assert.Equal(MylarTransactionType.Registered, transaction.Type);
        Assert.Equal(@"SONAERO\alex", transaction.Person);
        Assert.Equal(@"SONAERO\alex", transaction.RecordedBy);
        Assert.Equal("Primary shop Mylar", transaction.Purpose);
    }

    [Fact]
    public async Task EnforcesStatePerMylarAndRejectsDuplicateCheckInOrCheckOut()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var drawing = CreateDrawing();
        fixture.Context.Drawings.Add(drawing);
        await fixture.Context.SaveChangesAsync();
        var service = new MylarCustodyService(fixture.Context);
        var first = await service.RegisterAsync(drawing.Id, "MY-001", "Cabinet A", null, "registrar", CancellationToken.None);
        var second = await service.RegisterAsync(drawing.Id, "MY-002", "Cabinet B", null, "registrar", CancellationToken.None);

        var checkout = await service.RecordMovementAsync(
            drawing.Id,
            first.Mylar!.Id,
            true,
            "Inspection Lab",
            "First article review",
            @"SONAERO\jordan",
            CancellationToken.None);
        var duplicateCheckout = await service.RecordMovementAsync(
            drawing.Id,
            first.Mylar.Id,
            true,
            "Machine Shop",
            null,
            @"SONAERO\other",
            CancellationToken.None);
        var secondCheckout = await service.RecordMovementAsync(
            drawing.Id,
            second.Mylar!.Id,
            true,
            "Quality Office",
            "Independent copy",
            @"SONAERO\casey",
            CancellationToken.None);
        var checkin = await service.RecordMovementAsync(
            drawing.Id,
            first.Mylar.Id,
            false,
            "Cabinet A / Slot 4",
            "Returned undamaged",
            @"SONAERO\taylor",
            CancellationToken.None);
        var duplicateCheckin = await service.RecordMovementAsync(
            drawing.Id,
            first.Mylar.Id,
            false,
            "Cabinet C",
            null,
            @"SONAERO\other",
            CancellationToken.None);

        Assert.True(checkout.Succeeded);
        Assert.True(secondCheckout.Succeeded);
        Assert.True(checkin.Succeeded);
        Assert.Equal("MylarAlreadyCheckedOut", duplicateCheckout.ErrorCode);
        Assert.Equal("MylarAlreadyCheckedIn", duplicateCheckin.ErrorCode);

        var mylars = await fixture.Context.DrawingMylars.OrderBy(x => x.MylarNumber).ToListAsync();
        Assert.False(mylars[0].IsCheckedOut);
        Assert.Equal("Cabinet A / Slot 4", mylars[0].CurrentLocation);
        Assert.True(mylars[1].IsCheckedOut);
        Assert.Equal(@"SONAERO\casey", mylars[1].CheckedOutBy);

        var movements = await fixture.Context.MylarTransactions.OrderBy(x => x.RecordedAt).ToListAsync();
        Assert.Equal(5, movements.Count);
        Assert.Equal(@"SONAERO\jordan", movements[2].RecordedBy);
        Assert.Equal("First article review", movements[2].Purpose);
        Assert.Equal(@"SONAERO\taylor", movements[4].RecordedBy);
        Assert.Equal("Returned undamaged", movements[4].Purpose);
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

    private static Drawing CreateDrawing() => new()
    {
        DrawingNumber = "DRW-MYLAR-1",
        NormalizedDrawingNumber = "DRWMYLAR1",
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
