using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace cc.Infrastructure;

public class ServiceStateRecord
{
    [JsonPropertyName("key")] public string Key { get; set; } = ""; // e.g. "search:global", "upload:agent:{uuid}"
    [JsonPropertyName("service")] public string Service { get; set; } = ""; // "search" or "upload"
    [JsonPropertyName("scope")] public string Scope { get; set; } = ""; // "global" or agent UUID
    [JsonPropertyName("status")] public string Status { get; set; } = ServiceStatus.Running;
    [JsonPropertyName("updatedAt")] public double UpdatedAt { get; set; }
}

public static class ServiceStatus
{
    public const string Running = "running";
    public const string Paused = "paused";
}

public static class ServiceName
{
    public const string Search = "search";
    public const string Upload = "upload";
}

public class ServiceStateStore
{
    private readonly IJSRuntime _js;
    private readonly Dictionary<string, ServiceStateRecord> _cache = new();
    private bool _loaded;

    public event Action? OnChanged;

    public ServiceStateStore(IJSRuntime js) => _js = js;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var records = await _js.InvokeAsync<ServiceStateRecord[]>("ccServiceDb.getAll");
            foreach (var r in records)
                _cache[r.Key] = r;
        }
        catch
        {
            _cache.Clear();
        }
    }

    private static string MakeKey(string service, string scope) => $"{service}:{scope}";

    public bool IsPaused(string service, string scope)
    {
        var key = MakeKey(service, scope);
        return _cache.TryGetValue(key, out var record) && record.Status == ServiceStatus.Paused;
    }

    /// <summary>Check if service is paused globally OR for a specific agent.</summary>
    public bool IsEffectivelyPaused(string service, string agentUuid)
    {
        return IsPaused(service, "global") || IsPaused(service, agentUuid);
    }

    public string GetStatus(string service, string scope)
    {
        var key = MakeKey(service, scope);
        return _cache.TryGetValue(key, out var record) ? record.Status : ServiceStatus.Running;
    }

    public async Task SetStatusAsync(string service, string scope, string status)
    {
        var key = MakeKey(service, scope);
        var record = new ServiceStateRecord
        {
            Key = key,
            Service = service,
            Scope = scope,
            Status = status,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        _cache[key] = record;
        await _js.InvokeVoidAsync("ccServiceDb.put", record);
        OnChanged?.Invoke();
    }

    public async Task PauseAsync(string service, string scope)
        => await SetStatusAsync(service, scope, ServiceStatus.Paused);

    public async Task ResumeAsync(string service, string scope)
        => await SetStatusAsync(service, scope, ServiceStatus.Running);

    public List<ServiceStateRecord> GetAll() => _cache.Values.ToList();

    public List<ServiceStateRecord> GetByService(string service)
        => _cache.Values.Where(r => r.Service == service).ToList();
}
