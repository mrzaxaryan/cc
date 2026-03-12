using C2.Infrastructure;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace C2.Features.Notifications;

public class NotificationRecord
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "info";
    [JsonPropertyName("created")] public double Created { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
}

public class NotificationStore
{
    private readonly IJSRuntime _js;
    private readonly IEventBus _bus;
    private List<NotificationRecord> _items = new();
    private bool _loaded;

    public NotificationStore(IJSRuntime js, IEventBus bus)
    {
        _js = js;
        _bus = bus;
    }

    public IReadOnlyList<NotificationRecord> Items => _items;
    public int UnreadCount { get; private set; }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var records = await _js.InvokeAsync<NotificationRecord[]>("c2NotificationDb.getAll");
            _items = records.OrderByDescending(r => r.Created).ToList();
            UnreadCount = _items.Count;
        }
        catch
        {
            _items = new();
        }
    }

    public async Task AddAsync(string text, MessageType type, string? title = null, string? detail = null, string? source = null)
    {
        var record = new NotificationRecord
        {
            Text = text,
            Type = type.ToString().ToLower(),
            Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Title = title,
            Detail = detail,
            Source = source
        };

        await _js.InvokeVoidAsync("c2NotificationDb.put", record);

        // Re-fetch to get the auto-incremented id
        var all = await _js.InvokeAsync<NotificationRecord[]>("c2NotificationDb.getAll");
        _items = all.OrderByDescending(r => r.Created).ToList();
        UnreadCount++;
        _bus.Publish(new NotificationStoreChangedEvent());
    }

    public async Task RemoveAsync(int id)
    {
        await _js.InvokeVoidAsync("c2NotificationDb.remove", id);
        _items.RemoveAll(n => n.Id == id);
        UnreadCount = Math.Max(0, UnreadCount - 1);
        _bus.Publish(new NotificationStoreChangedEvent());
    }

    public async Task ClearAsync()
    {
        await _js.InvokeVoidAsync("c2NotificationDb.clear");
        _items.Clear();
        UnreadCount = 0;
        _bus.Publish(new NotificationStoreChangedEvent());
    }

    public void MarkAllRead()
    {
        UnreadCount = 0;
        _bus.Publish(new NotificationStoreChangedEvent());
    }
}
