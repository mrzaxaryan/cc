namespace cc.Infrastructure;

public enum MessageType { Info, Success, Warning, Error }

public class MessageService
{
    public event Action<string, MessageType>? OnNotification;

    public void Show(string text, MessageType type = MessageType.Info)
    {
        OnNotification?.Invoke(text, type);
    }

    public void Error(string text) => Show(text, MessageType.Error);
    public void Success(string text) => Show(text, MessageType.Success);
    public void Warn(string text) => Show(text, MessageType.Warning);
    public void Info(string text) => Show(text, MessageType.Info);
}
