using System.Text.Json.Serialization;
using cc.Models;
using Microsoft.JSInterop;

namespace cc.Services;

public class AgentRecord
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("agentId")] public string AgentId { get; set; } = "";
    [JsonPropertyName("relayStoreId")] public string RelayStoreId { get; set; } = "";
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    [JsonPropertyName("country")] public string Country { get; set; } = "";
    [JsonPropertyName("city")] public string City { get; set; } = "";
    [JsonPropertyName("region")] public string Region { get; set; } = "";
    [JsonPropertyName("continent")] public string Continent { get; set; } = "";
    [JsonPropertyName("timezone")] public string Timezone { get; set; } = "";
    [JsonPropertyName("asn")] public int Asn { get; set; }
    [JsonPropertyName("asOrganization")] public string AsOrganization { get; set; } = "";
    [JsonPropertyName("userAgent")] public string UserAgent { get; set; } = "";
    [JsonPropertyName("requestPriority")] public string RequestPriority { get; set; } = "";
    [JsonPropertyName("tlsVersion")] public string TlsVersion { get; set; } = "";
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("arch")] public string Arch { get; set; } = "";
    [JsonPropertyName("pairedRelayId")] public string? PairedRelayId { get; set; }
    [JsonPropertyName("firstSeen")] public double FirstSeen { get; set; }
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
            var records = await _js.InvokeAsync<AgentRecord[]>("ccAgentDb.getAll");
            _cache = records.ToDictionary(r => r.Uuid);
            foreach (var r in _cache.Values)
                _agentIdToUuid[r.AgentId] = r.Uuid;
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
            AgentId = agent.Id,
            RelayStoreId = relayStoreId,
            Ip = agent.Ip,
            Country = agent.Country,
            City = agent.City,
            Region = agent.Region,
            Continent = agent.Continent,
            Timezone = agent.Timezone,
            Asn = agent.Asn,
            AsOrganization = agent.AsOrganization,
            UserAgent = agent.UserAgent,
            RequestPriority = agent.RequestPriority,
            TlsVersion = agent.TlsVersion,
            Os = agent.Os,
            Arch = agent.Arch,
            PairedRelayId = agent.PairedRelayId,
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
        await _js.InvokeVoidAsync("ccAgentDb.put", record);
    }

    public string? GetUuidByAgentId(string agentId)
    {
        return _agentIdToUuid.TryGetValue(agentId, out var uuid) ? uuid : null;
    }

    public AgentRecord? GetByUuid(string uuid)
    {
        return _cache.TryGetValue(uuid, out var record) ? record : null;
    }

    public async Task MarkOfflineAsync(string uuid)
    {
        if (!_cache.TryGetValue(uuid, out var record)) return;
        record.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _js.InvokeVoidAsync("ccAgentDb.put", record);
    }

    public async Task RenameAsync(string uuid, string name)
    {
        if (!_cache.TryGetValue(uuid, out var record)) return;
        record.Name = name;
        await _js.InvokeVoidAsync("ccAgentDb.put", record);
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
        await _js.InvokeVoidAsync("ccAgentDb.remove", uuid);
    }

    public async Task ClearAsync()
    {
        _cache.Clear();
        _agentIdToUuid.Clear();
        await _js.InvokeVoidAsync("ccAgentDb.clear");
    }
}
