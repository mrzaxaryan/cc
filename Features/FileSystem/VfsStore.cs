using System.Text.Json.Serialization;
using C2.Features.Storage;
using C2.Infrastructure;
using Microsoft.JSInterop;

namespace C2.Features.FileSystem;

public class VfsDirectory
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("parentId")] public string ParentId { get; set; } = "";
    [JsonPropertyName("agentUuid")] public string AgentUuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("remotePath")] public string RemotePath { get; set; } = "";
    [JsonPropertyName("isDrive")] public bool IsDrive { get; set; }
    [JsonPropertyName("creationTime")] public ulong CreationTime { get; set; }
    [JsonPropertyName("lastModifiedTime")] public ulong LastModifiedTime { get; set; }
    [JsonPropertyName("createdAt")] public double CreatedAt { get; set; }
}

public class VfsFile
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("directoryId")] public string DirectoryId { get; set; } = "";
    [JsonPropertyName("agentUuid")] public string AgentUuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("remotePath")] public string RemotePath { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("creationTime")] public ulong CreationTime { get; set; }
    [JsonPropertyName("lastModifiedTime")] public ulong LastModifiedTime { get; set; }
    [JsonPropertyName("createdAt")] public double CreatedAt { get; set; }
}

/// <summary>Virtual filesystem backed by IndexedDB for metadata and .fs/{GUID} for blob storage.</summary>
public class VfsStore
{
    private readonly IJSRuntime _js;
    private readonly CacheManager _cache;
    private readonly IEventBus _bus;

    /// <summary>Sentinel parentId for root-level entries (drives / top-level dirs).</summary>
    public const string RootParentId = "__root__";

    public VfsStore(IJSRuntime js, CacheManager cache, IEventBus bus)
    {
        _js = js;
        _cache = cache;
        _bus = bus;
    }

    // --- Directories ---

    public async Task<VfsDirectory> PutDirectoryAsync(string agentUuid, string parentId, string name, string remotePath, bool isDrive = false, ulong creationTime = 0, ulong lastModifiedTime = 0)
    {
        name = name.TrimEnd('\\', '/');

        var existing = await FindDirectoryAsync(agentUuid, parentId, name);
        if (existing is not null)
        {
            if (creationTime != 0 || lastModifiedTime != 0)
            {
                existing.CreationTime = creationTime;
                existing.LastModifiedTime = lastModifiedTime;
                await _js.InvokeVoidAsync("c2DirectoryDb.put", existing);
            }
            return existing;
        }

        var dir = new VfsDirectory
        {
            Id = Guid.NewGuid().ToString("N"),
            ParentId = parentId,
            AgentUuid = agentUuid,
            Name = name,
            RemotePath = remotePath,
            IsDrive = isDrive,
            CreationTime = creationTime,
            LastModifiedTime = lastModifiedTime,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await _js.InvokeVoidAsync("c2DirectoryDb.put", dir);
        _bus.Publish(new VfsChangedEvent());
        return dir;
    }

    public async Task<VfsDirectory?> GetDirectoryAsync(string id)
    {
        return await _js.InvokeAsync<VfsDirectory?>("c2DirectoryDb.get", id);
    }

    public async Task<List<VfsDirectory>> GetChildDirectoriesAsync(string agentUuid, string parentId)
    {
        var dirs = await _js.InvokeAsync<VfsDirectory[]>("c2DirectoryDb.getByParent", agentUuid, parentId);
        return dirs?.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? new();
    }

    public async Task<VfsDirectory?> FindDirectoryAsync(string agentUuid, string parentId, string name)
    {
        var dirs = await _js.InvokeAsync<VfsDirectory[]>("c2DirectoryDb.getByParent", agentUuid, parentId);
        return dirs?.FirstOrDefault(d => string.Equals(d.Name.TrimEnd('\\', '/'), name.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Read-only lookup: returns the directory ID for a remote path, or null if not cached.</summary>
    public async Task<string?> FindDirectoryIdAsync(string agentUuid, string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath)) return RootParentId;

        var normalized = remotePath.Replace('\\', '/');
        var isUnixRoot = normalized.StartsWith('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentParent = RootParentId;

        if (isUnixRoot)
        {
            var rootDir = await FindDirectoryAsync(agentUuid, RootParentId, "/");
            if (rootDir is null) return null;
            currentParent = rootDir.Id;
        }

        for (int i = 0; i < segments.Length; i++)
        {
            var dir = await FindDirectoryAsync(agentUuid, currentParent, segments[i]);
            if (dir is null) return null;
            currentParent = dir.Id;
        }

        return currentParent;
    }

    public async Task<string> ResolveOrCreatePathAsync(string agentUuid, string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath)) return RootParentId;

        var normalized = remotePath.Replace('\\', '/');
        var isUnixRoot = normalized.StartsWith('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentParent = RootParentId;

        // Unix/Android paths start with "/" — create a root drive entry
        if (isUnixRoot)
        {
            var rootDir = await PutDirectoryAsync(agentUuid, RootParentId, "/", "/", isDrive: true);
            currentParent = rootDir.Id;
        }

        var pathSoFar = isUnixRoot ? "" : "";

        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            pathSoFar = isUnixRoot
                ? $"/{string.Join('/', segments.Take(i + 1))}"
                : (i == 0 ? seg : $"{pathSoFar}/{seg}");
            var isDrive = !isUnixRoot && i == 0 && seg.EndsWith(":");
            var dir = await PutDirectoryAsync(agentUuid, currentParent, seg, pathSoFar, isDrive);
            currentParent = dir.Id;
        }

        return currentParent;
    }

    public async Task RemoveDirectoryAsync(string id)
    {
        var dir = await GetDirectoryAsync(id);
        if (dir is null) return;

        var files = await GetFilesInDirectoryAsync(dir.AgentUuid, id);
        foreach (var f in files)
            await RemoveFileAsync(f.Id, raiseEvent: false);

        var childDirs = await GetChildDirectoriesAsync(dir.AgentUuid, id);
        foreach (var child in childDirs)
            await RemoveDirectoryAsync(child.Id);

        await _js.InvokeVoidAsync("c2DirectoryDb.remove", id);
        _bus.Publish(new VfsChangedEvent());
    }

    public async Task ClearAgentAsync(string agentUuid)
    {
        var files = await _js.InvokeAsync<VfsFile[]>("c2FileDb.getByAgent", agentUuid);
        if (files is not null)
        {
            foreach (var f in files)
            {
                try { await _cache.DeleteBlobAsync(f.Id); } catch { }
            }
        }

        await _js.InvokeVoidAsync("c2DirectoryDb.removeByAgent", agentUuid);
        await _js.InvokeVoidAsync("c2FileDb.removeByAgent", agentUuid);
        _bus.Publish(new VfsChangedEvent());
    }

    // --- Files ---

    public async Task<VfsFile> PutFileAsync(string agentUuid, string directoryId, string name, string remotePath, long size, ulong creationTime = 0, ulong lastModifiedTime = 0)
    {
        var existing = await FindFileAsync(agentUuid, directoryId, name);
        if (existing is not null)
        {
            existing.Size = size;
            existing.CreationTime = creationTime;
            existing.LastModifiedTime = lastModifiedTime;
            await _js.InvokeVoidAsync("c2FileDb.put", existing);
            return existing;
        }

        var file = new VfsFile
        {
            Id = Guid.NewGuid().ToString("N"),
            DirectoryId = directoryId,
            AgentUuid = agentUuid,
            Name = name,
            RemotePath = remotePath,
            Size = size,
            CreationTime = creationTime,
            LastModifiedTime = lastModifiedTime,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await _js.InvokeVoidAsync("c2FileDb.put", file);
        _bus.Publish(new VfsChangedEvent());
        return file;
    }

    public async Task<VfsFile?> GetFileAsync(string id)
    {
        return await _js.InvokeAsync<VfsFile?>("c2FileDb.get", id);
    }

    public async Task<List<VfsFile>> GetFilesInDirectoryAsync(string agentUuid, string directoryId)
    {
        var files = await _js.InvokeAsync<VfsFile[]>("c2FileDb.getByDirectory", agentUuid, directoryId);
        return files?.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? new();
    }

    public async Task<VfsFile?> FindFileAsync(string agentUuid, string directoryId, string name)
    {
        var files = await _js.InvokeAsync<VfsFile[]>("c2FileDb.getByDirectory", agentUuid, directoryId);
        return files?.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task RemoveFileAsync(string id, bool raiseEvent = true)
    {
        try { await _cache.DeleteBlobAsync(id); } catch { }
        await _js.InvokeVoidAsync("c2FileDb.remove", id);
        if (raiseEvent) _bus.Publish(new VfsChangedEvent());
    }

    // --- Stats ---

    public async Task<(int dirCount, int fileCount, long totalSize)> GetAgentStatsAsync(string agentUuid)
    {
        var dirs = await _js.InvokeAsync<VfsDirectory[]?>("c2DirectoryDb.getByAgent", agentUuid);
        var files = await _js.InvokeAsync<VfsFile[]?>("c2FileDb.getByAgent", agentUuid);
        var dirCount = dirs?.Length ?? 0;
        var fileCount = files?.Length ?? 0;
        var totalSize = files?.Sum(f => f.Size) ?? 0;
        return (dirCount, fileCount, totalSize);
    }

    public async Task<(int agentCount, int dirCount, int fileCount, long totalSize)> GetGlobalStatsAsync(IEnumerable<string> agentUuids)
    {
        int agents = 0, dirs = 0, files = 0;
        long size = 0;
        foreach (var uuid in agentUuids)
        {
            var (d, f, s) = await GetAgentStatsAsync(uuid);
            if (d > 0 || f > 0)
            {
                agents++;
                dirs += d;
                files += f;
                size += s;
            }
        }
        return (agents, dirs, files, size);
    }
}
