using cc.Models;
using Microsoft.JSInterop;

namespace cc.Services;

public class AgentRecord
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string RelayUrl { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public string Continent { get; set; } = "";
    public string Timezone { get; set; } = "";
    public int Asn { get; set; }
    public string AsOrganization { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string TlsVersion { get; set; } = "";
    public double FirstSeen { get; set; }
    public double LastSeen { get; set; }
}

public class AgentStore
{
    private readonly IJSRuntime _js;
    private Dictionary<string, AgentRecord> _cache = new();
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
        }
        catch
        {
            _cache = new();
        }
    }

    public async Task UpsertAsync(string uuid, AgentConnection agent, string relayUrl)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var record = new AgentRecord
        {
            Uuid = uuid,
            Name = $"Agent #{_cache.Count + 1}",
            AgentId = agent.Id,
            RelayUrl = relayUrl,
            Ip = agent.Ip,
            Country = agent.Country,
            City = agent.City,
            Region = agent.Region,
            Continent = agent.Continent,
            Timezone = agent.Timezone,
            Asn = agent.Asn,
            AsOrganization = agent.AsOrganization,
            UserAgent = agent.UserAgent,
            Protocol = agent.Protocol,
            TlsVersion = agent.TlsVersion,
            FirstSeen = now,
            LastSeen = now
        };

        if (_cache.TryGetValue(uuid, out var existing))
        {
            record.Name = existing.Name;
            record.FirstSeen = existing.FirstSeen;
        }

        _cache[uuid] = record;
        await _js.InvokeVoidAsync("ccAgentDb.put", record);
    }

    public string? GetUuidByAgentId(string agentId)
    {
        foreach (var record in _cache.Values)
        {
            if (record.AgentId == agentId)
                return record.Uuid;
        }
        return null;
    }

    public AgentRecord? GetByUuid(string uuid)
    {
        return _cache.TryGetValue(uuid, out var record) ? record : null;
    }

    public async Task RenameAsync(string uuid, string name)
    {
        if (!_cache.TryGetValue(uuid, out var record)) return;
        record.Name = name;
        await _js.InvokeVoidAsync("ccAgentDb.put", record);
    }

    public async Task RemoveAsync(string uuid)
    {
        _cache.Remove(uuid);
        await _js.InvokeVoidAsync("ccAgentDb.remove", uuid);
    }

    public async Task ClearAsync()
    {
        _cache.Clear();
        await _js.InvokeVoidAsync("ccAgentDb.clear");
    }
}
