using System.Text.Json.Serialization;
using C2.Infrastructure;
using Microsoft.JSInterop;

namespace C2.Features.Relay;

public class RelayRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("active")] public bool Enabled { get; set; }
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

public class RelayStore
{
    public const string DefaultUrl = "wss://relay.nostdlib.workers.dev";
    public const string DefaultName = "Default";
    public const string DefaultToken = "*";

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

    public static string GetWsBaseUrl(string httpUrl)
    {
        if (httpUrl.StartsWith("https://")) return "wss://" + httpUrl[8..];
        if (httpUrl.StartsWith("http://")) return "ws://" + httpUrl[7..];
        return httpUrl;
    }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var entries = await _js.InvokeAsync<RelayRecord[]>("c2RelayDb.getAll");
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
            await _js.InvokeVoidAsync("c2RelayDb.put", r);
        }

        if (_relays.Count == 0)
        {
            SetupRequired = true;
        }
    }

    public async Task AddRelay(string name, string url, string token = "")
    {
        url = url.TrimEnd('/');
        if (_relays!.Any(r => r.Url == url)) return;
        var entry = new RelayRecord { Id = Guid.NewGuid().ToString("N")[..8], Url = url, Name = name, Token = token };
        _relays!.Add(entry);
        await _js.InvokeVoidAsync("c2RelayDb.put", entry);
        _bus.Publish(new RelayStoreChangedEvent());
    }

    public async Task RemoveRelay(string url)
    {
        _relays!.RemoveAll(r => r.Url == url);
        await _js.InvokeVoidAsync("c2RelayDb.remove", url);
        _bus.Publish(new RelayStoreChangedEvent());
    }

    public async Task SetEnabled(string url, bool enabled)
    {
        var entry = _relays?.FirstOrDefault(r => r.Url == url);
        if (entry is null) return;
        if (entry.Enabled == enabled) return;
        entry.Enabled = enabled;
        await _js.InvokeVoidAsync("c2RelayDb.put", entry);
        _bus.Publish(new RelayStoreChangedEvent());
    }

    public async Task UpdateRelayName(string url, string name)
    {
        var entry = _relays?.FirstOrDefault(r => r.Url == url);
        if (entry is not null)
        {
            entry.Name = name;
            await _js.InvokeVoidAsync("c2RelayDb.put", entry);
        }
    }

    public async Task UpdateRelay(string oldUrl, string newName, string newUrl, string token = "")
    {
        newUrl = newUrl.TrimEnd('/');
        var entry = _relays?.FirstOrDefault(r => r.Url == oldUrl);
        if (entry is null) return;

        if (oldUrl != newUrl)
        {
            if (_relays!.Any(r => r.Url == newUrl)) return; // duplicate check
            await _js.InvokeVoidAsync("c2RelayDb.remove", oldUrl);
            entry.Url = newUrl;
        }

        entry.Name = newName;
        entry.Token = token;
        await _js.InvokeVoidAsync("c2RelayDb.put", entry);
        _bus.Publish(new RelayStoreChangedEvent());
    }

    public string GetTokenByUrl(string url) => _relays?.FirstOrDefault(r => r.Url == url)?.Token ?? "";
}
