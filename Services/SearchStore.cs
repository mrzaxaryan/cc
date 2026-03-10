using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace cc.Services;

public class SearchRecord
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Id { get; set; }
    [JsonPropertyName("agentUuid")] public string AgentUuid { get; set; } = "";
    [JsonPropertyName("agentName")] public string AgentName { get; set; } = "";
    [JsonPropertyName("rootPath")] public string RootPath { get; set; } = "";
    [JsonPropertyName("extensions")] public string Extensions { get; set; } = ""; // comma-separated e.g. ".txt,.pdf,.doc"
    [JsonPropertyName("status")] public string Status { get; set; } = SearchStatus.Pending;
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

public static class SearchStatus
{
    public const string Pending = "pending";
    public const string Scanning = "scanning";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public class SearchStore
{
    private readonly IJSRuntime _js;
    private List<SearchRecord> _cache = new();
    private bool _loaded;

    public readonly CtsManager Cts = new();

    public event Action? OnChanged;
    public event Action<string>? OnItemQueued; // fires with agentUuid

    public SearchStore(IJSRuntime js) => _js = js;

    public IReadOnlyList<SearchRecord> Searches => _cache;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var records = await _js.InvokeAsync<SearchRecord[]>("ccScanDb.getAll");
            _cache = records.ToList();
        }
        catch
        {
            _cache = new();
        }
    }

    public async Task<SearchRecord> AddAsync(string agentUuid, string agentName, string rootPath, string extensions, bool autoDownload)
    {
        var record = new SearchRecord
        {
            AgentUuid = agentUuid,
            AgentName = agentName,
            RootPath = rootPath,
            Extensions = extensions,
            AutoDownload = autoDownload,
            Status = SearchStatus.Scanning,
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

    public async Task UpdateAsync(SearchRecord record)
    {
        await _js.InvokeVoidAsync("ccScanDb.put", record);
        OnChanged?.Invoke();
    }

    public async Task PauseAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        record.Status = SearchStatus.Paused;
        await _js.InvokeVoidAsync("ccScanDb.put", record);
        OnChanged?.Invoke();
    }

    public async Task ResumeAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        record.Status = SearchStatus.Scanning;
        await _js.InvokeVoidAsync("ccScanDb.put", record);
        OnChanged?.Invoke();
        OnItemQueued?.Invoke(record.AgentUuid);
    }

    public async Task CompleteAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        record.Status = SearchStatus.Completed;
        record.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _js.InvokeVoidAsync("ccScanDb.put", record);
        OnChanged?.Invoke();
    }

    public async Task FailAsync(int id, string error)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        record.Status = SearchStatus.Failed;
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

    public List<SearchRecord> GetByAgent(string agentUuid) =>
        _cache.Where(r => r.AgentUuid == agentUuid).ToList();

    public List<SearchRecord> GetActive() =>
        _cache.Where(r => r.Status is SearchStatus.Scanning or SearchStatus.Paused).ToList();

    public bool HasActiveSearch(string agentUuid) =>
        _cache.Any(r => r.AgentUuid == agentUuid && r.Status == SearchStatus.Scanning);

    public SearchRecord? GetNextPending(string agentUuid) =>
        _cache.FirstOrDefault(r => r.AgentUuid == agentUuid && r.Status == SearchStatus.Scanning && r.PendingDirs.Count > 0);

}
