using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class OperationDateValidatorTests
{
    [Fact]
    public void Validate_RejectsAStartDateAfterTheEndDate()
    {
        var operation = Operation(
            startDate: new DateOnly(2026, 8, 25),
            endDate: new DateOnly(2026, 8, 24));

        var error = OperationDateValidator.Validate(operation);

        Assert.Equal("Start date cannot be later than end date.", error);
    }

    [Fact]
    public void Validate_AllowsTheSameStartAndEndDate()
    {
        var date = new DateOnly(2026, 8, 24);

        Assert.Null(OperationDateValidator.Validate(Operation(date, date)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Validate_AllowsAnIncompleteDateRange(bool hasStartDate, bool hasEndDate)
    {
        Assert.Null(OperationDateValidator.Validate(Operation(
            hasStartDate ? new DateOnly(2026, 8, 24) : null,
            hasEndDate ? new DateOnly(2026, 8, 25) : null)));
    }

    [Fact]
    public void Validate_AllowsAStartDateBeforeTheEndDate()
    {
        Assert.Null(OperationDateValidator.Validate(Operation(
            new DateOnly(2026, 8, 24),
            new DateOnly(2026, 8, 25))));
    }

    private static TaskUpsertDto Operation(DateOnly? startDate, DateOnly? endDate) => new(
        Sequence: 1,
        ExternalTaskId: null,
        Title: "Operation 1",
        Phase: null,
        WorkStation: null,
        DependencyTaskId: null,
        StartDate: startDate,
        StartDateLocked: false,
        OriginalStartDate: null,
        EndDate: endDate,
        OriginalEndDate: null,
        EstimatedDuration: null,
        ActualDuration: null,
        PercentComplete: 0m,
        PercentCompleteManual: false,
        Notes: null,
        OvertimeDays: null,
        Version: 0,
        ProjectVersion: 0);
}
