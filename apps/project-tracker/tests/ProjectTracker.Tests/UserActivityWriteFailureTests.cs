using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class UserActivityWriteFailureTests
{
    [Fact]
    public async Task DatabaseWriteFailure_IsOptionalAndLogsOnlySanitizedDetails()
    {
        await using var database = await ActivityDatabase.CreateAsync();
        var failure = new SimulatedDatabaseException("secret-server;password=never-log-this");
        var interceptor = new ActivityCommandInterceptor(writeFailure: failure);
        var logger = new ActivityLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logger));
        await using var db = database.CreateContext(interceptor, loggerFactory);
        db.Database.SetCommandTimeout(17);
        var user = await db.Users.SingleAsync();
        user.DisplayName = "Pending business edit";

        await UserActivityService.TouchLastSeenAsync(db, user.Id, database.InitialSeenAt.AddHours(1), default);

        Assert.Equal(1, interceptor.WriteAttempts);
        Assert.Equal(17, db.Database.GetCommandTimeout());
        Assert.Equal(EntityState.Modified, db.Entry(user).State);
        Assert.Equal("Pending business edit", user.DisplayName);
        await using var verify = database.CreateContext();
        var persisted = await verify.Users.SingleAsync();
        Assert.Equal("Saved name", persisted.DisplayName);
        Assert.Equal(database.InitialSeenAt, persisted.LastSeenAt);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(nameof(SimulatedDatabaseException), warning.Message);
        Assert.Contains("9002", warning.Message);
        Assert.DoesNotContain("never-log-this", warning.Message);
        Assert.DoesNotContain("secret-server", warning.Message);
        Assert.Null(warning.Exception);
    }

    [Fact]
    public async Task SuccessfulWrite_UpdatesOnlyLastSeenWithoutSavingPendingChanges()
    {
        await using var database = await ActivityDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await db.Users.SingleAsync();
        user.DisplayName = "Pending business edit";
        var seenAt = database.InitialSeenAt.AddHours(1);

        await UserActivityService.TouchLastSeenAsync(db, user.Id, seenAt, default);

        await using var verify = database.CreateContext();
        var persisted = await verify.Users.SingleAsync();
        Assert.Equal(seenAt, persisted.LastSeenAt);
        Assert.Equal("Saved name", persisted.DisplayName);
        Assert.Equal(EntityState.Modified, db.Entry(user).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseReadFailure_PropagatesExceptKnownSqliteContention(bool serverFailure)
    {
        await using var database = await ActivityDatabase.CreateAsync();
        DbException failure = serverFailure
            ? new SimulatedDatabaseException("Server read failed")
            : new SqliteException("Timestamp schema read failed", 1);
        var interceptor = new ActivityCommandInterceptor(readFailure: failure);
        await using var db = database.CreateContext(interceptor);
        db.Database.SetCommandTimeout(17);

        var actual = await Assert.ThrowsAnyAsync<DbException>(() =>
            UserActivityService.TouchLastSeenAsync(db, 1, database.InitialSeenAt.AddHours(1), default));

        Assert.Same(failure, actual);
        Assert.Equal(0, interceptor.WriteAttempts);
        Assert.Equal(17, db.Database.GetCommandTimeout());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task OptionalTimestampRead_PreservesSqliteContentionTolerance(int errorCode)
    {
        await using var database = await ActivityDatabase.CreateAsync();
        var interceptor = new ActivityCommandInterceptor(readFailure: new SqliteException("Database is busy", errorCode));
        await using var db = database.CreateContext(interceptor);
        db.Database.SetCommandTimeout(17);

        await UserActivityService.TouchLastSeenAsync(db, 1, database.InitialSeenAt.AddHours(1), default);

        Assert.Equal(0, interceptor.WriteAttempts);
        Assert.Equal(17, db.Database.GetCommandTimeout());
        await using var verify = database.CreateContext();
        Assert.Equal(database.InitialSeenAt, (await verify.Users.SingleAsync()).LastSeenAt);
    }

    [Fact]
    public async Task CancellationDuringSqliteTimestampReadContention_IsNotSuppressed()
    {
        await using var database = await ActivityDatabase.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var interceptor = new ActivityCommandInterceptor(
            readFailure: new SqliteException("Database is busy", 5), beforeRead: cancellation.Cancel);
        await using var db = database.CreateContext(interceptor);
        db.Database.SetCommandTimeout(17);

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UserActivityService.TouchLastSeenAsync(db, 1, database.InitialSeenAt.AddHours(1), cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal(0, interceptor.WriteAttempts);
        Assert.Equal(17, db.Database.GetCommandTimeout());
    }

    [Fact]
    public async Task UnexpectedReadFailure_Propagates()
    {
        await using var database = await ActivityDatabase.CreateAsync();
        var failure = new InvalidOperationException("Required read failed");
        await using var db = database.CreateContext(new ActivityCommandInterceptor(readFailure: failure));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UserActivityService.TouchLastSeenAsync(db, 1, database.InitialSeenAt.AddHours(1), default));

        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task UnexpectedWriteFailure_Propagates()
    {
        await using var database = await ActivityDatabase.CreateAsync();
        var failure = new InvalidOperationException("Programming error");
        await using var db = database.CreateContext(new ActivityCommandInterceptor(writeFailure: failure));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UserActivityService.TouchLastSeenAsync(db, 1, database.InitialSeenAt.AddHours(1), default));

        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task CancellationDuringDatabaseWriteFailure_IsNotSuppressed()
    {
        await using var database = await ActivityDatabase.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var interceptor = new ActivityCommandInterceptor(
            writeFailure: new SimulatedDatabaseException("Cancelled database write"),
            beforeWrite: cancellation.Cancel);
        var logger = new ActivityLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logger));
        await using var db = database.CreateContext(interceptor, loggerFactory);
        db.Database.SetCommandTimeout(17);

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UserActivityService.TouchLastSeenAsync(db, 1, database.InitialSeenAt.AddHours(1), cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal(1, interceptor.WriteAttempts);
        Assert.Equal(17, db.Database.GetCommandTimeout());
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task OperationCanceledExceptionFromWrite_Propagates()
    {
        await using var database = await ActivityDatabase.CreateAsync();
        var failure = new OperationCanceledException("Provider cancellation");
        await using var db = database.CreateContext(new ActivityCommandInterceptor(writeFailure: failure));

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            UserActivityService.TouchLastSeenAsync(db, 1, database.InitialSeenAt.AddHours(1), default));

        Assert.Same(failure, actual);
    }

    private sealed class SimulatedDatabaseException : DbException
    {
        public SimulatedDatabaseException(string message) : base(message) => HResult = 9002;
    }

    private sealed class ActivityCommandInterceptor(
        Exception? readFailure = null, Exception? writeFailure = null,
        Action? beforeWrite = null, Action? beforeRead = null) : DbCommandInterceptor
    {
        public int WriteAttempts { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            beforeRead?.Invoke();
            if (readFailure is not null) throw readFailure;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            beforeWrite?.Invoke();
            if (writeFailure is not null) throw writeFailure;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ActivityLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new ActivityLogger(this, categoryName);
        public void Dispose() { }

        private sealed class ActivityLogger(ActivityLoggerProvider provider, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => categoryName == typeof(UserActivityService).FullName;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) provider.Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed class ActivityDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public DateTimeOffset InitialSeenAt { get; } = DateTimeOffset.UtcNow.AddHours(-1);

        public static async Task<ActivityDatabase> CreateAsync()
        {
            var database = new ActivityDatabase();
            await database.connection.OpenAsync();
            await using var db = database.CreateContext();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE Users (Id INTEGER PRIMARY KEY, AccountName TEXT NOT NULL, DisplayName TEXT NOT NULL,
                    IsActive INTEGER NOT NULL, LastSeenAt TEXT NOT NULL, Role TEXT NULL);
                """);
            db.Users.Add(new AppUser
            {
                Id = 1,
                AccountName = @"SON4L\activity.user",
                DisplayName = "Saved name",
                LastSeenAt = database.InitialSeenAt
            });
            await db.SaveChangesAsync();
            return database;
        }

        public ProjectTrackerDbContext CreateContext(IInterceptor? interceptor = null, ILoggerFactory? loggerFactory = null)
        {
            var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            if (loggerFactory is not null) options.UseLoggerFactory(loggerFactory);
            return new ProjectTrackerDbContext(options.Options);
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
