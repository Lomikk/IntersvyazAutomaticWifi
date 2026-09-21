namespace IS74Wifi.Core;

public enum NotificationMode
{
    Important,
    All,
    Off
}

public enum AgentNotificationImportance
{
    Routine,
    Important
}

public enum AgentNotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record AgentNotification(
    string Title,
    string Message,
    AgentNotificationImportance Importance,
    AgentNotificationSeverity Severity = AgentNotificationSeverity.Info);

public interface IAgentNotificationSink
{
    void Publish(AgentNotification notification);
}
