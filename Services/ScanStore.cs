using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace cc.Services;

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
    [JsonPropertyName("autoDownload")] public bool AutoDownload { get; set; }
    [JsonPropertyName("pendingDirs")] public string PendingDirsJson { get; set; } = "[]"; // JSON array of dirs still to scan
    [JsonPropertyName("createdAt")] public double CreatedAt { get; set; }
    [JsonPropertyName("completedAt")] public double? CompletedAt { get; set; }

    [JsonIgnore]
    public List<string> PendingDirs
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<string>>(PendingDirsJson) ?? new();
        set => PendingDirsJson = System.Text.Json.JsonSerializer.Serialize(value);
    }
}

public static class ScanStatus
{
    public const string Pending = "pending";
    public const string Scanning = "scanning";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public class ScanStore
{
    private readonly IJSRuntime _js;
    private List<ScanRecord> _cache = new();
    private bool _loaded;

    private readonly Dictionary<int, CancellationTokenSource> _activeCts = new();

    public event Action? OnChanged;
    public event Action<string>? OnItemQueued; // fires with agentUuid

    public ScanStore(IJSRuntime js) => _js = js;

    public IReadOnlyList<ScanRecord> Scans => _cache;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var records = await _js.InvokeAsync<ScanRecord[]>("ccScanDb.getAll");
            _cache = records.ToList();
        }
        catch
        {
            _cache = new();
        }
    }

    public async Task<ScanRecord> AddAsync(string agentUuid, string agentName, string rootPath, string extensions, bool autoDownload)
    {
        var record = new ScanRecord
        {
            AgentUuid = agentUuid,
            AgentName = agentName,
            RootPath = rootPath,
            Extensions = extensions,
            AutoDownload = autoDownload,
            Status = ScanStatus.Scanning,
            PendingDirs = new List<string> { rootPath },
            DirsTotal = 1,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var id = await _js.InvokeAsync<int>("ccScanDb.put", record);
        record.Id = id;
        _cache.Add(record);
        OnChanged?.Invoke();
        OnItemQueued?.Invoke(record.AgentUuid);
        return record;
    }

    public async Task UpdateAsync(ScanRecord record)
    {
        await _js.InvokeVoidAsync("ccScanDb.put", record);
        OnChanged?.Invoke();
    }

    public async Task PauseAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        record.Status = ScanStatus.Paused;
        await _js.InvokeVoidAsync("ccScanDb.put", record);
        OnChanged?.Invoke();
    }

    public async Task ResumeAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        record.Status = ScanStatus.Scanning;
        await _js.InvokeVoidAsync("ccScanDb.put", record);
        OnChanged?.Invoke();
        OnItemQueued?.Invoke(record.AgentUuid);
    }

    public async Task CompleteAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        record.Status = ScanStatus.Completed;
        record.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _js.InvokeVoidAsync("ccScanDb.put", record);
        OnChanged?.Invoke();
    }

    public async Task FailAsync(int id, string error)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        record.Status = ScanStatus.Failed;
        record.Error = error;
        await _js.InvokeVoidAsync("ccScanDb.put", record);
        OnChanged?.Invoke();
    }

    public async Task RemoveAsync(int id)
    {
        _cache.RemoveAll(r => r.Id == id);
        await _js.InvokeVoidAsync("ccScanDb.remove", id);
        OnChanged?.Invoke();
    }

    public List<ScanRecord> GetByAgent(string agentUuid) =>
        _cache.Where(r => r.AgentUuid == agentUuid).ToList();

    public List<ScanRecord> GetActive() =>
        _cache.Where(r => r.Status is ScanStatus.Scanning or ScanStatus.Paused).ToList();

    public bool HasActiveScan(string agentUuid) =>
        _cache.Any(r => r.AgentUuid == agentUuid && r.Status == ScanStatus.Scanning);

    public ScanRecord? GetNextPending(string agentUuid) =>
        _cache.FirstOrDefault(r => r.AgentUuid == agentUuid && r.Status == ScanStatus.Scanning && r.PendingDirs.Count > 0);

    // --- CTS management ---

    public CancellationTokenSource RegisterCts(int recordId)
    {
        var cts = new CancellationTokenSource();
        _activeCts[recordId] = cts;
        return cts;
    }

    public void CancelCts(int recordId)
    {
        if (_activeCts.TryGetValue(recordId, out var cts))
            cts.Cancel();
    }

    public void RemoveCts(int recordId)
    {
        if (_activeCts.Remove(recordId, out var cts))
            cts.Dispose();
    }

    public bool HasActiveCts(int recordId) => _activeCts.ContainsKey(recordId);

    public void CancelAll()
    {
        foreach (var cts in _activeCts.Values)
            cts.Cancel();
    }
}
