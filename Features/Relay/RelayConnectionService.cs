using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using cc.Features.Workspace;
using cc.Features.Agents;
using cc.Infrastructure;

namespace cc.Features.Relay;

public class RelayConnectionService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly RelayStore _relayStore;
    private readonly AgentStore _agentDb;
    private readonly WindowManager _wm;
    private readonly MessageService _msg;

    private readonly Dictionary<string, RelayState> _relayStates = new();
    private bool _started;
    private bool _disposing;

    public event Action? OnChanged;
    public event Action<string, string, string>? OnAgentOnline; // (uuid, agentId, relayUrl)

    public IReadOnlyDictionary<string, RelayState> RelayStates => _relayStates;
    public bool IsDisposing => _disposing;

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
        RelayStore relayStore, AgentStore agentDb,
        WindowManager wm, MessageService msg)
    {
        _relayStore = relayStore;
        _agentDb = agentDb;
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

    /// <summary>Find an agent that is currently online, returning its agentId and relay URL.</summary>
    public (string AgentId, string RelayUrl)? FindOnlineAgent(string agentUuid)
    {
        var agent = _agentDb.GetByUuid(agentUuid);
        if (agent is null) return null;

        var relayEntry = !string.IsNullOrEmpty(agent.RelayStoreId) ? _relayStore.GetById(agent.RelayStoreId) : null;
        if (relayEntry is null) return null;

        if (!GetAllAgents().Any(a => a.Agent.Id == agent.AgentId)) return null;

        return (agent.AgentId, relayEntry.Url);
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
                    OnAgentOnline?.Invoke(uuid, agent.Id, relayUrl);
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
                OnAgentOnline?.Invoke(existingUuid, agent.Id, relayUrl);
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

            if (type is "agent_disconnected" or "agent_connected")
                Console.WriteLine($"[Event] {type}: {json[..Math.Min(json.Length, 500)]}");

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

                var eventAgentId = agent?.Id;
                if (string.IsNullOrEmpty(eventAgentId) && root.TryGetProperty("id", out var idProp))
                    eventAgentId = idProp.GetString();
                if (string.IsNullOrEmpty(eventAgentId) && root.TryGetProperty("agentId", out var agentIdProp))
                    eventAgentId = agentIdProp.GetString();

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
                else if (type == "agent_disconnected" && !string.IsNullOrEmpty(eventAgentId))
                {
                    var list = rs.Agents.Connections.Where(a => a.Id != eventAgentId).ToList();
                    rs.Agents = new GroupInfo<AgentConnection>
                    {
                        Count = list.Count,
                        Connections = list.ToArray()
                    };

                    var uuid = _agentDb.GetUuidByAgentId(eventAgentId);
                    if (uuid is not null)
                        _ = _agentDb.MarkOfflineAsync(uuid);

                    if (_wm.ConnectedAgentIds.Contains(eventAgentId))
                    {
                        _ = _wm.DisconnectAgent(eventAgentId);
                        _msg.Warn("Agent disconnected.");
                    }
                }
                else if (type is "agent_paired" or "agent_unpaired" && !string.IsNullOrEmpty(eventAgentId))
                {
                    var paired = type == "agent_paired";
                    var existing = rs.Agents.Connections.FirstOrDefault(a => a.Id == eventAgentId);
                    if (existing is not null)
                    {
                        existing.Paired = paired;
                        existing.PairedRelayId = paired && root.TryGetProperty("relayId", out var relayIdProp)
                            ? relayIdProp.GetString() : null;
                        rs.Agents = new GroupInfo<AgentConnection>
                        {
                            Count = rs.Agents.Count,
                            Connections = rs.Agents.Connections.ToArray()
                        };
                    }

                    if (!paired && _wm.ConnectedAgentIds.Contains(eventAgentId))
                    {
                        _ = _wm.DisconnectAgent(eventAgentId);
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
