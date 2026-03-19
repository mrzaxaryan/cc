using C2.Infrastructure;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace C2.Features.Scan;

public class ScanRecord
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Id { get; set; }
    [JsonPropertyName("agentUuid")] public string AgentUuid { get; set; } = "";
    [JsonPropertyName("agentName")] public string AgentName { get; set; } = "";
    [JsonPropertyName("rootPath")] public string RootPath { get; set; } = "";
    [JsonPropertyName("extensions")] public string Extensions { get; set; } = ""; // comma-separated e.g. ".txt,.pdf,.doc"
    [JsonPropertyName("status")] public string Status { get; set; } = ScanStatus.Pending;
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("dirsScanned")] public int DirsScanned { get; set; }
    [JsonPropertyName("dirsTotal")] public int DirsTotal { get; set; }
    [JsonPropertyName("filesFound")] public int FilesFound { get; set; }
    [JsonPropertyName("filesQueued")] public int FilesQueued { get; set; }
    [JsonPropertyName("recursive")] public bool Recursive { get; set; } = true;
    /// <summary>JSON-serialized array of directory paths still to be scanned (e.g. ["C:/Users","C:/Temp"]).</summary>
    [JsonPropertyName("pendingDirs")] public string PendingDirsJson { get; set; } = "[]";
    [JsonPropertyName("createdAt")] public double CreatedAt { get; set; }
    [JsonPropertyName("completedAt")] public double? CompletedAt { get; set; }

    [JsonIgnore]
    public List<string> PendingDirs
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<string>>(PendingDirsJson) ?? new();
        set => PendingDirsJson = System.Text.Json.JsonSerializer.Serialize(value);
    }
}

/// <summary>
/// Scan lifecycle states: Pending → Scanning → Completed/Failed.
/// A scan can be Paused from Scanning and resumed back.
/// </summary>
public static class ScanStatus
{
    /// <summary>Newly created, not yet started.</summary>
    public const string Pending = "pending";
    /// <summary>Actively scanning directories on the agent.</summary>
    public const string Scanning = "scanning";
    /// <summary>Temporarily suspended by the user; can be resumed.</summary>
    public const string Paused = "paused";
    /// <summary>All directories scanned successfully.</summary>
    public const string Completed = "completed";
    /// <summary>Scan terminated due to an error.</summary>
    public const string Failed = "failed";
}

public class ScanStore
{
    private readonly IJSRuntime _js;
    private readonly IEventBus _bus;
    private List<ScanRecord> _cache = new();
    private bool _loaded;

    /// <summary>CTS (CancellationTokenSource) manager for cancelling in-flight scan operations.</summary>
    public readonly CtsManager Cts = new();

    public ScanStore(IJSRuntime js, IEventBus bus)
    {
        _js = js;
        _bus = bus;
    }

    public IReadOnlyList<ScanRecord> Searches => _cache;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var records = await _js.InvokeAsync<ScanRecord[]>("c2ScanDb.getAll");
            _cache = records.ToList();
        }
        catch
        {
            _cache = new();
        }
    }

    public async Task<ScanRecord> AddAsync(string agentUuid, string agentName, string rootPath, string extensions, bool recursive = true)
    {
        var record = new ScanRecord
        {
            AgentUuid = agentUuid,
            AgentName = agentName,
            RootPath = rootPath,
            Extensions = extensions,
            Recursive = recursive,
            Status = ScanStatus.Scanning,
            PendingDirs = new List<string> { rootPath },
            DirsTotal = 1,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var id = await _js.InvokeAsync<int>("c2ScanDb.put", record);
        record.Id = id;
        _cache.Add(record);
        _bus.Publish(new ScanStoreChangedEvent());
        _bus.Publish(new ScanItemQueuedEvent(record.AgentUuid));
        return record;
    }

    public async Task UpdateAsync(ScanRecord record)
    {
        await _js.InvokeVoidAsync("c2ScanDb.put", record);
        _bus.Publish(new ScanStoreChangedEvent());
    }

    public async Task PauseAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        record.Status = ScanStatus.Paused;
        await _js.InvokeVoidAsync("c2ScanDb.put", record);
        _bus.Publish(new ScanStoreChangedEvent());
    }

    public async Task ResumeAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        record.Status = ScanStatus.Scanning;
        await _js.InvokeVoidAsync("c2ScanDb.put", record);
        _bus.Publish(new ScanStoreChangedEvent());
        _bus.Publish(new ScanItemQueuedEvent(record.AgentUuid));
    }

    public async Task CompleteAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        record.Status = ScanStatus.Completed;
        record.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _js.InvokeVoidAsync("c2ScanDb.put", record);
        _bus.Publish(new ScanStoreChangedEvent());
    }

    public async Task FailAsync(int id, string error)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        record.Status = ScanStatus.Failed;
        record.Error = error;
        await _js.InvokeVoidAsync("c2ScanDb.put", record);
        _bus.Publish(new ScanStoreChangedEvent());
    }

    public async Task RemoveAsync(int id)
    {
        _cache.RemoveAll(r => r.Id == id);
        await _js.InvokeVoidAsync("c2ScanDb.remove", id);
        _bus.Publish(new ScanStoreChangedEvent());
    }

    public List<ScanRecord> GetByAgent(string agentUuid) =>
        _cache.Where(r => r.AgentUuid == agentUuid).ToList();

    public List<ScanRecord> GetActive() =>
        _cache.Where(r => r.Status is ScanStatus.Scanning or ScanStatus.Paused).ToList();

    public bool HasActiveSearch(string agentUuid) =>
        _cache.Any(r => r.AgentUuid == agentUuid && r.Status == ScanStatus.Scanning);

    public ScanRecord? GetNextPending(string agentUuid) =>
        _cache.FirstOrDefault(r => r.AgentUuid == agentUuid && r.Status == ScanStatus.Scanning && r.PendingDirs.Count > 0);

}
