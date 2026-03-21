using C2.Features.Relay;
using C2.Features.Storage;
using static C2.Features.Storage.CacheManager;
using C2.Features.Workspace;
using C2.Infrastructure;

namespace C2.Features.Transfers;

public class TransferService : IDisposable
{
    private readonly TransferStore _store;
    private readonly CacheManager _cache;
    private readonly RelayConnectionService _relaySvc;
    private readonly WindowManager _wm;
    private readonly IEventBus _bus;

    private readonly HashSet<string> _processingAgents = new();
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposing;

    public IReadOnlyCollection<string> ProcessingAgents => _processingAgents;

    public TransferService(
        TransferStore store, CacheManager cache,
        RelayConnectionService relaySvc,
        WindowManager wm, IEventBus bus)
    {
        _store = store;
        _cache = cache;
        _relaySvc = relaySvc;
        _wm = wm;
        _bus = bus;
    }

    public async Task StartAsync()
    {
        await _store.LoadAsync();

        _subscriptions.Add(_bus.Subscribe<TransferItemQueuedEvent>(e => OnItemQueued(e.AgentUuid)));
        _subscriptions.Add(_bus.Subscribe<AgentOnlineEvent>(e => OnAgentOnline(e.Uuid, e.AgentId, e.RelayUrl)));
    }

    private async Task OnAgentOnline(string uuid, string agentId, string relayUrl)
    {
        if (!_cache.HasDirectory) return;
        await _store.LoadAsync();

        foreach (var dl in _store.GetByAgent(uuid))
        {
            if (dl.Status == TransferStatus.Downloading)
            {
                // Cancel any in-flight download — the relay from the old session
                // is dead.  QueueAsync sets status to Queued synchronously before
                // yielding, so the old task's catch block will see Queued and skip
                // overwriting the status back to Paused.
                _store.Cts.Cancel(dl.Id);
                await _store.QueueAsync(dl.Id);
            }
            else if (dl.Status == TransferStatus.Paused && dl.AutoResume)
                await _store.QueueAsync(dl.Id);
        }

        _processingAgents.Remove(uuid);
        await TryProcessQueue(uuid, agentId, relayUrl);
    }

    private void OnItemQueued(string agentUuid)
    {
        if (!_cache.HasDirectory) return;
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
            relay.AddRef();

            while (!_disposing && !_relaySvc.IsDisposing && relay.IsConnected)
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
                    {
                        // Disconnected mid-transfer — partial data saved, auto-resume
                        // when the agent reconnects.  Skip if already re-queued by
                        // a concurrent OnAgentOnline (reconnection cancelled us).
                        if (next.Status != TransferStatus.Queued)
                            await _store.PauseForResumeAsync(next.Id);
                        break;
                    }
                }
                catch (AgentErrorException ex)
                {
                    if (next.Status != TransferStatus.Queued)
                        await _store.FailAsync(next.Id, ex.Message);
                }
                catch (OperationCanceledException)
                {
                    if (cts.IsCancellationRequested && next.Status != TransferStatus.Queued)
                        await _store.PauseAsync(next.Id);
                    break;
                }
                catch (Exception ex)
                {
                    if (next.Status != TransferStatus.Queued)
                    {
                        if (cts.IsCancellationRequested)
                            await _store.PauseAsync(next.Id);
                        else
                            await _store.FailAsync(next.Id, ex.Message);
                    }
                    break;
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
            if (relay is not null)
                relay.Release();
            _processingAgents.Remove(uuid);
            if (relay is not null && !_wm.Windows.Any(w => w.Relay == relay) && relay.InUseCount <= 0)
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
