namespace cc.Models;

public class HealthStatus
{
    public GroupInfo<AgentConnection> Agents { get; set; } = new();
    public GroupInfo<RelayConnection> Relays { get; set; } = new();
    public GroupInfo<EventListenerConnection> EventListeners { get; set; } = new();
}

public class GroupInfo<T>
{
    public int Count { get; set; }
    public T[] Connections { get; set; } = [];
}

public class AgentConnection
{
    public string Id { get; set; } = "";
    public double ConnectedAt { get; set; }
    public bool Relayed { get; set; }
    public string? RelayId { get; set; }
    public int MessageCount { get; set; }
    public double LastActiveAt { get; set; }
    public string Ip { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public string Continent { get; set; } = "";
    public string Timezone { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Latitude { get; set; } = "";
    public string Longitude { get; set; } = "";
    public int Asn { get; set; }
    public string AsOrganization { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string TlsVersion { get; set; } = "";
    public string HttpVersion { get; set; } = "";
    public string Os { get; set; } = "";
    public string Arch { get; set; } = "";
}

public class RelayConnection
{
    public string Id { get; set; } = "";
    public double ConnectedAt { get; set; }
    public string AgentId { get; set; } = "";
}

public class EventListenerConnection
{
    public string Id { get; set; } = "";
    public double ConnectedAt { get; set; }
    public string Ip { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public string UserAgent { get; set; } = "";
}

// WebSocket event models
public class AgentEvent
{
    public string Type { get; set; } = "";
    public AgentConnection? Agent { get; set; }
}
