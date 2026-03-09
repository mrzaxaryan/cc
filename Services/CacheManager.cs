using Microsoft.JSInterop;

namespace cc.Services;

public class CacheManager
{
    private readonly IJSRuntime _js;

    public bool IsSupported { get; private set; }
    public bool HasDirectory { get; private set; }
    public bool NeedsPermission { get; private set; }
    public string? DirectoryName { get; private set; }
    public bool SetupRequired => IsSupported && !HasDirectory;

    public event Action? OnChanged;

    public CacheManager(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>Initialize: check support and try to restore persisted handle (no user gesture needed).</summary>
    public async Task InitializeAsync()
    {
        IsSupported = await _js.InvokeAsync<bool>("ccFileSystem.isSupported");
        if (!IsSupported) return;

        var hasPersisted = await _js.InvokeAsync<bool>("ccFileSystem.hasPersistedHandle");
        if (hasPersisted)
        {
            HasDirectory = await _js.InvokeAsync<bool>("ccFileSystem.restoreHandle");
            if (HasDirectory)
                DirectoryName = await _js.InvokeAsync<string?>("ccFileSystem.getRootName");
            else
                NeedsPermission = true; // handle exists but permission expired
        }
    }

    /// <summary>Re-request permission on persisted handle (must be called from user gesture).</summary>
    public async Task<bool> ReRequestPermissionAsync()
    {
        var granted = await _js.InvokeAsync<bool>("ccFileSystem.reRequestPermission");
        if (granted)
        {
            HasDirectory = true;
            NeedsPermission = false;
            DirectoryName = await _js.InvokeAsync<string?>("ccFileSystem.getRootName");
            OnChanged?.Invoke();
        }
        return granted;
    }

    /// <summary>Prompt user to pick a cache directory.</summary>
    public async Task<bool> PickDirectoryAsync()
    {
        var name = await _js.InvokeAsync<string?>("ccFileSystem.pickDirectory");
        if (name is null) return false;
        DirectoryName = name;
        HasDirectory = true;
        OnChanged?.Invoke();
        return true;
    }

    /// <summary>List entries in a sub-path relative to cache root.</summary>
    public async Task<List<CacheEntry>> ListDirectoryAsync(string subPath = "")
    {
        var entries = await _js.InvokeAsync<CacheEntry[]>("ccFileSystem.listDirectory", subPath);
        return entries?.ToList() ?? new();
    }

    /// <summary>Write binary data to a file in cache.</summary>
    public async Task WriteFileAsync(string subPath, string fileName, byte[] data)
    {
        await _js.InvokeVoidAsync("ccFileSystem.writeFile", subPath, fileName, data);
        OnChanged?.Invoke();
    }

    /// <summary>Check if a file exists in cache.</summary>
    public async Task<bool> FileExistsAsync(string subPath, string fileName)
    {
        return await _js.InvokeAsync<bool>("ccFileSystem.fileExists", subPath, fileName);
    }

    /// <summary>Delete a file from cache.</summary>
    public async Task DeleteFileAsync(string subPath, string fileName)
    {
        await _js.InvokeVoidAsync("ccFileSystem.deleteFile", subPath, fileName);
        OnChanged?.Invoke();
    }

    /// <summary>Delete a directory recursively.</summary>
    public async Task DeleteDirectoryAsync(string subPath, string dirName)
    {
        await _js.InvokeVoidAsync("ccFileSystem.deleteDirectory", subPath, dirName);
        OnChanged?.Invoke();
    }

    /// <summary>Create a subdirectory.</summary>
    public async Task CreateDirectoryAsync(string subPath, string dirName)
    {
        await _js.InvokeVoidAsync("ccFileSystem.createDirectory", subPath, dirName);
        OnChanged?.Invoke();
    }

    /// <summary>Get total cache size in bytes.</summary>
    public async Task<long> GetCacheSizeAsync(string subPath = "")
    {
        return await _js.InvokeAsync<long>("ccFileSystem.getCacheSize", subPath);
    }

    /// <summary>Download a file from an agent via relay and save to cache.</summary>
    public async Task<bool> DownloadFromAgentAsync(RelaySocket relay, string remotePath, string cacheSubPath, string fileName, Action<long, long>? onProgress = null)
    {
        if (!relay.IsConnected || !HasDirectory) return false;

        // Get file size first by reading 0 bytes
        var probe = RelaySocket.BuildFileCommand(0x02, remotePath, 0, 0);
        var probeResp = await relay.SendAndReceive(probe);
        if (probeResp is null || probeResp.Length < 12) return false;

        // Read the full file in chunks
        const long chunkSize = 65536;
        using var ms = new MemoryStream();
        long offset = 0;

        while (true)
        {
            var payload = RelaySocket.BuildFileCommand(0x02, remotePath, chunkSize, offset);
            var response = await relay.SendAndReceive(payload);
            if (response is null || response.Length < 4) return false;

            var status = RelaySocket.ReadStatus(response);
            if (status != 0) return false;
            if (response.Length < 12) return false;

            var (bytesRead, data) = RelaySocket.ReadFileContent(response);
            if (bytesRead == 0) break;

            ms.Write(data, 0, data.Length);
            offset += (long)bytesRead;
            onProgress?.Invoke(offset, -1); // total unknown

            if ((long)bytesRead < chunkSize) break;
        }

        await WriteFileAsync(cacheSubPath, fileName, ms.ToArray());
        return true;
    }

    /// <summary>Reset: clear persisted handle.</summary>
    public async Task ResetAsync()
    {
        await _js.InvokeVoidAsync("ccFileSystem.clearHandle");
        HasDirectory = false;
        DirectoryName = null;
        OnChanged?.Invoke();
    }
}

public class CacheEntry
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "file";
    public long Size { get; set; }
    public long LastModified { get; set; }

    public bool IsDirectory => Kind == "directory";
}
