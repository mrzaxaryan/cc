using Microsoft.JSInterop;
using System.Text.Json;

namespace cc.Services;

public class RelayEntry
{
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
}

public class RelayStore
{
    private const string StorageKey = "relay_list";
    private const string ActiveKey = "relay_active";
    private const string DefaultUrl = "wss://relay.nostdlib.workers.dev";
    private const string DefaultName = "Default";

    private readonly IJSRuntime _js;
    private List<RelayEntry>? _relays;
    private string? _activeUrl;

    public RelayStore(IJSRuntime js) => _js = js;

    public IReadOnlyList<RelayEntry> Relays => _relays ?? [];
    public string ActiveUrl => _activeUrl ?? DefaultUrl;
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
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            _relays = json is not null
                ? JsonSerializer.Deserialize<List<RelayEntry>>(json) ?? new()
                : new();

            _activeUrl = await _js.InvokeAsync<string?>("localStorage.getItem", ActiveKey);
        }
        catch
        {
            _relays = new();
            _activeUrl = null;
        }

        if (_relays.Count == 0)
        {
            SetupRequired = true;
            _relays.Add(new RelayEntry { Url = DefaultUrl, Name = DefaultName });
            _activeUrl = DefaultUrl;
            await SaveAsync();
            await _js.InvokeVoidAsync("localStorage.setItem", ActiveKey, _activeUrl);
        }

        _activeUrl ??= DefaultUrl;
    }

    public async Task AddRelay(string name, string url)
    {
        url = url.TrimEnd('/');
        if (_relays!.Any(r => r.Url == url)) return;
        _relays!.Add(new RelayEntry { Url = url, Name = name });
        await SaveAsync();
    }

    public async Task RemoveRelay(string url)
    {
        _relays!.RemoveAll(r => r.Url == url);
        if (_activeUrl == url)
        {
            _activeUrl = _relays.FirstOrDefault()?.Url ?? DefaultUrl;
            await _js.InvokeVoidAsync("localStorage.setItem", ActiveKey, _activeUrl);
        }
        await SaveAsync();
    }

    public async Task SetActive(string url)
    {
        _activeUrl = url;
        await _js.InvokeVoidAsync("localStorage.setItem", ActiveKey, url);
    }

    private async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_relays);
        await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
    }
}
