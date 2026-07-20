using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ProjectTracker.Api.Data;

public sealed class ProjectTrackerDbContextFactory : IDesignTimeDbContextFactory<ProjectTrackerDbContext>
{
    public ProjectTrackerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlServer("Server=.\\SQLEXPRESS;Database=ProjectTracker;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true")
            .Options;

        return new ProjectTrackerDbContext(options);
    }
}

