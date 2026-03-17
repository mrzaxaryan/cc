using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using C2.Features.Workspace;
using C2.Features.Agents;
using C2.Infrastructure;

namespace C2.Features.Relay;

public class RelayConnectionService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly RelayStore _relayStore;
    private readonly AgentStore _agentDb;
    private readonly WindowManager _wm;
    private readonly MessageService _msg;
    private readonly IEventBus _bus;

    private readonly Dictionary<string, RelayState> _relayStates = new();
    /// <summary>Agents whose system info fetch failed (e.g. relay was paired). Key: agentId, Value: relayUrl.</summary>
    private readonly Dictionary<string, string> _pendingInfoFetch = new();
    private IDisposable? _relayStoreSub;
    private bool _started;
    private bool _disposing;

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
        WindowManager wm, MessageService msg, IEventBus bus)
    {
        _relayStore = relayStore;
        _agentDb = agentDb;
        _wm = wm;
        _msg = msg;
        _bus = bus;
    }

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        await _relayStore.LoadAsync();
        await _agentDb.LoadAsync();
        _relayStoreSub = _bus.Subscribe<RelayStoreChangedEvent>(_ =>
        {
            SyncRelays();
            _bus.Publish(new RelayAgentsChangedEvent(""));
        });
        SyncRelays();
    }

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
            // Capture state before removing so DisconnectRelay can still use it
            if (_relayStates.TryGetValue(url, out var removedState))
            {
                removedState.Cts?.Cancel();
                if (removedState.Agents?.Connections is not null)
                {
                    foreach (var agent in removedState.Agents.Connections)
                    {
                        if (_wm.ConnectedAgentIds.Contains(agent.Id))
                            _ = _wm.DisconnectAgent(agent.Id);
                    }
                }
                removedState.Agents = null;
            }
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

        var agentId = _agentDb.GetAgentIdByUuid(agentUuid);
        if (agentId is null || !GetAllAgents().Any(a => a.Agent.Id == agentId)) return null;

        return (agentId, relayEntry.Url);
    }

    public async Task<(int Disconnected, string[] AgentIds)?> DisconnectAllAgents(string relayUrl)
    {
        var httpUrl = RelayStore.GetHttpBaseUrl(relayUrl);
        using var client = new HttpClient();
        var token = _relayStore.GetTokenByUrl(relayUrl);
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.PostAsync($"{httpUrl}/disconnect-all-agents", null);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<DisconnectAllResult>(json, JsonOptions);
        return result is not null ? (result.Disconnected, result.AgentIds) : null;
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

        var relay = new RelaySocket { BaseUrl = relayUrl, Token = _relayStore.GetTokenByUrl(relayUrl) };
        try
        {
            await relay.Connect(agentId);
            return relay;
        }
        catch (Exception ex)
        {
            _msg.Error("Connection Failed", $"Could not connect to agent: {ex.Message}", "Relay");
            await relay.Disconnect();
            return null;
        }
    }

    // --- UUID fetching ---

    private async Task FetchUuid(AgentConnection agent, string relayUrl, int attempt = 0)
    {
        const int maxRetries = 2;
        var relay = new RelaySocket { BaseUrl = relayUrl, Token = _relayStore.GetTokenByUrl(relayUrl) };
        try
        {
            await relay.Connect(agent.Id);
            var response = await relay.SendAndReceive(new byte[] { AgentCommands.SystemInfo });
            if (response is not null && response.Length >= 4 + RelaySocket.SystemInfoSize)
            {
                var status = RelaySocket.ReadStatus(response);
                if (status == 0)
                {
                    var (guid, hostname, architecture, platform, buildNumber, commitHash) = RelaySocket.ReadSystemInfo(response);
                    var uuid = guid.ToString();
                    agent.Hostname = hostname;
                    agent.Os = platform;
                    agent.Arch = architecture;
                    agent.BuildNumber = buildNumber;
                    agent.CommitHash = commitHash;

                    // Also update the live agent in the current connections array
                    // in case it was replaced by a newer "agents" event
                    if (_relayStates.TryGetValue(relayUrl, out var rs) && rs.Agents?.Connections is not null)
                    {
                        var liveAgent = rs.Agents.Connections.FirstOrDefault(a => a.Id == agent.Id);
                        if (liveAgent is not null && liveAgent != agent)
                        {
                            liveAgent.Hostname = hostname;
                            liveAgent.Os = platform;
                            liveAgent.Arch = architecture;
                            liveAgent.BuildNumber = buildNumber;
                            liveAgent.CommitHash = commitHash;
                        }
                    }

                    _pendingInfoFetch.Remove(agent.Id);
                    var relayEntry = _relayStore.GetByUrl(relayUrl);
                    await _agentDb.UpsertAsync(uuid, agent, relayEntry?.Id ?? "");
                    _bus.Publish(new RelayAgentsChangedEvent(relayUrl));
                    _bus.Publish(new AgentOnlineEvent(uuid, agent.Id, relayUrl));
                    return;
                }
            }

            // Response was null, too short, or bad status — retry
            if (attempt < maxRetries)
            {
                await relay.Disconnect();
                await Task.Delay(1000 * (attempt + 1));
                await FetchUuid(agent, relayUrl, attempt + 1);
            }
            else
            {
                _pendingInfoFetch[agent.Id] = relayUrl;
            }
        }
        catch when (attempt < maxRetries)
        {
            await relay.Disconnect();
            await Task.Delay(1000 * (attempt + 1));
            await FetchUuid(agent, relayUrl, attempt + 1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FetchUuid] Failed for {agent.Id} after {attempt + 1} attempts: {ex.Message}");
            _pendingInfoFetch[agent.Id] = relayUrl;
        }
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
                _bus.Publish(new AgentOnlineEvent(existingUuid, agent.Id, relayUrl));
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
            // Use a local variable so that if ForceReconnect starts a new
            // ConnectToEvents, the old finally block won't dispose the new websocket.
            ClientWebSocket? ws = null;
            try
            {
                ws = new ClientWebSocket();
                rs.Ws = ws;
                var eventsUri = $"{RelayStore.GetWsBaseUrl(relayUrl)}/events";
                var authToken = _relayStore.GetTokenByUrl(relayUrl);
                if (!string.IsNullOrEmpty(authToken))
                    eventsUri += $"?token={Uri.EscapeDataString(authToken)}";
                await ws.ConnectAsync(new Uri(eventsUri), token);
                rs.Connected = true;
                _bus.Publish(new RelayConnectionChangedEvent(relayUrl, true));

                var buffer = new byte[65536];
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, token);
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    HandleEventMessage(json, relayUrl);
                    _bus.Publish(new RelayAgentsChangedEvent(relayUrl));
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
                ws?.Dispose();
                // Only clear shared state if we're still the active connection
                if (rs.Ws == ws)
                {
                    rs.Ws = null;
                    rs.Connected = false;
                    rs.Agents = null;
                    if (!_disposing)
                        _bus.Publish(new RelayConnectionChangedEvent(relayUrl, false));
                }
            }

            if (!token.IsCancellationRequested)
            {
                rs.ReconnectAt = DateTime.UtcNow.AddSeconds(30);
                if (!_disposing) _bus.Publish(new RelayAgentsChangedEvent(relayUrl));
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
                _msg.Warn("Unknown Event", $"Unrecognized relay event: {json[..Math.Min(json.Length, 200)]}", "Relay");
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
                    _bus.Publish(new AgentConnectedEvent(agent.Id, relayUrl));
                }
                else if (type == "agent_disconnected" && !string.IsNullOrEmpty(eventAgentId))
                {
                    _pendingInfoFetch.Remove(eventAgentId);

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
                        var disconnectedUuid = _agentDb.GetUuidByAgentId(eventAgentId);
                        var disconnectedName = disconnectedUuid is not null ? _agentDb.GetDisplayName(disconnectedUuid) : eventAgentId;
                        _msg.Warn("Agent Disconnected", $"{disconnectedName} has gone offline", "Relay");
                    }

                    _bus.Publish(new AgentDisconnectedEvent(eventAgentId, uuid, relayUrl));
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

                    if (!paired)
                    {
                        // Relay is now free — if info fetch is still pending, retry
                        if (existing is not null && _pendingInfoFetch.Remove(eventAgentId))
                            _ = FetchUuid(existing, relayUrl);

                        // Reconnect any open windows for this agent that have no active relay
                        _ = ReconnectWindowsForAgent(eventAgentId, relayUrl);
                    }

                    _bus.Publish(new AgentPairingChangedEvent(eventAgentId, paired, relayUrl));
                }
            }
        }
        catch (Exception ex)
        {
            _msg.Error("Event Parse Error", $"Failed to parse relay event: {ex.Message}", "Relay");
        }
    }

    /// <summary>
    /// When an agent becomes unpaired, reconnect any open windows that have
    /// a disconnected or null relay for that agent.
    /// </summary>
    private async Task ReconnectWindowsForAgent(string agentId, string relayUrl)
    {
        var windowsToReconnect = _wm.Windows
            .Where(w => w.AgentId == agentId && w.Relay is not { IsConnected: true })
            .ToList();

        if (windowsToReconnect.Count == 0) return;

        var relay = await CreateRelay(agentId, relayUrl);
        if (relay is null) return;

        foreach (var win in windowsToReconnect)
        {
            win.Relay = relay;
        }

        _bus.Publish(new WindowChangedEvent());
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        _relayStoreSub?.Dispose();
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
