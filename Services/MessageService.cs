namespace cc.Services;

public enum MessageType { Info, Success, Warning, Error }

public class ToastMessage
{
    public int Id { get; init; }
    public string Text { get; init; } = "";
    public MessageType Type { get; init; }
    public DateTime Created { get; init; } = DateTime.Now;
    public int DurationMs { get; init; } = 5000;
}

public class MessageService
{
    private int _nextId;
    private readonly List<ToastMessage> _messages = new();

    public IReadOnlyList<ToastMessage> Messages => _messages;

    public event Action? OnChanged;

    public void Show(string text, MessageType type = MessageType.Info, int durationMs = 5000)
    {
        _messages.Add(new ToastMessage
        {
            Id = _nextId++,
            Text = text,
            Type = type,
            DurationMs = durationMs
        });
        OnChanged?.Invoke();
    }

    public void Error(string text, int durationMs = 8000) => Show(text, MessageType.Error, durationMs);
    public void Success(string text, int durationMs = 4000) => Show(text, MessageType.Success, durationMs);
    public void Warn(string text, int durationMs = 6000) => Show(text, MessageType.Warning, durationMs);
    public void Info(string text, int durationMs = 5000) => Show(text, MessageType.Info, durationMs);

    public void Dismiss(int id)
    {
        _messages.RemoveAll(m => m.Id == id);
        OnChanged?.Invoke();
    }
}
