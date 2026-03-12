namespace C2.Features.Relay;

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
    /// <summary>Unix timestamp (ms) when the agent connected.</summary>
    public double ConnectedAt { get; set; }
    public bool Paired { get; set; }
    public string? PairedRelayId { get; set; }
    public int MessagesForwarded { get; set; }
    /// <summary>Unix timestamp (ms) of the last activity.</summary>
    public double LastActiveAt { get; set; }
    /// <summary>IP (Internet Protocol) address of the agent.</summary>
    public string Ip { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public string Continent { get; set; } = "";
    public string Timezone { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Latitude { get; set; } = "";
    public string Longitude { get; set; } = "";
    /// <summary>ASN (Autonomous System Number) of the agent's network.</summary>
    public int Asn { get; set; }
    /// <summary>Organization name associated with the ASN (Autonomous System).</summary>
    public string AsOrganization { get; set; } = "";
    public string UserAgent { get; set; } = "";
    /// <summary>HTTP request priority hint (e.g. "u=0", "u=1").</summary>
    public string RequestPriority { get; set; } = "";
    /// <summary>TLS (Transport Layer Security) version used (e.g. "TLSv1.3").</summary>
    public string TlsVersion { get; set; } = "";
    /// <summary>HTTP protocol version (e.g. "h2", "HTTP/2").</summary>
    public string HttpVersion { get; set; } = "";
    /// <summary>Operating system of the agent (e.g. "Windows", "Linux").</summary>
    public string Os { get; set; } = "";
    /// <summary>CPU architecture of the agent (e.g. "x64", "arm64").</summary>
    public string Arch { get; set; } = "";
}

public class RelayConnection
{
    public string Id { get; set; } = "";
    public double ConnectedAt { get; set; }
    public string PairedAgentId { get; set; } = "";
}

public class EventListenerConnection
{
    public string Id { get; set; } = "";
    /// <summary>Unix timestamp (ms) when the listener connected.</summary>
    public double ConnectedAt { get; set; }
    /// <summary>IP (Internet Protocol) address of the listener.</summary>
    public string Ip { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public string Continent { get; set; } = "";
    public string Timezone { get; set; } = "";
    /// <summary>ASN (Autonomous System Number) of the listener's network.</summary>
    public int Asn { get; set; }
    /// <summary>Organization name associated with the ASN (Autonomous System).</summary>
    public string AsOrganization { get; set; } = "";
    public string UserAgent { get; set; } = "";
    /// <summary>TLS (Transport Layer Security) version used (e.g. "TLSv1.3").</summary>
    public string TlsVersion { get; set; } = "";
    /// <summary>HTTP protocol version (e.g. "h2", "HTTP/2").</summary>
    public string HttpVersion { get; set; } = "";
}

// WebSocket event models
public class AgentEvent
{
    public string Type { get; set; } = "";
    public AgentConnection? Agent { get; set; }
}
