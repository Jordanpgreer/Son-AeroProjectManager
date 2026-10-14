namespace ProjectTracker.Api.Configuration;

public sealed class OperationScheduleReminderOptions
{
    public const string SectionName = "OperationScheduleReminders";

    public bool Enabled { get; set; }
}
