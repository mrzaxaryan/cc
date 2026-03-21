using System.Text.Json.Serialization;
using C2.Infrastructure;
using Microsoft.JSInterop;

namespace C2.Features.Loaders;

public class LoaderStore
{
    private readonly IJSRuntime _js;
    private readonly IEventBus _bus;
    private Dictionary<string, LoaderRecord> _loaders = new();
    private bool _loaded;

    public const string PythonKey = "python";
    public const string PowerShellKey = "powershell";

    public const string DefaultPythonUrl = "https://raw.githubusercontent.com/mrzaxaryan/Position-Independent-Agent/main/loaders/python/loader.py";
    public const string DefaultPowerShellUrl = "https://raw.githubusercontent.com/mrzaxaryan/Position-Independent-Agent/main/loaders/windows/powershell/loader.ps1";

    public const string PythonRepoUrl = "https://github.com/mrzaxaryan/Position-Independent-Agent/tree/main/loaders/python";
    public const string PowerShellRepoUrl = "https://github.com/mrzaxaryan/Position-Independent-Agent/tree/main/loaders/windows/powershell";

    public bool SetupRequired { get; private set; }

    public string PythonLoaderUrl => GetUrl(PythonKey) ?? DefaultPythonUrl;
    public string PowerShellLoaderUrl => GetUrl(PowerShellKey) ?? DefaultPowerShellUrl;

    public LoaderStore(IJSRuntime js, IEventBus bus)
    {
        _js = js;
        _bus = bus;
    }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var entries = await _js.InvokeAsync<LoaderRecord[]>("c2LoaderDb.getAll");
            _loaders = entries?.ToDictionary(e => e.Key) ?? new();
        }
        catch { _loaders = new(); }

        if (_loaders.Count == 0)
            SetupRequired = true;
    }

    public string? GetUrl(string key) =>
        _loaders.TryGetValue(key, out var r) ? r.Url : null;

    public async Task SetUrlAsync(string key, string url)
    {
        var record = new LoaderRecord { Key = key, Url = url };
        _loaders[key] = record;
        await _js.InvokeVoidAsync("c2LoaderDb.put", record);
        SetupRequired = false;
        _bus.Publish(new LoaderStoreChangedEvent());
    }

    public async Task SaveDefaultsAsync()
    {
        await SetUrlAsync(PythonKey, DefaultPythonUrl);
        await SetUrlAsync(PowerShellKey, DefaultPowerShellUrl);
    }
}

public class LoaderRecord
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}
