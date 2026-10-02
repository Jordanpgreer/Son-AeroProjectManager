using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmallBusinessSubcontracting.Api;

// Design-time tooling must never start the web app, contact the shared RoleStore,
// or use the developer's SQLite data. SQL Server is the canonical model.
public sealed class SubcontractingDbContextFactory : IDesignTimeDbContextFactory<SubcontractingDbContext>
{
    public SubcontractingDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SubcontractingDbContext>()
            .UseSqlServer("Server=(local);Database=SmallBusinessSubcontracting;" +
                "Integrated Security=True;Encrypt=True;TrustServerCertificate=True")
            .Options);
}
