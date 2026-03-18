using C2.Features.Transfers;
using C2.Features.FileManager;
using C2.Features.Relay;
using C2.Features.Storage;
using C2.Features.Workspace;
using C2.Infrastructure;

namespace C2.Features.Scan;

public class ScanService : IDisposable
{
    private readonly ScanStore _store;
    private readonly TransferStore _downloads;
    private readonly VfsStore _vfs;
    private readonly CacheManager _cache;
    private readonly RelayConnectionService _relaySvc;
    private readonly ServiceStateStore _serviceState;
    private readonly MessageService _msg;
    private readonly WindowManager _wm;
    private readonly IEventBus _bus;

    private readonly HashSet<string> _searchingAgents = new();
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposing;

    public ScanService(
        ScanStore store, TransferStore downloads, VfsStore vfs,
        CacheManager cache, RelayConnectionService relaySvc,
        ServiceStateStore serviceState, MessageService msg,
        WindowManager wm, IEventBus bus)
    {
        _store = store;
        _downloads = downloads;
        _vfs = vfs;
        _cache = cache;
        _relaySvc = relaySvc;
        _serviceState = serviceState;
        _msg = msg;
        _wm = wm;
        _bus = bus;
    }

    public async Task StartAsync()
    {
        await _store.LoadAsync();
        await ResetStaleSearches();

        _subscriptions.Add(_bus.Subscribe<ScanItemQueuedEvent>(e => OnSearchItemQueued(e.AgentUuid)));
        _subscriptions.Add(_bus.Subscribe<ServiceStateChangedEvent>(_ => OnServiceStateChanged()));
        _subscriptions.Add(_bus.Subscribe<AgentOnlineEvent>(e => AutoResumeAsync(e.Uuid, e.AgentId, e.RelayUrl)));
    }

    private async Task ResetStaleSearches()
    {
        var stale = _store.Searches
            .Where(r => r.Status == ScanStatus.Scanning && !_store.Cts.HasActive(r.Id))
            .ToList();
        foreach (var s in stale)
            await _store.PauseAsync(s.Id);
    }

    private void OnServiceStateChanged()
    {
        foreach (var uuid in _searchingAgents.ToList())
        {
            if (_serviceState.IsEffectivelyPaused(ServiceName.Scan, uuid))
                _store.Cts.CancelAll();
        }
    }

    private async Task AutoResumeAsync(string uuid, string agentId, string relayUrl)
    {
        if (_serviceState.IsEffectivelyPaused(ServiceName.Scan, uuid)) return;
        await _store.LoadAsync();

        var paused = _store.GetByAgent(uuid)
            .Where(r => r.Status == ScanStatus.Paused)
            .ToList();
        foreach (var s in paused)
            await _store.ResumeAsync(s.Id);

        await TryProcessSearchQueue(uuid, agentId, relayUrl);
    }

    private void OnSearchItemQueued(string agentUuid)
    {
        if (_serviceState.IsEffectivelyPaused(ServiceName.Scan, agentUuid)) return;
        if (_relaySvc.IsDisposing) return;

        var resolved = _relaySvc.FindOnlineAgent(agentUuid);
        if (resolved is null) return;
        _ = TryProcessSearchQueue(agentUuid, resolved.Value.AgentId, resolved.Value.RelayUrl);
    }

    private async Task TryProcessSearchQueue(string uuid, string agentId, string relayUrl)
    {
        if (_searchingAgents.Contains(uuid)) return;
        if (_store.GetNextPending(uuid) is null) return;

        _searchingAgents.Add(uuid);
        RelaySocket? relay = null;
        try
        {
            relay = await _relaySvc.CreateRelay(agentId, relayUrl);
            if (relay is null) return;
            relay.AddRef();

            while (!_disposing && !_relaySvc.IsDisposing)
            {
                var scan = _store.GetNextPending(uuid);
                if (scan is null) break;

                var cts = _store.Cts.Register(scan.Id);
                var label = string.IsNullOrEmpty(scan.RootPath) ? "Full filesystem" : scan.RootPath;
                var agent = scan.AgentName;
                _msg.Info("Search Started", $"{label} ({scan.Extensions}) on {agent}", "Search");
                try
                {
                    await ProcessSearch(relay, scan, cts.Token);
                    _msg.Success("Search Completed", $"{label} on {agent} — {scan.FilesFound} files found in {scan.DirsScanned} dirs", "Search");
                }
                catch (OperationCanceledException)
                {
                    await _store.PauseAsync(scan.Id);
                    _msg.Warn("Search Paused", $"{label} on {agent} — {scan.DirsScanned}/{scan.DirsTotal} dirs, {scan.FilesFound} files found", "Search");
                }
                catch (Exception ex)
                {
                    if (cts.IsCancellationRequested)
                    {
                        await _store.PauseAsync(scan.Id);
                        _msg.Warn("Search Paused", $"{label} on {agent} — {scan.DirsScanned}/{scan.DirsTotal} dirs, {scan.FilesFound} files found", "Search");
                    }
                    else
                    {
                        await _store.FailAsync(scan.Id, ex.Message);
                        _msg.Error("Search Failed", $"{label} on {agent} — {ex.Message}", "Search");
                    }
                }
                finally
                {
                    _store.Cts.Remove(scan.Id);
                }
            }
        }
        catch { }
        finally
        {
            if (relay is not null)
                relay.Release();
            _searchingAgents.Remove(uuid);
            if (relay is not null && !_wm.Windows.Any(w => w.Relay == relay) && relay.InUseCount <= 0)
                await relay.Disconnect();
        }
    }

    private async Task ProcessSearch(RelaySocket relay, ScanRecord scan, CancellationToken ct)
    {
        var extensionSet = scan.Extensions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant())
            .ToHashSet();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var pending = scan.PendingDirs;
            if (pending.Count == 0) break;

            var dir = pending[0];
            pending.RemoveAt(0);
            scan.PendingDirs = pending;

            var payload = RelaySocket.BuildPathCommand(AgentCommands.ListDirectory, dir);
            var response = await relay.SendAndReceive(payload);
            if (response is null || response.Length < 4) continue;

            var (entries, error) = DirEntry.ParseDirectoryResponse(response);
            if (error is not null) continue;

            var parentRemotePath = dir.TrimEnd('\\', '/');
            var parentId = await _vfs.ResolveOrCreatePathAsync(scan.AgentUuid, parentRemotePath);

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                var entryRemotePath = string.IsNullOrEmpty(parentRemotePath)
                    ? entry.Name
                    : parentRemotePath + @"\" + entry.Name;

                if (entry.IsDirectory || entry.IsDrive)
                {
                    await _vfs.PutDirectoryAsync(scan.AgentUuid, parentId, entry.Name, entryRemotePath.Replace('\\', '/'), entry.IsDrive);

                    var subDir = entryRemotePath + @"\";
                    pending = scan.PendingDirs;
                    pending.Add(subDir);
                    scan.PendingDirs = pending;
                    scan.DirsTotal++;
                }
                else
                {
                    var vfsFile = await _vfs.PutFileAsync(scan.AgentUuid, parentId, entry.Name, entryRemotePath.Replace('\\', '/'), (long)entry.Size);

                    var ext = System.IO.Path.GetExtension(entry.Name)?.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(ext) && extensionSet.Contains(ext))
                    {
                        scan.FilesFound++;

                        if (scan.AutoDownload && _cache.HasDirectory)
                        {
                            var existing = _downloads.Find(scan.AgentUuid, entryRemotePath);
                            if (existing is null)
                            {
                                await _downloads.AddAsync(scan.AgentUuid, scan.AgentName, entryRemotePath, entry.Name, vfsFile.Id, (long)entry.Size);
                                scan.FilesQueued++;
                            }
                            else if (existing.Status == TransferStatus.Failed)
                            {
                                await _downloads.RequeueAsync(existing.Id, vfsFile.Id, (long)entry.Size);
                                scan.FilesQueued++;
                            }
                        }
                    }
                }
            }

            scan.DirsScanned++;
            await _store.UpdateAsync(scan);
        }

        await _store.CompleteAsync(scan.Id);
    }

    public void Dispose()
    {
        _disposing = true;
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
    }
}
