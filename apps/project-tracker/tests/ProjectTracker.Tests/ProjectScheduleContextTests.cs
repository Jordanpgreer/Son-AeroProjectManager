using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class ProjectScheduleContextTests
{
    [Theory]
    [InlineData(20, -3, "3 days ahead")]
    [InlineData(23, 0, "On schedule")]
    [InlineData(28, 5, "5 days behind")]
    public void CompletedProject_ReportsPlannedActualAndCalendarDayVariance(
        int completedDay,
        int expectedVariance,
        string expectedPerformance)
    {
        var project = new Project
        {
            ProgramName = "Completed Test",
            Status = ProjectStatus.Complete,
            CompletedOn = new DateOnly(2026, 4, completedDay),
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Production",
                    OriginalStartDate = new DateOnly(2026, 4, 1),
                    OriginalEndDate = new DateOnly(2026, 4, 23),
                    StartDate = new DateOnly(2026, 4, 2),
                    EndDate = new DateOnly(2026, 4, completedDay)
                }
            ]
        };

        var context = ProjectScheduleContext.From(project);

        Assert.Equal(new DateOnly(2026, 4, 1), context.PlannedStart);
        Assert.Equal(new DateOnly(2026, 4, 23), context.PlannedFinish);
        Assert.Equal(new DateOnly(2026, 4, 2), context.ActualStart);
        Assert.Equal(project.CompletedOn, context.ActualFinish);
        Assert.Equal(expectedVariance, context.VarianceDays);
        Assert.Equal(expectedPerformance, context.Performance);
    }

    [Fact]
    public void ActiveProject_DoesNotClaimActualFinishOrVariance()
    {
        var project = new Project
        {
            ProgramName = "Active Test",
            Status = ProjectStatus.OnTrack,
            TargetDelivery = new DateOnly(2026, 10, 30)
        };

        var context = ProjectScheduleContext.From(project);

        Assert.Null(context.ActualFinish);
        Assert.Null(context.VarianceDays);
        Assert.Null(context.Performance);
    }
}
