using cc.Infrastructure;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace cc.Features.Notifications;

public class NotificationRecord
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "info";
    [JsonPropertyName("created")] public double Created { get; set; }
}

public class NotificationStore
{
    private readonly IJSRuntime _js;
    private List<NotificationRecord> _items = new();
    private bool _loaded;

    public NotificationStore(IJSRuntime js) => _js = js;

    public IReadOnlyList<NotificationRecord> Items => _items;
    public int UnreadCount { get; private set; }

    public event Action? OnChanged;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var records = await _js.InvokeAsync<NotificationRecord[]>("ccNotificationDb.getAll");
            _items = records.OrderByDescending(r => r.Created).ToList();
            UnreadCount = _items.Count;
        }
        catch
        {
            _items = new();
        }
    }

    public async Task AddAsync(string text, MessageType type)
    {
        var record = new NotificationRecord
        {
            Text = text,
            Type = type.ToString().ToLower(),
            Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        await _js.InvokeVoidAsync("ccNotificationDb.put", record);

        // Re-fetch to get the auto-incremented id
        var all = await _js.InvokeAsync<NotificationRecord[]>("ccNotificationDb.getAll");
        _items = all.OrderByDescending(r => r.Created).ToList();
        UnreadCount++;
        OnChanged?.Invoke();
    }

    public async Task RemoveAsync(int id)
    {
        await _js.InvokeVoidAsync("ccNotificationDb.remove", id);
        _items.RemoveAll(n => n.Id == id);
        UnreadCount = Math.Max(0, UnreadCount - 1);
        OnChanged?.Invoke();
    }

    public async Task ClearAsync()
    {
        await _js.InvokeVoidAsync("ccNotificationDb.clear");
        _items.Clear();
        UnreadCount = 0;
        OnChanged?.Invoke();
    }

    public void MarkAllRead()
    {
        UnreadCount = 0;
        OnChanged?.Invoke();
    }
}
