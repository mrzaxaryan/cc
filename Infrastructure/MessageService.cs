namespace cc.Infrastructure;

/// <summary>Severity level for toast/notification messages.</summary>
public enum MessageType
{
    /// <summary>Informational notice (neutral).</summary>
    Info,
    /// <summary>Operation completed successfully.</summary>
    Success,
    /// <summary>Non-critical issue that may need attention.</summary>
    Warning,
    /// <summary>Operation failed or a critical problem occurred.</summary>
    Error
}

public class MessageService
{
    private readonly IEventBus _bus;

    public MessageService(IEventBus bus) => _bus = bus;

    public void Show(string text, MessageType type = MessageType.Info)
    {
        _bus.Publish(new NotificationEvent(text, type));
    }

    public void Error(string text) => Show(text, MessageType.Error);
    public void Success(string text) => Show(text, MessageType.Success);
    public void Warn(string text) => Show(text, MessageType.Warning);
    public void Info(string text) => Show(text, MessageType.Info);
}
