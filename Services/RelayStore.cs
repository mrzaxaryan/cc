using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace cc.Services;

public class RelayEntry
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("active")] public bool Active { get; set; }
}

public class RelayStore
{
    private const string DefaultUrl = "wss://relay.nostdlib.workers.dev";
    private const string DefaultName = "Default";

    private readonly IJSRuntime _js;
    private List<RelayEntry>? _relays;
    private bool _loaded;

    public event Action? OnChanged;

    public RelayStore(IJSRuntime js) => _js = js;

    public IReadOnlyList<RelayEntry> Relays => _relays ?? [];
    public string ActiveUrl => _relays?.FirstOrDefault(r => r.Active)?.Url ?? DefaultUrl;
    public bool SetupRequired { get; private set; }

    public string HttpBaseUrl
    {
        get
        {
            var url = ActiveUrl;
            if (url.StartsWith("wss://")) return "https://" + url[6..];
            if (url.StartsWith("ws://")) return "http://" + url[5..];
            return url;
        }
    }

    public string WsBaseUrl => ActiveUrl;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var entries = await _js.InvokeAsync<RelayEntry[]>("ccRelayDb.getAll");
            _relays = entries?.ToList() ?? new();
        }
        catch
        {
            _relays = new();
        }

        if (_relays.Count == 0)
        {
            SetupRequired = true;
            var entry = new RelayEntry { Url = DefaultUrl, Name = DefaultName, Active = true };
            _relays.Add(entry);
            await _js.InvokeVoidAsync("ccRelayDb.put", entry);
        }

        if (!_relays.Any(r => r.Active) && _relays.Count > 0)
        {
            _relays[0].Active = true;
            await _js.InvokeVoidAsync("ccRelayDb.put", _relays[0]);
        }
    }

    public async Task AddRelay(string name, string url)
    {
        url = url.TrimEnd('/');
        if (_relays!.Any(r => r.Url == url)) return;
        var entry = new RelayEntry { Url = url, Name = name };
        _relays!.Add(entry);
        await _js.InvokeVoidAsync("ccRelayDb.put", entry);
        OnChanged?.Invoke();
    }

    public async Task RemoveRelay(string url)
    {
        var wasActive = _relays!.FirstOrDefault(r => r.Url == url)?.Active ?? false;
        _relays!.RemoveAll(r => r.Url == url);
        await _js.InvokeVoidAsync("ccRelayDb.remove", url);

        if (wasActive && _relays.Count > 0)
        {
            _relays[0].Active = true;
            await _js.InvokeVoidAsync("ccRelayDb.put", _relays[0]);
        }
        OnChanged?.Invoke();
    }

    public async Task SetActive(string url)
    {
        foreach (var r in _relays!)
        {
            var wasActive = r.Active;
            r.Active = r.Url == url;
            if (r.Active != wasActive)
                await _js.InvokeVoidAsync("ccRelayDb.put", r);
        }
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
}
