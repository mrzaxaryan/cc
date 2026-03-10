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
    private readonly CacheManager _cache;
    private readonly WindowManager _wm;
    private readonly MessageService _msg;

    private readonly Dictionary<string, RelayState> _relayStates = new();
    private bool _started;
    private bool _disposing;

    public event Action? OnChanged;

    public IReadOnlyDictionary<string, RelayState> RelayStates => _relayStates;

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
        CacheManager cache, WindowManager wm, MessageService msg)
    {
        _relayStore = relayStore;
        _agentDb = agentDb;
        _downloads = downloads;
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
        _relayStore.OnChanged += OnRelayStoreChanged;
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
        var currentUrls = _relayStore.Relays.Select(r => r.Url).ToHashSet();

        foreach (var relay in _relayStore.Relays)
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

        var removedUrls = _relayStates.Keys.Where(url => !currentUrls.Contains(url)).ToList();
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

    // --- UUID fetching ---

    private async Task FetchUuid(AgentConnection agent, string relayUrl)
    {
        var relay = new RelaySocket { BaseUrl = relayUrl };
        try
        {
            await relay.Connect(agent.Id);
            var response = await relay.SendAndReceive(new byte[] { 0x00 });
            if (response is not null && response.Length >= 20)
            {
                var status = RelaySocket.ReadStatus(response);
                if (status == 0)
                {
                    var uuid = RelaySocket.ReadUuid(response).ToString();
                    var relayEntry = _relayStore.GetByUrl(relayUrl);
                    await _agentDb.UpsertAsync(uuid, agent, relayEntry?.Id ?? "");
                    NotifyChanged();
                    _ = AutoResumeDownloads(uuid, agent.Id, relayUrl);
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
                _ = AutoResumeDownloads(existingUuid, agent.Id, relayUrl);
        }
    }

    private async Task AutoResumeDownloads(string uuid, string agentId, string relayUrl)
    {
        if (!_cache.HasDirectory) return;
        await _downloads.LoadAsync();

        var paused = _downloads.GetByAgent(uuid)
            .Where(r => r.Status == DownloadStatus.Paused)
            .ToList();
        if (paused.Count == 0) return;

        RelaySocket? relay = null;
        try
        {
            relay = await CreateRelay(agentId, relayUrl);
            if (relay is null) return;

            foreach (var dl in paused)
            {
                if (_disposing) break;

                var cts = _downloads.RegisterCts(dl.Id);
                try
                {
                    var success = await _cache.DownloadFromAgentAsync(
                        relay, dl.RemotePath, dl.CacheSubPath,
                        dl.DownloadedSize, cts.Token,
                        async (downloaded, _) =>
                        {
                            await _downloads.UpdateProgressAsync(dl.Id, downloaded);
                        });

                    if (success)
                        await _downloads.CompleteAsync(dl.Id);
                    else
                        await _downloads.FailAsync(dl.Id, "Download returned failure");
                }
                catch (OperationCanceledException)
                {
                    await _downloads.PauseAsync(dl.Id);
                }
                catch (Exception ex)
                {
                    if (cts.IsCancellationRequested)
                        await _downloads.PauseAsync(dl.Id);
                    else
                        await _downloads.FailAsync(dl.Id, ex.Message);
                }
                finally
                {
                    _downloads.RemoveCts(dl.Id);
                }
            }
        }
        catch { }
        finally
        {
            if (relay is not null && !_wm.Windows.Any(w => w.Relay == relay))
                await relay.Disconnect();
        }
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
