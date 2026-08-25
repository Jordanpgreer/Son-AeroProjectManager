using ProjectTracker.Api.Dtos;

namespace ProjectTracker.Api.Services;

public static class OperationDateValidator
{
    public const string StartAfterEndMessage = "Start date cannot be later than end date.";

    public static string? Validate(TaskUpsertDto operation) =>
        operation.StartDate is { } startDate
        && operation.EndDate is { } endDate
        && startDate > endDate
            ? StartAfterEndMessage
            : null;
}
