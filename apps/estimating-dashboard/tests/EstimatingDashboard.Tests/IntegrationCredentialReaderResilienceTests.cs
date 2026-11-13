using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace EstimatingDashboard.Tests;

public sealed class IntegrationCredentialReaderResilienceTests
{
    private const string Secret = "synthetic-private-token";
    private const string ProviderMessage = "9002 LOG_BACKUP; synthetic-private-connection";
    private const string Key = IntegrationCredentialNames.FulcrumPublicApi;

    [Fact]
    public async Task Failed_usage_write_returns_secret_without_flushing_or_tracking_business_changes()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        await AddCredentialAsync(fixture);
        var failure = new InjectedDbException(ProviderMessage);
        var probe = new CredentialCommandProbe { UsageFailure = () => failure };
        await using var db = CreateContext(fixture, probe);
        var credential = await db.IntegrationCredentials.SingleAsync();
        var quote = await db.QuoteHistory.SingleAsync(row => row.QuoteNumber == 4445);
        quote.ArdaStatus = EstimatingArdaStatuses.RfqSent;
        db.Add(new MaterialPurchasingState { QuoteHistoryId = quote.Id, DraftsJson = "[]" });
        var logger = new RecordingLogger<IntegrationCredentialReader>();
        var reader = new IntegrationCredentialReader(db, new Protector(), TimeProvider.System, logger);

        Assert.Equal(Secret, await reader.GetSecretAsync(Key, default));

        Assert.Equal(1, probe.UsageAttempts);
        Assert.Null(credential.LastUsedAt);
        Assert.Equal(EntityState.Unchanged, db.Entry(credential).State);
        await using var persisted = fixture.AnotherContext();
        Assert.Null((await persisted.IntegrationCredentials.SingleAsync()).LastUsedAt);
        Assert.Null((await persisted.QuoteHistory.SingleAsync(row => row.Id == quote.Id)).ArdaStatus);
        Assert.Empty(await persisted.Set<MaterialPurchasingState>().ToListAsync());
        Assert.True(db.ChangeTracker.HasChanges());
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Null(warning.Exception);
        Assert.Contains(nameof(InjectedDbException), warning.Message);
        Assert.DoesNotContain(ProviderMessage, warning.Message);
        Assert.DoesNotContain(Secret, warning.Message);

        // A later business save must not retry or flush a tracked usage timestamp either.
        probe.UsageFailure = null;
        await db.SaveChangesAsync();
        Assert.Equal(1, probe.UsageAttempts);
        Assert.Null((await persisted.IntegrationCredentials.AsNoTracking().SingleAsync()).LastUsedAt);
        Assert.Equal(EstimatingArdaStatuses.RfqSent,
            (await persisted.QuoteHistory.AsNoTracking().SingleAsync(row => row.Id == quote.Id)).ArdaStatus);
    }

    [Fact]
    public async Task Credential_read_failure_still_propagates_without_decryption_or_usage_write()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        await AddCredentialAsync(fixture);
        var failure = new InjectedDbException(ProviderMessage);
        var probe = new CredentialCommandProbe { ReadFailure = failure };
        await using var db = CreateContext(fixture, probe);
        var protector = new Protector();
        var logger = new RecordingLogger<IntegrationCredentialReader>();
        var reader = new IntegrationCredentialReader(db, protector, TimeProvider.System, logger);

        Assert.Same(failure, await Assert.ThrowsAsync<InjectedDbException>(() => reader.GetSecretAsync(Key, default)));
        Assert.Equal(0, protector.Decryptions);
        Assert.Equal(0, probe.UsageAttempts);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Missing_credential_preserves_null_result_without_usage_write()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        var probe = new CredentialCommandProbe();
        await using var db = CreateContext(fixture, probe);
        var protector = new Protector();
        var reader = new IntegrationCredentialReader(db, protector, TimeProvider.System);

        Assert.Null(await reader.GetSecretAsync(Key, default));
        Assert.Equal(0, protector.Decryptions);
        Assert.Equal(0, probe.UsageAttempts);
    }

    [Fact]
    public async Task Expired_credential_still_fails_before_decryption_or_usage_write()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        await AddCredentialAsync(fixture, DateTimeOffset.UtcNow.AddDays(-1));
        var probe = new CredentialCommandProbe();
        await using var db = CreateContext(fixture, probe);
        var protector = new Protector();
        var reader = new IntegrationCredentialReader(db, protector, TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetSecretAsync(Key, default));
        Assert.Contains("expired", error.Message);
        Assert.Equal(0, protector.Decryptions);
        Assert.Equal(0, probe.UsageAttempts);
    }

    [Theory]
    [InlineData("cryptographic")]
    [InlineData("format")]
    [InlineData("platform")]
    public async Task Unreadable_credential_still_fails_without_usage_write(string kind)
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        await AddCredentialAsync(fixture);
        var probe = new CredentialCommandProbe();
        await using var db = CreateContext(fixture, probe);
        Exception failure = kind switch
        {
            "cryptographic" => new CryptographicException("Synthetic decryption failure"),
            "format" => new FormatException("Synthetic invalid ciphertext"),
            _ => new PlatformNotSupportedException("Synthetic unsupported platform")
        };
        var reader = new IntegrationCredentialReader(db, new Protector { Failure = failure }, TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetSecretAsync(Key, default));
        Assert.Contains("could not be decrypted", error.Message);
        Assert.Same(failure, error.InnerException);
        Assert.Equal(0, probe.UsageAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Usage_write_cancellation_is_never_swallowed_even_when_provider_reports_database_error(bool databaseError)
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        await AddCredentialAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        var probe = new CredentialCommandProbe
        {
            UsageFailure = () =>
            {
                cancellation.Cancel();
                return databaseError ? new InjectedDbException(ProviderMessage)
                    : new OperationCanceledException(cancellation.Token);
            }
        };
        await using var db = CreateContext(fixture, probe);
        var logger = new RecordingLogger<IntegrationCredentialReader>();
        var reader = new IntegrationCredentialReader(db, new Protector(), TimeProvider.System, logger);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.GetSecretAsync(Key, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(logger.Entries);
        Assert.Null((await fixture.Db.IntegrationCredentials.AsNoTracking().SingleAsync()).LastUsedAt);
    }

    [Fact]
    public async Task Programming_failure_in_usage_update_is_not_hidden()
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        await AddCredentialAsync(fixture);
        var failure = new InvalidOperationException("Synthetic programming error");
        var probe = new CredentialCommandProbe { UsageFailure = () => failure };
        await using var db = CreateContext(fixture, probe);
        var reader = new IntegrationCredentialReader(db, new Protector(), TimeProvider.System);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetSecretAsync(Key, default)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Quote_details_load_when_usage_write_fails_and_optional_link_may_be_unavailable(bool coordinationUnavailable)
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        await AddCredentialAsync(fixture);
        var probe = new CredentialCommandProbe { UsageFailure = () => new InjectedDbException(ProviderMessage) };
        await using var db = CreateContext(fixture, probe);
        var reader = new IntegrationCredentialReader(db, new Protector(), TimeProvider.System);
        using var handler = new QuoteHandler(coordinationUnavailable);
        using var http = new HttpClient(handler);
        var options = Options.Create(new FulcrumQuoteSyncOptions
            { QuoteUrlTemplate = "https://son-aero.fulcrumpro.com/ui/quotes/{id}/details" });
        var logger = new RecordingLogger<FulcrumQuoteLinkResolver>();
        var resolver = new FulcrumQuoteLinkResolver(new FulcrumQuoteGenerationClient(http, options, reader),
            new Provider(), options, logger);
        var service = new QuoteStatusService(fixture.Db, TimeProvider.System, fixture.Vendors, fixture.Workflow, resolver);

        var detail = await service.DetailAsync(fixture.QuoteId(), VendorQuoteTestFixture.Editor, default);

        Assert.Equal(4445, detail.Quote.QuoteNumber);
        Assert.Equal("Synthetic customer", detail.Quote.Customer);
        Assert.Equal(coordinationUnavailable ? null : "https://son-aero.fulcrumpro.com/ui/quotes/test-4445/details",
            detail.FulcrumQuoteUrl);
        Assert.Equal(1, probe.UsageAttempts);
        Assert.Equal(1, handler.Requests);
        Assert.Null((await fixture.Db.IntegrationCredentials.AsNoTracking().SingleAsync()).LastUsedAt);
        if (coordinationUnavailable)
        {
            var entry = Assert.Single(logger.Entries);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(ProviderMessage, entry.Message);
            Assert.DoesNotContain(Secret, entry.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Optional_link_does_not_hide_caller_cancellation_when_provider_failure_races_it(bool coordinationUnavailable)
    {
        await using var fixture = await VendorQuoteTestFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        using var handler = new QuoteHandler(false);
        using var http = new HttpClient(handler);
        var options = Options.Create(new FulcrumQuoteSyncOptions
            { QuoteUrlTemplate = "https://son-aero.fulcrumpro.com/ui/quotes/{id}/details" });
        var reader = new IntegrationCredentialReader(fixture.Db, new Protector(), TimeProvider.System);
        var logger = new RecordingLogger<FulcrumQuoteLinkResolver>();
        var resolver = new FulcrumQuoteLinkResolver(new FulcrumQuoteGenerationClient(http, options, reader),
            new CancellingProvider(cancellation, coordinationUnavailable), options, logger);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync("test-4445", 4445, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, handler.Requests);
        Assert.Empty(logger.Entries);
    }

    private static async Task AddCredentialAsync(VendorQuoteTestFixture fixture, DateTimeOffset? expiresAt = null)
    {
        fixture.Db.IntegrationCredentials.Add(new EstimatingIntegrationCredentialRecord
        {
            CredentialKey = Key, DisplayName = "Synthetic credential", EncryptedSecret = "synthetic-ciphertext",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "TEST", ExpiresAt = expiresAt
        });
        await fixture.Db.SaveChangesAsync();
    }

    private static EstimatingAccessDbContext CreateContext(VendorQuoteTestFixture fixture, CredentialCommandProbe probe) =>
        new(new DbContextOptionsBuilder<EstimatingAccessDbContext>().UseSqlite(fixture.Db.Database.GetDbConnection())
            .AddInterceptors(probe).Options);

    private sealed class CredentialCommandProbe : DbCommandInterceptor
    {
        public Func<Exception>? UsageFailure { get; set; }
        public Exception? ReadFailure { get; init; }
        public int UsageAttempts { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"IntegrationCredentials\"", StringComparison.Ordinal))
            {
                UsageAttempts++;
                if (UsageFailure is not null) throw UsageFailure();
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (ReadFailure is not null && command.CommandText.Contains("FROM \"IntegrationCredentials\"", StringComparison.Ordinal))
                throw ReadFailure;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedDbException(string message) : DbException(message);
    private sealed class Protector : IIntegrationSecretProtector
    {
        public Exception? Failure { get; init; }
        public int Decryptions { get; private set; }
        public string Protect(string secret) => throw new NotSupportedException();
        public string Unprotect(string encryptedSecret)
        {
            Decryptions++;
            if (Failure is not null) throw Failure;
            return Secret;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class Provider : IEnterpriseProviderSource
    {
        public Task<string> GetActiveProviderAsync(CancellationToken cancellationToken) => Task.FromResult(EnterpriseProviderNames.Fulcrum);
    }
    private sealed class CancellingProvider(CancellationTokenSource cancellation, bool coordinationUnavailable) : IEnterpriseProviderSource
    {
        public Task<string> GetActiveProviderAsync(CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw coordinationUnavailable
                ? new FulcrumCoordinationUnavailableException(new InjectedDbException(ProviderMessage))
                : new InvalidOperationException("Synthetic provider failure");
        }
    }
    private sealed class QuoteHandler(bool coordinationUnavailable) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (coordinationUnavailable)
                throw new FulcrumCoordinationUnavailableException(new InjectedDbException(ProviderMessage));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"id":"test-4445","number":4445}""") });
        }
    }
}
