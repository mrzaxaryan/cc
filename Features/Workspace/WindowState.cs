using C2.Features.Relay;

namespace C2.Features.Workspace;

/// <summary>Tracks position, size, and context for a single workspace window/panel.</summary>
public class WindowState
{
    public int Id { get; set; }
    /// <summary>Component type name rendered inside this window (e.g. "FileSystem", "Shell").</summary>
    public string Panel { get; set; } = "";
    public string Title { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Minimized { get; set; }
    public bool Maximized { get; set; }
    public int ZIndex { get; set; }
    /// <summary>Transient connection-scoped agent ID (changes each session).</summary>
    public string? AgentId { get; set; }
    public string? AgentName { get; set; }
    /// <summary>Persistent agent UUID stored in IndexedDB (stable across sessions).</summary>
    public string? AgentUuid { get; set; }
    /// <summary>Relay WebSocket connection this window operates through.</summary>
    public RelaySocket? Relay { get; set; }
    /// <summary>Root path for file search operations scoped to this window.</summary>
    public string? SearchPath { get; set; }

    // --- Snap state ---
    /// <summary>Position/size before snapping, for restore-on-unsnap.</summary>
    public double PreSnapX { get; set; }
    public double PreSnapY { get; set; }
    public double PreSnapWidth { get; set; }
    public double PreSnapHeight { get; set; }
    public bool IsSnapped { get; set; }
    /// <summary>Active snap zone: left-half, right-half, top-left, top-right, bottom-left, bottom-right, maximize.</summary>
    public string? SnapZone { get; set; }
}
