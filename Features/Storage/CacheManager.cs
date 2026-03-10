using cc.Features.Relay;
using cc.Infrastructure;
using Microsoft.JSInterop;

namespace cc.Features.Storage;

public class CacheManager
{
    private readonly IJSRuntime _js;
    private readonly IEventBus _bus;

    public bool IsSupported { get; private set; }
    public bool HasDirectory { get; private set; }
    public bool NeedsPermission { get; private set; }
    public string? DirectoryName { get; private set; }
    public bool SetupRequired => IsSupported && !HasDirectory;

    public CacheManager(IJSRuntime js, IEventBus bus)
    {
        _js = js;
        _bus = bus;
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
            _bus.Publish(new CacheChangedEvent());
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
        _bus.Publish(new CacheChangedEvent());
        return true;
    }

    /// <summary>Write a blob by file GUID into .fs/{fileId}.</summary>
    public async Task WriteBlobAsync(string fileId, byte[] data)
    {
        await _js.InvokeVoidAsync("ccFileSystem.writeBlobById", fileId, data);
        _bus.Publish(new CacheChangedEvent());
    }

    /// <summary>Read a blob by file GUID (returns base64 string).</summary>
    public async Task<string?> ReadBlobAsync(string fileId)
    {
        return await _js.InvokeAsync<string?>("ccFileSystem.readBlobById", fileId);
    }

    /// <summary>Check if a blob exists by file GUID.</summary>
    public async Task<bool> BlobExistsAsync(string fileId)
    {
        return await _js.InvokeAsync<bool>("ccFileSystem.blobExists", fileId);
    }

    /// <summary>Delete a blob by file GUID.</summary>
    public async Task DeleteBlobAsync(string fileId)
    {
        await _js.InvokeVoidAsync("ccFileSystem.deleteBlobById", fileId);
        _bus.Publish(new CacheChangedEvent());
    }

    /// <summary>Get total cache size in bytes (all blobs in .fs/).</summary>
    public async Task<long> GetCacheSizeAsync()
    {
        return await _js.InvokeAsync<long>("ccFileSystem.getCacheSize");
    }

    /// <summary>Download a file from an agent via relay and stream directly to .fs/{fileId}.</summary>
    public async Task<bool> DownloadFromAgentAsync(
        RelaySocket relay, string remotePath, string fileId,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default,
        Func<long, long, Task>? onProgress = null)
    {
        if (!relay.IsConnected || !HasDirectory) return false;

        // Get file size first by reading 0 bytes
        var probe = RelaySocket.BuildFileCommand(0x02, remotePath, 0, 0);
        var probeResp = await relay.SendAndReceive(probe);
        if (probeResp is null || probeResp.Length < 12) return false;

        // Open a streaming writable to .fs/{fileId}
        if (resumeOffset > 0)
            await _js.InvokeVoidAsync("ccFileSystem.beginResumeWriteById", fileId, resumeOffset);
        else
            await _js.InvokeVoidAsync("ccFileSystem.beginWriteById", fileId);

        const long chunkSize = 65536;
        long offset = resumeOffset;
        bool success = false;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var payload = RelaySocket.BuildFileCommand(0x02, remotePath, chunkSize, offset);
                var response = await relay.SendAndReceive(payload);
                if (response is null || response.Length < 4) return false;

                var status = RelaySocket.ReadStatus(response);
                if (status != 0) return false;
                if (response.Length < 12) return false;

                var (bytesRead, data) = RelaySocket.ReadFileContent(response);
                if (bytesRead == 0) break;

                // Stream chunk directly to disk
                await _js.InvokeVoidAsync("ccFileSystem.writeChunk", data);
                offset += (long)bytesRead;
                if (onProgress is not null)
                    await onProgress(offset, -1);

                if ((long)bytesRead < chunkSize) break;
            }

            await _js.InvokeVoidAsync("ccFileSystem.endWrite");
            success = true;
            _bus.Publish(new CacheChangedEvent());
            return true;
        }
        catch (OperationCanceledException)
        {
            // Paused: save partial data
            await _js.InvokeVoidAsync("ccFileSystem.endWrite");
            throw;
        }
        finally
        {
            if (!success)
            {
                try { await _js.InvokeVoidAsync("ccFileSystem.abortWrite"); } catch { }
            }
        }
    }

    /// <summary>Reset: clear persisted handle.</summary>
    public async Task ResetAsync()
    {
        await _js.InvokeVoidAsync("ccFileSystem.clearHandle");
        HasDirectory = false;
        DirectoryName = null;
        _bus.Publish(new CacheChangedEvent());
    }

    /// <summary>Clear all application data: all IndexedDB stores + persisted file handle.</summary>
    public async Task ClearAllDataAsync()
    {
        await _js.InvokeVoidAsync("ccClearAll");
        await _js.InvokeVoidAsync("ccFileSystem.clearHandle");
        HasDirectory = false;
        DirectoryName = null;
        NeedsPermission = false;
        _bus.Publish(new CacheChangedEvent());
    }
}
