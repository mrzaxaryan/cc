using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace cc.Services;

public class DownloadRecord
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Id { get; set; }
    [JsonPropertyName("agentUuid")] public string AgentUuid { get; set; } = "";
    [JsonPropertyName("agentName")] public string AgentName { get; set; } = "";
    [JsonPropertyName("remotePath")] public string RemotePath { get; set; } = "";
    [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
    [JsonPropertyName("cacheSubPath")] public string CacheSubPath { get; set; } = "";
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
    [JsonPropertyName("downloadedSize")] public long DownloadedSize { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = DownloadStatus.Pending;
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("createdAt")] public double CreatedAt { get; set; }
    [JsonPropertyName("completedAt")] public double? CompletedAt { get; set; }
    [JsonPropertyName("priority")] public int Priority { get; set; }

    // Transient speed tracking (not persisted)
    [JsonIgnore] public double SpeedBytesPerSec { get; set; }
    [JsonIgnore] public long LastSpeedBytes { get; set; }
    [JsonIgnore] public DateTime LastSpeedTime { get; set; }
}

public static class DownloadStatus
{
    public const string Pending = "pending";
    public const string Queued = "queued";
    public const string Downloading = "downloading";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public class DownloadStore
{
    private readonly IJSRuntime _js;
    private List<DownloadRecord> _cache = new();
    private bool _loaded;

    // Shared CTS tracking — keyed by record ID
    private readonly Dictionary<int, CancellationTokenSource> _activeCts = new();

    public event Action? OnChanged;
    public event Action<string>? OnItemQueued; // fires with agentUuid

    public DownloadStore(IJSRuntime js) => _js = js;

    public IReadOnlyList<DownloadRecord> Downloads => _cache;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var records = await _js.InvokeAsync<DownloadRecord[]>("ccDownloadDb.getAll");
            _cache = records.ToList();
        }
        catch
        {
            _cache = new();
        }
    }

    public async Task<DownloadRecord> AddAsync(string agentUuid, string agentName, string remotePath, string fileName, string cacheSubPath, long totalSize, int priority = 0)
    {
        var record = new DownloadRecord
        {
            AgentUuid = agentUuid,
            AgentName = agentName,
            RemotePath = remotePath,
            FileName = fileName,
            CacheSubPath = cacheSubPath,
            TotalSize = totalSize,
            Status = DownloadStatus.Queued,
            Priority = priority,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var id = await _js.InvokeAsync<int>("ccDownloadDb.put", record);
        record.Id = id;
        _cache.Add(record);
        OnChanged?.Invoke();
        OnItemQueued?.Invoke(record.AgentUuid);
        return record;
    }

    public async Task QueueAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = DownloadStatus.Queued;
        await _js.InvokeVoidAsync("ccDownloadDb.put", record);
        OnChanged?.Invoke();
        OnItemQueued?.Invoke(record.AgentUuid);
    }

    public async Task UpdateProgressAsync(int id, long downloadedSize)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.DownloadedSize = downloadedSize;
        record.Status = DownloadStatus.Downloading;

        // Calculate speed
        var now = DateTime.UtcNow;
        if (record.LastSpeedTime == default)
        {
            record.LastSpeedTime = now;
            record.LastSpeedBytes = downloadedSize;
        }
        else
        {
            var elapsed = (now - record.LastSpeedTime).TotalSeconds;
            if (elapsed >= 0.5)
            {
                var bytesDelta = downloadedSize - record.LastSpeedBytes;
                record.SpeedBytesPerSec = bytesDelta / elapsed;
                record.LastSpeedBytes = downloadedSize;
                record.LastSpeedTime = now;
            }
        }

        await _js.InvokeVoidAsync("ccDownloadDb.put", record);
        OnChanged?.Invoke();
    }

    public async Task CompleteAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = DownloadStatus.Completed;
        record.DownloadedSize = record.TotalSize;
        record.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _js.InvokeVoidAsync("ccDownloadDb.put", record);
        OnChanged?.Invoke();
    }

    public async Task PauseAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = DownloadStatus.Paused;
        await _js.InvokeVoidAsync("ccDownloadDb.put", record);
        OnChanged?.Invoke();
    }

    public async Task FailAsync(int id, string error)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = DownloadStatus.Failed;
        record.Error = error;
        await _js.InvokeVoidAsync("ccDownloadDb.put", record);
        OnChanged?.Invoke();
    }

    public async Task RemoveAsync(int id)
    {
        _cache.RemoveAll(r => r.Id == id);
        await _js.InvokeVoidAsync("ccDownloadDb.remove", id);
        OnChanged?.Invoke();
    }

    public async Task ClearAsync()
    {
        _cache.Clear();
        await _js.InvokeVoidAsync("ccDownloadDb.clear");
        OnChanged?.Invoke();
    }

    public async Task SetPriorityAsync(int id, int priority)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Priority = priority;
        await _js.InvokeVoidAsync("ccDownloadDb.put", record);
        OnChanged?.Invoke();
    }

    public bool IsCompleted(string agentUuid, string remotePath) =>
        _cache.Any(r => r.AgentUuid == agentUuid && r.RemotePath == remotePath && r.Status == DownloadStatus.Completed);

    public bool IsDownloading(string agentUuid, string remotePath) =>
        _cache.Any(r => r.AgentUuid == agentUuid && r.RemotePath == remotePath && r.Status == DownloadStatus.Downloading);

    public DownloadRecord? Find(string agentUuid, string remotePath) =>
        _cache.FirstOrDefault(r => r.AgentUuid == agentUuid && r.RemotePath == remotePath);

    public List<DownloadRecord> GetByAgent(string agentUuid) =>
        _cache.Where(r => r.AgentUuid == agentUuid).ToList();

    public List<DownloadRecord> GetActive() =>
        _cache.Where(r => r.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Queued)
              .ToList();

    /// <summary>True if agent has a file currently downloading.</summary>
    public bool HasActiveDownload(string agentUuid) =>
        _cache.Any(r => r.AgentUuid == agentUuid && r.Status == DownloadStatus.Downloading);

    /// <summary>Get the next queued record for an agent, ordered by priority (lower first) then createdAt.</summary>
    public DownloadRecord? GetNextQueued(string agentUuid) =>
        _cache.Where(r => r.AgentUuid == agentUuid && r.Status == DownloadStatus.Queued)
              .OrderBy(r => r.Priority)
              .ThenBy(r => r.CreatedAt)
              .FirstOrDefault();

    // --- Shared CTS management ---

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

    public bool IsCancelled(int recordId) =>
        _activeCts.TryGetValue(recordId, out var cts) && cts.IsCancellationRequested;

    public bool HasActiveCts(int recordId) => _activeCts.ContainsKey(recordId);

    public void CancelAll()
    {
        foreach (var cts in _activeCts.Values)
            cts.Cancel();
    }
}
