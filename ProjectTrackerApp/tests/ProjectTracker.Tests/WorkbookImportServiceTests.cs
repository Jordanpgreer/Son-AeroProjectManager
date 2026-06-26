using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Services;
using ProjectTracker.Api.Services.Import;

namespace ProjectTracker.Tests;

public sealed class WorkbookImportServiceTests
{
    [Fact]
    public async Task ImportAsync_LoadsCurrentWorkbookProjectsTasksAndHolidays()
    {
        var workbookPath = FindWorkbook();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var importer = new WorkbookImportService(new ProjectMetricsService(new ScheduleCalculator()));
        var result = await importer.ImportAsync(db, workbookPath, replaceExisting: true);

        Assert.Equal(4, result.ProjectCount);
        Assert.Equal(57, result.TaskCount);
        Assert.Equal(10, result.HolidayCount);
        Assert.Equal(4, await db.Projects.CountAsync());
        Assert.Equal(57, await db.Tasks.CountAsync());
        Assert.Equal(10, await db.Holidays.CountAsync());
        Assert.Contains(await db.Projects.Select(project => project.ProgramName).ToListAsync(), name => name == "1TD1351A10015-1009");
        Assert.Contains(await db.Projects.Select(project => project.ProgramName).ToListAsync(), name => name == "280-331-310");
    }

    private static string FindWorkbook()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Project Tracker.xlsm");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find Project Tracker.xlsm from the test output path.");
    }
}
