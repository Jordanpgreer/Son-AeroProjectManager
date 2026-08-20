namespace ProjectTracker.Api.Models;

public enum NotificationKind
{
    ProjectChatMention = 1,
    OperationNoteMention = 2,
    OperationStartConfirmation = 3,
    OperationFinishConfirmation = 4,
    OperationStartResponse = 5,
    OperationFinishResponse = 6
}
