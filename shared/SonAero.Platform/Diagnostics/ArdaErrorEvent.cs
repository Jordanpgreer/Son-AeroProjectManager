using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SonAero.Platform.Diagnostics;

public sealed class ArdaErrorEvent
{
    public long Id { get; set; }
    public string ApplicationId { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string AffectedArea { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public int HttpStatusCode { get; set; }
    public string RequestMethod { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public string TraceIdentifier { get; set; } = string.Empty;
    public string? AccountName { get; set; }
    public string? ExceptionType { get; set; }
}

public sealed class ArdaErrorEventConfiguration : IEntityTypeConfiguration<ArdaErrorEvent>
{
    public void Configure(EntityTypeBuilder<ArdaErrorEvent> entity)
    {
        entity.ToTable("ArdaErrorEvents");
        entity.HasKey(error => error.Id);
        entity.Property(error => error.Id).ValueGeneratedOnAdd();
        entity.Property(error => error.ApplicationId).HasMaxLength(64);
        entity.Property(error => error.ApplicationName).HasMaxLength(120);
        entity.Property(error => error.ErrorCode).HasMaxLength(80);
        entity.Property(error => error.Explanation).HasMaxLength(500);
        entity.Property(error => error.AffectedArea).HasMaxLength(240);
        entity.Property(error => error.RequestMethod).HasMaxLength(12);
        entity.Property(error => error.RequestPath).HasMaxLength(500);
        entity.Property(error => error.TraceIdentifier).HasMaxLength(160);
        entity.Property(error => error.AccountName).HasMaxLength(160);
        entity.Property(error => error.ExceptionType).HasMaxLength(240);
        entity.HasIndex(error => error.OccurredAtUtc);
        entity.HasIndex(error => new { error.ApplicationId, error.OccurredAtUtc });
        entity.HasIndex(error => new { error.ErrorCode, error.OccurredAtUtc });
    }
}
