using C2.Infrastructure;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace C2.Features.Transfers;

public class TransferRecord
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
    [JsonPropertyName("status")] public string Status { get; set; } = TransferStatus.Pending;
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("createdAt")] public double CreatedAt { get; set; }
    [JsonPropertyName("completedAt")] public double? CompletedAt { get; set; }
    [JsonPropertyName("priority")] public int Priority { get; set; }
    /// <summary>True when paused automatically (e.g. agent disconnect); false when paused by user.</summary>
    [JsonPropertyName("autoResume")] public bool AutoResume { get; set; }

    // Transient speed tracking (not persisted)
    [JsonIgnore] public double SpeedBytesPerSec { get; set; }
    [JsonIgnore] public long LastSpeedBytes { get; set; }
    [JsonIgnore] public DateTime LastSpeedTime { get; set; }
}

/// <summary>
/// Transfer lifecycle states: Pending → Queued → Downloading → Completed/Failed.
/// A transfer can be Paused from Downloading and resumed back to Queued.
/// </summary>
public static class TransferStatus
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

public class TransferStore
{
    private readonly IJSRuntime _js;
    private readonly IEventBus _bus;
    private List<TransferRecord> _cache = new();
    private bool _loaded;

    /// <summary>CTS (CancellationTokenSource) manager for cancelling in-flight transfer operations.</summary>
    public readonly CtsManager Cts = new();

    public TransferStore(IJSRuntime js, IEventBus bus)
    {
        _js = js;
        _bus = bus;
    }

    public IReadOnlyList<TransferRecord> Downloads => _cache;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var records = await _js.InvokeAsync<TransferRecord[]>("c2DownloadDb.getAll");
            _cache = records.ToList();
        }
        catch
        {
            _cache = new();
        }
    }

    public async Task<TransferRecord> AddAsync(string agentUuid, string agentName, string remotePath, string fileName, string cacheSubPath, long totalSize, int priority = 0)
    {
        var record = new TransferRecord
        {
            AgentUuid = agentUuid,
            AgentName = agentName,
            RemotePath = remotePath,
            FileName = fileName,
            CacheSubPath = cacheSubPath,
            TotalSize = totalSize,
            Status = TransferStatus.Queued,
            Priority = priority,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var id = await _js.InvokeAsync<int>("c2DownloadDb.put", record);
        record.Id = id;
        _cache.Add(record);
        _bus.Publish(new TransferStoreChangedEvent());
        _bus.Publish(new TransferItemQueuedEvent(record.AgentUuid));
        return record;
    }

    public async Task QueueAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = TransferStatus.Queued;
        record.AutoResume = false;
        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new TransferStoreChangedEvent());
        _bus.Publish(new TransferItemQueuedEvent(record.AgentUuid));
    }

    /// <summary>Reset a completed or failed record and re-queue it for upload.</summary>
    public async Task RequeueAsync(int id, string cacheSubPath, long totalSize)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.CacheSubPath = cacheSubPath;
        record.TotalSize = totalSize;
        record.DownloadedSize = 0;
        record.Status = TransferStatus.Queued;
        record.Error = null;
        record.CompletedAt = null;
        record.SpeedBytesPerSec = 0;
        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new TransferStoreChangedEvent());
        _bus.Publish(new TransferItemQueuedEvent(record.AgentUuid));
    }

    public async Task UpdateProgressAsync(int id, long downloadedSize)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.DownloadedSize = downloadedSize;
        record.Status = TransferStatus.Downloading;

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
        _bus.Publish(new TransferStoreChangedEvent());
    }

    public async Task CompleteAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = TransferStatus.Completed;
        record.DownloadedSize = record.TotalSize;
        record.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new TransferStoreChangedEvent());
    }

    public async Task PauseAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = TransferStatus.Paused;
        record.AutoResume = false;
        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new TransferStoreChangedEvent());
    }

    /// <summary>Pause with auto-resume flag so the transfer resumes when the agent reconnects.</summary>
    public async Task PauseForResumeAsync(int id)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = TransferStatus.Paused;
        record.AutoResume = true;
        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new TransferStoreChangedEvent());
    }

    public async Task FailAsync(int id, string error)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Status = TransferStatus.Failed;
        record.Error = error;
        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new TransferStoreChangedEvent());
    }

    public async Task RemoveAsync(int id)
    {
        _cache.RemoveAll(r => r.Id == id);
        await _js.InvokeVoidAsync("c2DownloadDb.remove", id);
        _bus.Publish(new TransferStoreChangedEvent());
    }

    public async Task ClearAsync()
    {
        _cache.Clear();
        await _js.InvokeVoidAsync("c2DownloadDb.clear");
        _bus.Publish(new TransferStoreChangedEvent());
    }

    public async Task SetPriorityAsync(int id, int priority)
    {
        var record = _cache.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.Priority = priority;
        await _js.InvokeVoidAsync("c2DownloadDb.put", record);
        _bus.Publish(new TransferStoreChangedEvent());
    }

    public bool IsCompleted(string agentUuid, string remotePath) =>
        _cache.Any(r => r.AgentUuid == agentUuid && r.RemotePath == remotePath && r.Status == TransferStatus.Completed);

    public bool IsDownloading(string agentUuid, string remotePath) =>
        _cache.Any(r => r.AgentUuid == agentUuid && r.RemotePath == remotePath && r.Status == TransferStatus.Downloading);

    public TransferRecord? Find(string agentUuid, string remotePath) =>
        _cache.FirstOrDefault(r => r.AgentUuid == agentUuid && r.RemotePath == remotePath);

    public List<TransferRecord> GetByAgent(string agentUuid) =>
        _cache.Where(r => r.AgentUuid == agentUuid).ToList();

    public List<TransferRecord> GetActive() =>
        _cache.Where(r => r.Status is TransferStatus.Downloading or TransferStatus.Paused or TransferStatus.Queued or TransferStatus.Pending)
              .ToList();

    /// <summary>True if agent has a file currently downloading.</summary>
    public bool HasActiveDownload(string agentUuid) =>
        _cache.Any(r => r.AgentUuid == agentUuid && r.Status == TransferStatus.Downloading);

    /// <summary>Get the next queued record for an agent, ordered by priority (lower first) then createdAt.</summary>
    public TransferRecord? GetNextQueued(string agentUuid) =>
        _cache.Where(r => r.AgentUuid == agentUuid && r.Status == TransferStatus.Queued)
              .OrderBy(r => r.Priority)
              .ThenBy(r => r.CreatedAt)
              .FirstOrDefault();

}
