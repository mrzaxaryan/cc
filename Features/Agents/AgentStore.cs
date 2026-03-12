using System.Text.Json.Serialization;
using C2.Features.Relay;
using Microsoft.JSInterop;

namespace C2.Features.Agents;

/// <summary>Persisted agent metadata stored in IndexedDB.</summary>
public class AgentRecord
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Key into the relay store identifying which relay this agent connects through.</summary>
    [JsonPropertyName("relayStoreId")] public string RelayStoreId { get; set; } = "";
    /// <summary>IP (Internet Protocol) address of the agent.</summary>
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    [JsonPropertyName("country")] public string Country { get; set; } = "";
    [JsonPropertyName("city")] public string City { get; set; } = "";
    [JsonPropertyName("region")] public string Region { get; set; } = "";
    [JsonPropertyName("continent")] public string Continent { get; set; } = "";
    [JsonPropertyName("timezone")] public string Timezone { get; set; } = "";
    /// <summary>Operating system of the agent (e.g. "Windows", "Linux").</summary>
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    /// <summary>CPU architecture of the agent (e.g. "x64", "arm64").</summary>
    [JsonPropertyName("arch")] public string Arch { get; set; } = "";
    /// <summary>Unix timestamp (ms) when this agent was first observed.</summary>
    [JsonPropertyName("firstSeen")] public double FirstSeen { get; set; }
    /// <summary>Unix timestamp (ms) when this agent was last observed.</summary>
    [JsonPropertyName("lastSeen")] public double LastSeen { get; set; }
}

public class AgentStore
{
    private readonly IJSRuntime _js;
    private Dictionary<string, AgentRecord> _cache = new();
    private readonly Dictionary<string, string> _agentIdToUuid = new();
    private bool _loaded;

    public AgentStore(IJSRuntime js) => _js = js;

    public IReadOnlyDictionary<string, AgentRecord> Agents => _cache;

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var records = await _js.InvokeAsync<AgentRecord[]>("c2AgentDb.getAll");
            _cache = records.ToDictionary(r => r.Uuid);
        }
        catch
        {
            _cache = new();
        }
    }

    public async Task UpsertAsync(string uuid, AgentConnection agent, string relayStoreId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var record = new AgentRecord
        {
            Uuid = uuid,
            Name = $"Agent #{_cache.Count + 1}",
            RelayStoreId = relayStoreId,
            Ip = agent.Ip,
            Country = agent.Country,
            City = agent.City,
            Region = agent.Region,
            Continent = agent.Continent,
            Timezone = agent.Timezone,
            Os = agent.Os,
            Arch = agent.Arch,
            FirstSeen = now,
            LastSeen = now
        };

        if (_cache.TryGetValue(uuid, out var existing))
        {
            record.Name = existing.Name;
            record.FirstSeen = existing.FirstSeen;
        }

        _cache[uuid] = record;
        _agentIdToUuid[agent.Id] = uuid;
        await _js.InvokeVoidAsync("c2AgentDb.put", record);
    }

    public string? GetUuidByAgentId(string agentId)
    {
        return _agentIdToUuid.TryGetValue(agentId, out var uuid) ? uuid : null;
    }

    public string? GetAgentIdByUuid(string uuid)
    {
        foreach (var (agentId, u) in _agentIdToUuid)
            if (u == uuid) return agentId;
        return null;
    }

    public AgentRecord? GetByUuid(string uuid)
    {
        return _cache.TryGetValue(uuid, out var record) ? record : null;
    }

    public string GetDisplayName(string uuid) =>
        _cache.TryGetValue(uuid, out var r) ? r.Name : uuid[..Math.Min(8, uuid.Length)];

    public async Task MarkOfflineAsync(string uuid)
    {
        if (!_cache.TryGetValue(uuid, out var record)) return;
        record.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _js.InvokeVoidAsync("c2AgentDb.put", record);
    }

    public async Task RenameAsync(string uuid, string name)
    {
        if (!_cache.TryGetValue(uuid, out var record)) return;
        record.Name = name;
        await _js.InvokeVoidAsync("c2AgentDb.put", record);
    }

    public async Task RemoveAsync(string uuid)
    {
        if (_cache.TryGetValue(uuid, out var record))
        {
            // Remove all agentId mappings pointing to this uuid
            var keysToRemove = _agentIdToUuid.Where(kv => kv.Value == uuid).Select(kv => kv.Key).ToList();
            foreach (var key in keysToRemove)
                _agentIdToUuid.Remove(key);
        }
        _cache.Remove(uuid);
        await _js.InvokeVoidAsync("c2AgentDb.remove", uuid);
    }

    public async Task ClearAsync()
    {
        _cache.Clear();
        _agentIdToUuid.Clear();
        await _js.InvokeVoidAsync("c2AgentDb.clear");
    }
}
