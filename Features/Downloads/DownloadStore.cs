using C2.Infrastructure;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace C2.Features.Downloads;

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

/// <summary>
/// Download lifecycle states: Pending → Queued → Downloading → Completed/Failed.
/// A download can be Paused from Downloading and resumed back to Queued.
/// </summary>
public static class DownloadStatus
{
    /// <summary>Newly created, not yet queued for processing.</summary>
    public const string Pending = "pending";
    /// <summary>Waiting in the download queue for an available slot.</summary>
    public const string Queued = "queued";
    /// <summary>Actively transferring data from the agent.</summary>
    public const string Downloading = "downloading";
    /// <summary>Temporarily suspended by the user; can be resumed.</summary>
    public const string Paused = "paused";
    /// <summary>Transfer finished successfully.</summary>
    public const string Completed = "completed";
    /// <summary>Transfer terminated due to an error.</summary>
    public const string Failed = "failed";
}

public class DownloadStore
{
    private readonly IJSRuntime _js;
    private readonly IEventBus _bus;
    private List<DownloadRecord> _cache = new();
    private bool _loaded;

    /// <summary>CTS (CancellationTokenSource) manager for cancelling in-flight download operations.</summary>
    public readonly CtsManager Cts = new();

    public DownloadStore(IJSRuntime js, IEventBus bus)
    {
        _js = js;
        _bus = bus;
    }

    public IReadOnlyList<DownloadRecord> Downloads => _cache;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var records = await _js.InvokeAsync<DownloadRecord[]>("c2DownloadDb.getAll");
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

        var id = await _js.InvokeAsync<int>("c2DownloadDb.put", record);
        record.Id = id;
        _cache.Add(record);
        _bus.Publish(new DownloadStoreChangedEvent());
        _bus.Publish(new DownloadItemQueuedEvent(record.AgentUuid));
        return record;
    }

    public async Task QueueAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = DownloadStatus.Queued;
        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new DownloadStoreChangedEvent());
        _bus.Publish(new DownloadItemQueuedEvent(record.AgentUuid));
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

        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new DownloadStoreChangedEvent());
    }

    public async Task CompleteAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = DownloadStatus.Completed;
        record.DownloadedSize = record.TotalSize;
        record.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new DownloadStoreChangedEvent());
    }

    public async Task PauseAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = DownloadStatus.Paused;
        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new DownloadStoreChangedEvent());
    }

    public async Task FailAsync(int id, string error)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = DownloadStatus.Failed;
        record.Error = error;
        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new DownloadStoreChangedEvent());
    }

    public async Task RemoveAsync(int id)
    {
        _cache.RemoveAll(r => r.Id == id);
        await _js.InvokeVoidAsync("c2DownloadDb.remove", id);
        _bus.Publish(new DownloadStoreChangedEvent());
    }

    public async Task ClearAsync()
    {
        _cache.Clear();
        await _js.InvokeVoidAsync("c2DownloadDb.clear");
        _bus.Publish(new DownloadStoreChangedEvent());
    }

    public async Task SetPriorityAsync(int id, int priority)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Priority = priority;
        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new DownloadStoreChangedEvent());
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

}
