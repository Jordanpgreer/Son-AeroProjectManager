using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class ProjectNoteServiceTests
{
    [Fact]
    public void GetMostRecent_UsesNoteTimestampInsteadOfOperationOrderOrUpdateTime()
    {
        var older = new DateTimeOffset(2026, 6, 29, 14, 0, 0, TimeSpan.Zero);
        var newer = older.AddHours(1);
        var project = new Project
        {
            ProgramName = "Recent note test",
            Tasks =
            [
                new ProjectTask
                {
                    Id = 1,
                    Sequence = 3,
                    Title = "Step 3",
                    Notes = "This note was edited most recently.",
                    NoteUpdatedAt = newer,
                    UpdatedAt = newer,
                    Status = TaskScheduleStatus.OnTrack
                },
                new ProjectTask
                {
                    Id = 2,
                    Sequence = 4,
                    Title = "Step 4",
                    Notes = "This note is older.",
                    NoteUpdatedAt = older,
                    UpdatedAt = newer.AddHours(1),
                    Status = TaskScheduleStatus.Complete
                },
                new ProjectTask
                {
                    Id = 3,
                    Sequence = 5,
                    Title = "Step 5",
                    Notes = "This completed step also has an older note.",
                    NoteUpdatedAt = older.AddMinutes(1),
                    UpdatedAt = newer.AddHours(2),
                    Status = TaskScheduleStatus.Complete
                }
            ]
        };

        var result = ProjectNoteService.GetMostRecent(project);

        Assert.NotNull(result);
        Assert.Equal(1, result.Task.Id);
        Assert.Equal(newer, result.UpdatedAt);
    }

    [Fact]
    public async Task BackfillUpdatedAt_UsesLatestNoteAuditAndNotGeneralTaskUpdate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var createdAt = new DateTimeOffset(2026, 6, 29, 14, 0, 0, TimeSpan.Zero);
        var noteChangedAt = createdAt.AddHours(1);
        var project = new Project
        {
            ProgramName = "Backfill test",
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Earlier operation",
                    Notes = "Newest note",
                    CreatedAt = createdAt,
                    UpdatedAt = noteChangedAt.AddHours(2)
                },
                new ProjectTask
                {
                    Sequence = 2,
                    Title = "Latest operation",
                    Notes = "Seeded note",
                    CreatedAt = createdAt,
                    UpdatedAt = noteChangedAt.AddHours(3)
                }
            ]
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        db.ProjectAuditEntries.Add(new ProjectAuditEntry
        {
            ProjectId = project.Id,
            ProjectTaskId = project.Tasks[0].Id,
            Action = "OperationUpdated",
            Summary = "Updated operation note",
            ChangesJson = "[{\"field\":\"Notes\",\"oldValue\":\"Old\",\"newValue\":\"Newest note\"}]",
            ChangedByAccountName = "TEST\\editor",
            ChangedByDisplayName = "Test Editor",
            ChangedAt = noteChangedAt
        });
        await db.SaveChangesAsync();

        await ProjectNoteService.BackfillUpdatedAtAsync(db);

        Assert.Equal(noteChangedAt, project.Tasks[0].NoteUpdatedAt);
        Assert.Equal(createdAt, project.Tasks[1].NoteUpdatedAt);
        Assert.Equal(project.Tasks[0].Id, ProjectNoteService.GetMostRecent(project)!.Task.Id);
    }
}
