using C2.Features.Relay;
using C2.Features.Storage;
using C2.Features.Workspace;
using C2.Infrastructure;

namespace C2.Features.Downloads;

public class DownloadService : IDisposable
{
    private readonly DownloadStore _store;
    private readonly CacheManager _cache;
    private readonly RelayConnectionService _relaySvc;
    private readonly ServiceStateStore _serviceState;
    private readonly WindowManager _wm;
    private readonly IEventBus _bus;

    private readonly HashSet<string> _processingAgents = new();
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposing;

    public IReadOnlyCollection<string> ProcessingAgents => _processingAgents;

    public DownloadService(
        DownloadStore store, CacheManager cache,
        RelayConnectionService relaySvc, ServiceStateStore serviceState,
        WindowManager wm, IEventBus bus)
    {
        _store = store;
        _cache = cache;
        _relaySvc = relaySvc;
        _serviceState = serviceState;
        _wm = wm;
        _bus = bus;
    }

    public async Task StartAsync()
    {
        await _store.LoadAsync();
        await ResetStaleDownloads();

        _subscriptions.Add(_bus.Subscribe<DownloadItemQueuedEvent>(e => OnItemQueued(e.AgentUuid)));
        _subscriptions.Add(_bus.Subscribe<ServiceStateChangedEvent>(_ => OnServiceStateChanged()));
        _subscriptions.Add(_bus.Subscribe<AgentOnlineEvent>(e => AutoResumeAsync(e.Uuid, e.AgentId, e.RelayUrl)));
    }

    private async Task ResetStaleDownloads()
    {
        var stale = _store.Downloads
            .Where(r => (r.Status == DownloadStatus.Downloading || r.Status == DownloadStatus.Queued) && !_store.Cts.HasActive(r.Id))
            .ToList();
        foreach (var dl in stale)
            await _store.PauseAsync(dl.Id);
    }

    private void OnServiceStateChanged()
    {
        foreach (var uuid in _processingAgents.ToList())
        {
            if (_serviceState.IsEffectivelyPaused(ServiceName.Upload, uuid))
                _store.Cts.CancelAll();
        }
    }

    private async Task AutoResumeAsync(string uuid, string agentId, string relayUrl)
    {
        if (!_cache.HasDirectory) return;
        if (_serviceState.IsEffectivelyPaused(ServiceName.Upload, uuid)) return;
        await _store.LoadAsync();

        var paused = _store.GetByAgent(uuid)
            .Where(r => r.Status == DownloadStatus.Paused)
            .ToList();
        foreach (var dl in paused)
            await _store.QueueAsync(dl.Id);

        await TryProcessQueue(uuid, agentId, relayUrl);
    }

    private void OnItemQueued(string agentUuid)
    {
        if (!_cache.HasDirectory) return;
        if (_serviceState.IsEffectivelyPaused(ServiceName.Upload, agentUuid)) return;
        if (_relaySvc.IsDisposing) return;

        var resolved = _relaySvc.FindOnlineAgent(agentUuid);
        if (resolved is null) return;
        _ = TryProcessQueue(agentUuid, resolved.Value.AgentId, resolved.Value.RelayUrl);
    }

    private async Task TryProcessQueue(string uuid, string agentId, string relayUrl)
    {
        if (_processingAgents.Contains(uuid)) return;
        if (_store.HasActiveDownload(uuid)) return;
        if (_store.GetNextQueued(uuid) is null) return;

        _processingAgents.Add(uuid);
        RelaySocket? relay = null;
        try
        {
            relay = await _relaySvc.CreateRelay(agentId, relayUrl);
            if (relay is null) return;

            while (!_disposing && !_relaySvc.IsDisposing)
            {
                if (_store.HasActiveDownload(uuid)) break;
                var next = _store.GetNextQueued(uuid);
                if (next is null) break;

                var cts = _store.Cts.Register(next.Id);
                try
                {
                    var success = await _cache.DownloadFromAgentAsync(
                        relay, next.RemotePath, next.CacheSubPath,
                        next.DownloadedSize, cts.Token,
                        async (downloaded, _) =>
                        {
                            await _store.UpdateProgressAsync(next.Id, downloaded);
                        });

                    if (success)
                        await _store.CompleteAsync(next.Id);
                    else
                        await _store.FailAsync(next.Id, "Sync returned failure");
                }
                catch (OperationCanceledException)
                {
                    await _store.PauseAsync(next.Id);
                }
                catch (Exception ex)
                {
                    if (cts.IsCancellationRequested)
                        await _store.PauseAsync(next.Id);
                    else
                        await _store.FailAsync(next.Id, ex.Message);
                }
                finally
                {
                    _store.Cts.Remove(next.Id);
                }
            }
        }
        catch { }
        finally
        {
            _processingAgents.Remove(uuid);
            if (relay is not null && !_wm.Windows.Any(w => w.Relay == relay))
                await relay.Disconnect();
        }
    }

    public void Dispose()
    {
        _disposing = true;
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
    }
}
