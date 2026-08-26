using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QualityAssurance.Api.Data;

public sealed class QualityAssuranceDbContextFactory
    : IDesignTimeDbContextFactory<QualityAssuranceDbContext>
{
    public QualityAssuranceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<QualityAssuranceDbContext>()
            .UseSqlServer(
                "Server=.\\SQLEXPRESS;Database=QualityAssurance;Trusted_Connection=True;" +
                "TrustServerCertificate=True;MultipleActiveResultSets=true")
            .Options;

        return new QualityAssuranceDbContext(options);
    }
}
