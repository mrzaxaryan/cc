using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace cc.Services;

public class VfsDirectory
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("parentId")] public string ParentId { get; set; } = "";
    [JsonPropertyName("agentUuid")] public string AgentUuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("remotePath")] public string RemotePath { get; set; } = "";
    [JsonPropertyName("isDrive")] public bool IsDrive { get; set; }
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
    [JsonPropertyName("createdAt")] public double CreatedAt { get; set; }
}

/// <summary>Virtual filesystem backed by IndexedDB for metadata and .fs/{GUID} for blob storage.</summary>
public class VfsStore
{
    private readonly IJSRuntime _js;
    private readonly CacheManager _cache;

    /// <summary>Sentinel parentId for root-level entries (drives / top-level dirs).</summary>
    public const string RootParentId = "__root__";

    public event Action? OnChanged;

    public VfsStore(IJSRuntime js, CacheManager cache)
    {
        _js = js;
        _cache = cache;
    }

    // --- Directories ---

    public async Task<VfsDirectory> PutDirectoryAsync(string agentUuid, string parentId, string name, string remotePath, bool isDrive = false)
    {
        // Normalize drive names: "C:\" → "C:"
        name = name.TrimEnd('\\', '/');

        // Check if it already exists
        var existing = await FindDirectoryAsync(agentUuid, parentId, name);
        if (existing is not null) return existing;

        var dir = new VfsDirectory
        {
            Id = Guid.NewGuid().ToString("N"),
            ParentId = parentId,
            AgentUuid = agentUuid,
            Name = name,
            RemotePath = remotePath,
            IsDrive = isDrive,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await _js.InvokeVoidAsync("ccDirectoryDb.put", dir);
        OnChanged?.Invoke();
        return dir;
    }

    public async Task<VfsDirectory?> GetDirectoryAsync(string id)
    {
        return await _js.InvokeAsync<VfsDirectory?>("ccDirectoryDb.get", id);
    }

    public async Task<List<VfsDirectory>> GetChildDirectoriesAsync(string agentUuid, string parentId)
    {
        var dirs = await _js.InvokeAsync<VfsDirectory[]>("ccDirectoryDb.getByParent", agentUuid, parentId);
        return dirs?.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? new();
    }

    public async Task<VfsDirectory?> FindDirectoryAsync(string agentUuid, string parentId, string name)
    {
        var dirs = await _js.InvokeAsync<VfsDirectory[]>("ccDirectoryDb.getByParent", agentUuid, parentId);
        return dirs?.FirstOrDefault(d => string.Equals(d.Name.TrimEnd('\\', '/'), name.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolve a remote path like "C:\Users\foo" to the directory ID, creating parents as needed.</summary>
    public async Task<string> ResolveOrCreatePathAsync(string agentUuid, string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath)) return RootParentId;

        var segments = remotePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentParent = RootParentId;
        var pathSoFar = "";

        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            pathSoFar = i == 0 ? seg : $"{pathSoFar}/{seg}";
            var isDrive = i == 0 && seg.EndsWith(":");
            var dir = await PutDirectoryAsync(agentUuid, currentParent, seg, pathSoFar, isDrive);
            currentParent = dir.Id;
        }

        return currentParent;
    }

    /// <summary>Recursively remove a directory: deletes child files (with blobs), child dirs, then self.</summary>
    public async Task RemoveDirectoryAsync(string id)
    {
        var dir = await GetDirectoryAsync(id);
        if (dir is null) return;

        // Delete child files (blobs + DB)
        var files = await GetFilesInDirectoryAsync(dir.AgentUuid, id);
        foreach (var f in files)
            await RemoveFileAsync(f.Id, raiseEvent: false);

        // Recurse into child directories
        var childDirs = await GetChildDirectoriesAsync(dir.AgentUuid, id);
        foreach (var child in childDirs)
            await RemoveDirectoryAsync(child.Id);

        await _js.InvokeVoidAsync("ccDirectoryDb.remove", id);
        OnChanged?.Invoke();
    }

    /// <summary>Remove all VFS data and blobs for an agent.</summary>
    public async Task ClearAgentAsync(string agentUuid)
    {
        // Delete all blobs for this agent's files
        var files = await _js.InvokeAsync<VfsFile[]>("ccFileDb.getByAgent", agentUuid);
        if (files is not null)
        {
            foreach (var f in files)
            {
                try { await _cache.DeleteBlobAsync(f.Id); } catch { }
            }
        }

        await _js.InvokeVoidAsync("ccDirectoryDb.removeByAgent", agentUuid);
        await _js.InvokeVoidAsync("ccFileDb.removeByAgent", agentUuid);
        OnChanged?.Invoke();
    }

    // --- Files ---

    public async Task<VfsFile> PutFileAsync(string agentUuid, string directoryId, string name, string remotePath, long size)
    {
        // Check if it already exists
        var existing = await FindFileAsync(agentUuid, directoryId, name);
        if (existing is not null)
        {
            existing.Size = size;
            await _js.InvokeVoidAsync("ccFileDb.put", existing);
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
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await _js.InvokeVoidAsync("ccFileDb.put", file);
        OnChanged?.Invoke();
        return file;
    }

    public async Task<VfsFile?> GetFileAsync(string id)
    {
        return await _js.InvokeAsync<VfsFile?>("ccFileDb.get", id);
    }

    public async Task<List<VfsFile>> GetFilesInDirectoryAsync(string agentUuid, string directoryId)
    {
        var files = await _js.InvokeAsync<VfsFile[]>("ccFileDb.getByDirectory", agentUuid, directoryId);
        return files?.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? new();
    }

    public async Task<VfsFile?> FindFileAsync(string agentUuid, string directoryId, string name)
    {
        var files = await _js.InvokeAsync<VfsFile[]>("ccFileDb.getByDirectory", agentUuid, directoryId);
        return files?.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Remove a file: deletes blob from filesystem and metadata from IndexedDB.</summary>
    public async Task RemoveFileAsync(string id, bool raiseEvent = true)
    {
        try { await _cache.DeleteBlobAsync(id); } catch { }
        await _js.InvokeVoidAsync("ccFileDb.remove", id);
        if (raiseEvent) OnChanged?.Invoke();
    }
}
