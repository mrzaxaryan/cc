namespace cc.Models;

public class RelayStatus
{
    public GroupInfo<AgentConnection> Agents { get; set; } = new();
    public GroupInfo<RelayConnection> Relays { get; set; } = new();
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
    public string Ip { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public int MessageCount { get; set; }
    public double LastActiveAt { get; set; }
}

public class RelayConnection
{
    public string Id { get; set; } = "";
    public double ConnectedAt { get; set; }
    public string ClientId { get; set; } = "";
}
