using System.Text.Json.Serialization;
using cc.Infrastructure;
using Microsoft.JSInterop;

namespace cc.Features.Relay;

public class RelayRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("active")] public bool Enabled { get; set; }
}

public class RelayStore
{
    private const string DefaultUrl = "wss://relay.nostdlib.workers.dev";
    private const string DefaultName = "Default";

    private readonly IJSRuntime _js;
    private readonly IEventBus _bus;
    private List<RelayRecord>? _relays;
    private bool _loaded;

    public RelayStore(IJSRuntime js, IEventBus bus)
    {
        _js = js;
        _bus = bus;
    }

    public IReadOnlyList<RelayRecord> Relays => _relays ?? [];
    public IReadOnlyList<RelayRecord> EnabledRelays => _relays?.Where(r => r.Enabled).ToList() ?? [];

    public RelayRecord? GetById(string id) => _relays?.FirstOrDefault(r => r.Id == id);
    public RelayRecord? GetByUrl(string url) => _relays?.FirstOrDefault(r => r.Url == url);
    public bool SetupRequired { get; private set; }

    public static string GetHttpBaseUrl(string wsUrl)
    {
        if (wsUrl.StartsWith("wss://")) return "https://" + wsUrl[6..];
        if (wsUrl.StartsWith("ws://")) return "http://" + wsUrl[5..];
        return wsUrl;
    }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var entries = await _js.InvokeAsync<RelayRecord[]>("ccRelayDb.getAll");
            _relays = entries?.ToList() ?? new();
        }
        catch
        {
            _relays = new();
        }

        // Backfill IDs for entries that don't have one
        foreach (var r in _relays.Where(r => string.IsNullOrEmpty(r.Id)))
        {
            r.Id = Guid.NewGuid().ToString("N")[..8];
            await _js.InvokeVoidAsync("ccRelayDb.put", r);
        }

        if (_relays.Count == 0)
        {
            SetupRequired = true;
            var entry = new RelayRecord { Id = Guid.NewGuid().ToString("N")[..8], Url = DefaultUrl, Name = DefaultName, Enabled = true };
            _relays.Add(entry);
            await _js.InvokeVoidAsync("ccRelayDb.put", entry);
        }
    }

    public async Task AddRelay(string name, string url)
    {
        url = url.TrimEnd('/');
        if (_relays!.Any(r => r.Url == url)) return;
        var entry = new RelayRecord { Id = Guid.NewGuid().ToString("N")[..8], Url = url, Name = name };
        _relays!.Add(entry);
        await _js.InvokeVoidAsync("ccRelayDb.put", entry);
        _bus.Publish(new RelayStoreChangedEvent());
    }

    public async Task RemoveRelay(string url)
    {
        _relays!.RemoveAll(r => r.Url == url);
        await _js.InvokeVoidAsync("ccRelayDb.remove", url);
        _bus.Publish(new RelayStoreChangedEvent());
    }

    public async Task SetEnabled(string url, bool enabled)
    {
        var entry = _relays?.FirstOrDefault(r => r.Url == url);
        if (entry is null) return;
        if (entry.Enabled == enabled) return;
        entry.Enabled = enabled;
        await _js.InvokeVoidAsync("ccRelayDb.put", entry);
        _bus.Publish(new RelayStoreChangedEvent());
    }

    public async Task UpdateRelayName(string url, string name)
    {
        var entry = _relays?.FirstOrDefault(r => r.Url == url);
        if (entry is not null)
        {
            entry.Name = name;
            await _js.InvokeVoidAsync("ccRelayDb.put", entry);
        }
    }

    public async Task UpdateRelay(string oldUrl, string newName, string newUrl)
    {
        newUrl = newUrl.TrimEnd('/');
        var entry = _relays?.FirstOrDefault(r => r.Url == oldUrl);
        if (entry is null) return;

        if (oldUrl != newUrl)
        {
            if (_relays!.Any(r => r.Url == newUrl)) return; // duplicate check
            await _js.InvokeVoidAsync("ccRelayDb.remove", oldUrl);
            entry.Url = newUrl;
        }

        entry.Name = newName;
        await _js.InvokeVoidAsync("ccRelayDb.put", entry);
        _bus.Publish(new RelayStoreChangedEvent());
    }
}
