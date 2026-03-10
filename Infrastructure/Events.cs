namespace cc.Infrastructure;

// ── Base ──────────────────────────────────────────────────────────────

public interface IEvent { }

// ── Agent events ──────────────────────────────────────────────────────

/// <summary>Fired when agent UUID is resolved and agent is reachable.</summary>
public record AgentOnlineEvent(string Uuid, string AgentId, string RelayUrl) : IEvent;

/// <summary>Fired when the relay receives an agent_connected message.</summary>
public record AgentConnectedEvent(string AgentId, string RelayUrl) : IEvent;

/// <summary>Fired when the relay receives an agent_disconnected message.</summary>
public record AgentDisconnectedEvent(string AgentId, string? Uuid, string RelayUrl) : IEvent;

/// <summary>Fired when agent pairing state changes.</summary>
public record AgentPairingChangedEvent(string AgentId, bool Paired, string RelayUrl) : IEvent;

// ── Relay events ──────────────────────────────────────────────────────

/// <summary>Relay WebSocket connected or disconnected.</summary>
public record RelayConnectionChangedEvent(string RelayUrl, bool Connected) : IEvent;

/// <summary>Relay agent list was updated (full refresh).</summary>
public record RelayAgentsChangedEvent(string RelayUrl) : IEvent;

/// <summary>Relay store configuration changed (add/remove/enable/disable).</summary>
public record RelayStoreChangedEvent : IEvent;

// ── Window events ─────────────────────────────────────────────────────

public record WindowChangedEvent : IEvent;

// ── Store change events ───────────────────────────────────────────────

public record DownloadStoreChangedEvent : IEvent;

public record DownloadItemQueuedEvent(string AgentUuid) : IEvent;

public record SearchStoreChangedEvent : IEvent;

public record SearchItemQueuedEvent(string AgentUuid) : IEvent;

public record NotificationStoreChangedEvent : IEvent;

public record ServiceStateChangedEvent : IEvent;

public record ExtensionGroupStoreChangedEvent : IEvent;

// ── Theme ─────────────────────────────────────────────────────────────

public record ThemeChangedEvent : IEvent;

// ── Notifications (transient messages) ────────────────────────────────

public record NotificationEvent(string Text, MessageType Type) : IEvent;

// ── Cache / VFS ───────────────────────────────────────────────────────

public record CacheChangedEvent : IEvent;

public record VfsChangedEvent : IEvent;
