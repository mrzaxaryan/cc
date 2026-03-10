using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using cc.Models;

namespace cc.Services;

public class RelayConnectionService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly RelayStore _relayStore;
    private readonly AgentStore _agentDb;
    private readonly DownloadStore _downloads;
    private readonly ScanStore _scans;
    private readonly VfsStore _vfs;
    private readonly CacheManager _cache;
    private readonly WindowManager _wm;
    private readonly MessageService _msg;

    private readonly Dictionary<string, RelayState> _relayStates = new();
    private readonly HashSet<string> _processingAgents = new();
    private readonly HashSet<string> _scanningAgents = new();
    private bool _started;
    private bool _disposing;

    public event Action? OnChanged;

    public IReadOnlyDictionary<string, RelayState> RelayStates => _relayStates;
    public IReadOnlyCollection<string> ProcessingAgents => _processingAgents;

    public class RelayState
    {
        public string Url { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Connected { get; set; }
        public GroupInfo<AgentConnection>? Agents { get; set; }
        public ClientWebSocket? Ws { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public DateTime? ReconnectAt { get; set; }
    }

    public RelayConnectionService(
        RelayStore relayStore, AgentStore agentDb, DownloadStore downloads,
        ScanStore scans, VfsStore vfs,
        CacheManager cache, WindowManager wm, MessageService msg)
    {
        _relayStore = relayStore;
        _agentDb = agentDb;
        _downloads = downloads;
        _scans = scans;
        _vfs = vfs;
        _cache = cache;
        _wm = wm;
        _msg = msg;
    }

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        await _relayStore.LoadAsync();
        await _agentDb.LoadAsync();
        await _downloads.LoadAsync();
        await _scans.LoadAsync();
        await ResetStaleDownloads();
        await ResetStaleScans();
        _relayStore.OnChanged += OnRelayStoreChanged;
        _downloads.OnItemQueued += OnItemQueued;
        _scans.OnItemQueued += OnScanItemQueued;
        SyncRelays();
    }

    private void OnRelayStoreChanged()
    {
        SyncRelays();
        NotifyChanged();
    }

    private void NotifyChanged() => OnChanged?.Invoke();

    private void SyncRelays()
    {
        var enabledUrls = _relayStore.EnabledRelays.Select(r => r.Url).ToHashSet();

        foreach (var relay in _relayStore.EnabledRelays)
        {
            if (!_relayStates.ContainsKey(relay.Url))
            {
                _relayStates[relay.Url] = new RelayState { Url = relay.Url, Name = relay.Name };
                _ = ConnectToEvents(relay.Url);
            }
            else
            {
                _relayStates[relay.Url].Name = relay.Name;
            }
        }

        var removedUrls = _relayStates.Keys.Where(url => !enabledUrls.Contains(url)).ToList();
        foreach (var url in removedUrls)
        {
            _ = DisconnectRelay(url);
            _relayStates.Remove(url);
        }
    }

    public List<(AgentConnection Agent, string RelayUrl)> GetAllAgents()
    {
        var result = new List<(AgentConnection, string)>();
        foreach (var rs in _relayStates.Values)
        {
            if (rs.Agents?.Connections is not null)
            {
                foreach (var agent in rs.Agents.Connections)
                    result.Add((agent, rs.Url));
            }
        }
        return result;
    }

    public void ForceReconnect(string url)
    {
        if (_relayStates.TryGetValue(url, out var rs))
        {
            rs.ReconnectAt = null;
            rs.Cts?.Cancel();
            _ = ConnectToEvents(url);
        }
    }

    public async Task<RelaySocket?> CreateRelay(string agentId, string relayUrl)
    {
        var existingWin = _wm.Windows.FirstOrDefault(w => w.AgentId == agentId && w.Relay is { IsConnected: true });
        if (existingWin?.Relay is not null)
            return existingWin.Relay;

        var relay = new RelaySocket { BaseUrl = relayUrl };
        try
        {
            await relay.Connect(agentId);
            return relay;
        }
        catch (Exception ex)
        {
            _msg.Error($"Connection failed: {ex.Message}");
            await relay.Disconnect();
            return null;
        }
    }

    // Reset downloads stuck in "Downloading" from a previous session (page refresh)
    private async Task ResetStaleDownloads()
    {
        var stale = _downloads.Downloads
            .Where(r => (r.Status == DownloadStatus.Downloading || r.Status == DownloadStatus.Queued) && !_downloads.HasActiveCts(r.Id))
            .ToList();
        foreach (var dl in stale)
            await _downloads.PauseAsync(dl.Id);
    }

    // --- UUID fetching ---

    private async Task FetchUuid(AgentConnection agent, string relayUrl)
    {
        var relay = new RelaySocket { BaseUrl = relayUrl };
        try
        {
            await relay.Connect(agent.Id);
            var response = await relay.SendAndReceive(new byte[] { 0x00 });
            if (response is not null && response.Length >= 4 + RelaySocket.SystemInfoSize)
            {
                var status = RelaySocket.ReadStatus(response);
                if (status == 0)
                {
                    var (guid, hostname, architecture, platform) = RelaySocket.ReadSystemInfo(response);
                    var uuid = guid.ToString();
                    agent.Os = platform;
                    agent.Arch = architecture;
                    var relayEntry = _relayStore.GetByUrl(relayUrl);
                    await _agentDb.UpsertAsync(uuid, agent, relayEntry?.Id ?? "");
                    NotifyChanged();
                    _ = AutoResumeDownloads(uuid, agent.Id, relayUrl);
                    _ = AutoResumeScans(uuid, agent.Id, relayUrl);
                }
            }
        }
        catch { }
        finally
        {
            await relay.Disconnect();
        }
    }

    private void FetchUuidsForAgents(AgentConnection[] agents, string relayUrl)
    {
        foreach (var agent in agents)
        {
            var existingUuid = _agentDb.GetUuidByAgentId(agent.Id);
            if (existingUuid is null)
                _ = FetchUuid(agent, relayUrl);
            else
            {
                _ = AutoResumeDownloads(existingUuid, agent.Id, relayUrl);
                _ = AutoResumeScans(existingUuid, agent.Id, relayUrl);
            }
        }
    }

    private async Task AutoResumeDownloads(string uuid, string agentId, string relayUrl)
    {
        if (!_cache.HasDirectory) return;
        await _downloads.LoadAsync();

        // Queue all paused downloads for this agent
        var paused = _downloads.GetByAgent(uuid)
            .Where(r => r.Status == DownloadStatus.Paused)
            .ToList();
        foreach (var dl in paused)
            await _downloads.QueueAsync(dl.Id);

        // Process all queued items (both freshly queued and previously queued)
        await TryProcessQueue(uuid, agentId, relayUrl);
    }

    private void OnItemQueued(string agentUuid)
    {
        if (_disposing || !_cache.HasDirectory) return;

        var agent = _agentDb.GetByUuid(agentUuid);
        if (agent is null) return;

        var relayEntry = !string.IsNullOrEmpty(agent.RelayStoreId) ? _relayStore.GetById(agent.RelayStoreId) : null;
        if (relayEntry is null) return;

        // Check the agent is actually online
        var online = GetAllAgents().Any(a => a.Agent.Id == agent.AgentId);
        if (!online) return;

        _ = TryProcessQueue(agentUuid, agent.AgentId, relayEntry.Url);
    }

    private async Task TryProcessQueue(string uuid, string agentId, string relayUrl)
    {
        // Prevent concurrent processing loops for the same agent
        if (_processingAgents.Contains(uuid)) return;
        if (_downloads.HasActiveDownload(uuid)) return;
        if (_downloads.GetNextQueued(uuid) is null) return;

        _processingAgents.Add(uuid);
        RelaySocket? relay = null;
        try
        {
            relay = await CreateRelay(agentId, relayUrl);
            if (relay is null) return;

            while (!_disposing)
            {
                if (_downloads.HasActiveDownload(uuid)) break;
                var next = _downloads.GetNextQueued(uuid);
                if (next is null) break;

                var cts = _downloads.RegisterCts(next.Id);
                try
                {
                    var success = await _cache.DownloadFromAgentAsync(
                        relay, next.RemotePath, next.CacheSubPath,
                        next.DownloadedSize, cts.Token,
                        async (downloaded, _) =>
                        {
                            await _downloads.UpdateProgressAsync(next.Id, downloaded);
                        });

                    if (success)
                        await _downloads.CompleteAsync(next.Id);
                    else
                        await _downloads.FailAsync(next.Id, "Sync returned failure");
                }
                catch (OperationCanceledException)
                {
                    await _downloads.PauseAsync(next.Id);
                }
                catch (Exception ex)
                {
                    if (cts.IsCancellationRequested)
                        await _downloads.PauseAsync(next.Id);
                    else
                        await _downloads.FailAsync(next.Id, ex.Message);
                }
                finally
                {
                    _downloads.RemoveCts(next.Id);
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

    // --- Scan processing ---

    private async Task ResetStaleScans()
    {
        var stale = _scans.Scans
            .Where(r => r.Status == ScanStatus.Scanning && !_scans.HasActiveCts(r.Id))
            .ToList();
        foreach (var s in stale)
            await _scans.PauseAsync(s.Id);
    }

    private void OnScanItemQueued(string agentUuid)
    {
        if (_disposing) return;

        var agent = _agentDb.GetByUuid(agentUuid);
        if (agent is null) return;

        var relayEntry = !string.IsNullOrEmpty(agent.RelayStoreId) ? _relayStore.GetById(agent.RelayStoreId) : null;
        if (relayEntry is null) return;

        var online = GetAllAgents().Any(a => a.Agent.Id == agent.AgentId);
        if (!online) return;

        _ = TryProcessScanQueue(agentUuid, agent.AgentId, relayEntry.Url);
    }

    private async Task AutoResumeScans(string uuid, string agentId, string relayUrl)
    {
        await _scans.LoadAsync();
        var paused = _scans.GetByAgent(uuid)
            .Where(r => r.Status == ScanStatus.Paused)
            .ToList();
        foreach (var s in paused)
            await _scans.ResumeAsync(s.Id);

        await TryProcessScanQueue(uuid, agentId, relayUrl);
    }

    private async Task TryProcessScanQueue(string uuid, string agentId, string relayUrl)
    {
        if (_scanningAgents.Contains(uuid)) return;
        if (_scans.GetNextPending(uuid) is null) return;

        _scanningAgents.Add(uuid);
        RelaySocket? relay = null;
        try
        {
            relay = await CreateRelay(agentId, relayUrl);
            if (relay is null) return;

            while (!_disposing)
            {
                var scan = _scans.GetNextPending(uuid);
                if (scan is null) break;

                var cts = _scans.RegisterCts(scan.Id);
                try
                {
                    await ProcessScan(relay, scan, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    await _scans.PauseAsync(scan.Id);
                }
                catch (Exception ex)
                {
                    if (cts.IsCancellationRequested)
                        await _scans.PauseAsync(scan.Id);
                    else
                        await _scans.FailAsync(scan.Id, ex.Message);
                }
                finally
                {
                    _scans.RemoveCts(scan.Id);
                }
            }
        }
        catch { }
        finally
        {
            _scanningAgents.Remove(uuid);
            if (relay is not null && !_wm.Windows.Any(w => w.Relay == relay))
                await relay.Disconnect();
        }
    }

    private async Task ProcessScan(RelaySocket relay, ScanRecord scan, CancellationToken ct)
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

            // List directory via relay
            var payload = RelaySocket.BuildPathCommand(0x01, dir);
            var response = await relay.SendAndReceive(payload);
            if (response is null || response.Length < 4) continue;

            var (entries, error) = Models.DirEntry.ParseDirectoryResponse(response);
            if (error is not null) continue;

            // Resolve parent directory in VFS for this listing
            var parentRemotePath = dir.TrimEnd('\\', '/');
            var parentId = await _vfs.ResolveOrCreatePathAsync(scan.AgentUuid, parentRemotePath);

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                var entryRemotePath = parentRemotePath + @"\" + entry.Name;

                if (entry.IsDirectory || entry.IsDrive)
                {
                    // Cache directory in VFS
                    await _vfs.PutDirectoryAsync(scan.AgentUuid, parentId, entry.Name, entryRemotePath.Replace('\\', '/'), entry.IsDrive);

                    var subDir = entryRemotePath + @"\";
                    pending = scan.PendingDirs;
                    pending.Add(subDir);
                    scan.PendingDirs = pending;
                    scan.DirsTotal++;
                }
                else
                {
                    // Cache every file in VFS
                    var vfsFile = await _vfs.PutFileAsync(scan.AgentUuid, parentId, entry.Name, entryRemotePath.Replace('\\', '/'), (long)entry.Size);

                    // Check extension filter for scan stats + auto-download
                    var ext = System.IO.Path.GetExtension(entry.Name)?.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(ext) && extensionSet.Contains(ext))
                    {
                        scan.FilesFound++;

                        // Auto-download if enabled
                        if (scan.AutoDownload && _cache.HasDirectory)
                        {
                            var existing = _downloads.Find(scan.AgentUuid, entryRemotePath);
                            if (existing is null || existing.Status == DownloadStatus.Failed)
                            {
                                await _downloads.AddAsync(scan.AgentUuid, scan.AgentName, entryRemotePath, entry.Name, vfsFile.Id, (long)entry.Size);
                                scan.FilesQueued++;
                            }
                        }
                    }
                }
            }

            scan.DirsScanned++;
            await _scans.UpdateAsync(scan);
        }

        await _scans.CompleteAsync(scan.Id);
    }

    // --- Events WebSocket ---

    private async Task DisconnectRelay(string url)
    {
        if (_relayStates.TryGetValue(url, out var rs))
        {
            rs.Cts?.Cancel();

            if (rs.Agents?.Connections is not null)
            {
                foreach (var agent in rs.Agents.Connections)
                {
                    if (_wm.ConnectedAgentIds.Contains(agent.Id))
                        await _wm.DisconnectAgent(agent.Id);
                }
            }
            rs.Agents = null;
        }
    }

    private async Task ConnectToEvents(string relayUrl)
    {
        if (!_relayStates.TryGetValue(relayUrl, out var rs)) return;

        rs.Cts = new CancellationTokenSource();
        var token = rs.Cts.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                rs.Ws = new ClientWebSocket();
                await rs.Ws.ConnectAsync(new Uri($"{relayUrl}/events"), token);
                rs.Connected = true;
                NotifyChanged();

                var buffer = new byte[65536];
                while (rs.Ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await rs.Ws.ReceiveAsync(buffer, token);
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    HandleEventMessage(json, relayUrl);
                    NotifyChanged();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
            }
            finally
            {
                rs.Ws?.Dispose();
                rs.Ws = null;
                rs.Connected = false;
                rs.Agents = null;
                if (!_disposing)
                    NotifyChanged();
            }

            if (!token.IsCancellationRequested)
            {
                rs.ReconnectAt = DateTime.UtcNow.AddSeconds(30);
                if (!_disposing) NotifyChanged();
                await Task.Delay(30000, token).ContinueWith(_ => { });
                rs.ReconnectAt = null;
            }
        }
    }

    private void HandleEventMessage(string json, string relayUrl)
    {
        if (!_relayStates.TryGetValue(relayUrl, out var rs)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProp))
            {
                _msg.Warn($"Unknown event: {json[..Math.Min(json.Length, 200)]}");
                return;
            }

            var type = typeProp.GetString();

            if (type == "agents" && root.TryGetProperty("agents", out var agentsProp))
            {
                var agentArray = JsonSerializer.Deserialize<AgentConnection[]>(agentsProp.GetRawText(), JsonOptions) ?? [];
                rs.Agents = new GroupInfo<AgentConnection>
                {
                    Count = agentArray.Length,
                    Connections = agentArray
                };

                FetchUuidsForAgents(agentArray, relayUrl);

                foreach (var id in _wm.ConnectedAgentIds.ToList())
                {
                    if (!GetAllAgents().Any(a => a.Agent.Id == id))
                        _ = _wm.DisconnectAgent(id);
                }
            }
            else
            {
                var agent = root.TryGetProperty("agent", out var agentProp)
                    ? JsonSerializer.Deserialize<AgentConnection>(agentProp.GetRawText(), JsonOptions)
                    : null;

                if (rs.Agents is null) return;

                if (type == "agent_connected" && agent is not null)
                {
                    var list = rs.Agents.Connections.ToList();
                    list.Add(agent);
                    rs.Agents = new GroupInfo<AgentConnection>
                    {
                        Count = list.Count,
                        Connections = list.ToArray()
                    };

                    _ = FetchUuid(agent, relayUrl);
                }
                else if (type == "agent_disconnected" && agent is not null)
                {
                    var list = rs.Agents.Connections.Where(a => a.Id != agent.Id).ToList();
                    rs.Agents = new GroupInfo<AgentConnection>
                    {
                        Count = list.Count,
                        Connections = list.ToArray()
                    };

                    var uuid = _agentDb.GetUuidByAgentId(agent.Id);
                    if (uuid is not null)
                        _ = _agentDb.RemoveAsync(uuid);

                    if (_wm.ConnectedAgentIds.Contains(agent.Id))
                    {
                        _ = _wm.DisconnectAgent(agent.Id);
                        _msg.Warn("Agent disconnected.");
                    }
                }
                else if (type == "agent_relayed" && agent is not null)
                {
                    var existing = rs.Agents.Connections.FirstOrDefault(a => a.Id == agent.Id);
                    if (existing is not null)
                    {
                        existing.Relayed = true;
                        existing.RelayId = agent.RelayId;
                        rs.Agents = new GroupInfo<AgentConnection>
                        {
                            Count = rs.Agents.Count,
                            Connections = rs.Agents.Connections.ToArray()
                        };
                    }
                }
                else if (type == "agent_unrelayed" && agent is not null)
                {
                    var existing = rs.Agents.Connections.FirstOrDefault(a => a.Id == agent.Id);
                    if (existing is not null)
                    {
                        existing.Relayed = false;
                        existing.RelayId = null;
                        rs.Agents = new GroupInfo<AgentConnection>
                        {
                            Count = rs.Agents.Count,
                            Connections = rs.Agents.Connections.ToArray()
                        };
                    }

                    if (_wm.ConnectedAgentIds.Contains(agent.Id))
                    {
                        _ = _wm.DisconnectAgent(agent.Id);
                        _msg.Warn("Agent relay disconnected.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _msg.Error($"Event parse error: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        _relayStore.OnChanged -= OnRelayStoreChanged;
        _downloads.OnItemQueued -= OnItemQueued;
        _scans.OnItemQueued -= OnScanItemQueued;
        foreach (var rs in _relayStates.Values)
        {
            rs.Cts?.Cancel();
            if (rs.Ws is not null)
            {
                rs.Ws.Dispose();
                rs.Ws = null;
            }
            rs.Cts?.Dispose();
        }
        _relayStates.Clear();
    }
}
