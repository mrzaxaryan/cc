using C2.Features.Relay;
using C2.Infrastructure;

namespace C2.Features.Workspace;

public class WindowManager
{
    private readonly IEventBus _bus;
    private int _nextId;
    private int _topZ = 1040;
    private double _vpWidth;
    private double _vpHeight;

    public List<WindowState> Windows { get; } = new();

    public WindowManager(IEventBus bus) => _bus = bus;

    public void SetViewportSize(double width, double height)
    {
        _vpWidth = width;
        _vpHeight = height;
    }

    private void NotifyChanged() => _bus.Publish(new WindowChangedEvent());

    /// <summary>All agent IDs with active relay connections across windows.</summary>
    public IEnumerable<string> ConnectedAgentIds =>
        Windows.Where(w => w.Relay is { IsConnected: true } && w.AgentId is not null)
               .Select(w => w.AgentId!)
               .Distinct();

    public void BringToFront(WindowState win)
    {
        if (win.ZIndex == _topZ) return;
        _topZ++;
        win.ZIndex = _topZ;
        NotifyChanged();
    }

    public void OpenWindow(string panel)
    {
        var existing = Windows.FirstOrDefault(w => w.Panel == panel && w.AgentId is null);
        if (existing is not null)
        {
            existing.Minimized = false;
            BringToFront(existing);
            return;
        }

        _topZ++;
        var offset = Windows.Count * 30;
        var win = new WindowState
        {
            Id = _nextId++,
            Panel = panel,
            Title = DisplayTitle(panel),
            X = 100 + offset,
            Y = 80 + offset,
            Width = panel switch { "FileSystem" => 800, "Settings" => 650, "Relay" => 900, "Agents" => 950, "Transfers" => 600, "ExtensionGroups" => 500, "PeParser" => 900, "Base64" => 600, "LnkTool" => 750, "PythonLoader" => 650, _ => 700 },
            Height = 500,
            ZIndex = _topZ
        };
        ClampToViewport(win);
        Windows.Add(win);
        NotifyChanged();
    }

    public void OpenAgentWindow(string panel, string agentId, RelaySocket? relay = null, string? agentName = null, string? agentUuid = null, string? searchPath = null)
    {
        var existing = Windows.FirstOrDefault(w => w.Panel == panel && w.AgentId == agentId);
        if (existing is not null)
        {
            existing.Minimized = false;
            BringToFront(existing);
            return;
        }

        _topZ++;
        var offset = Windows.Count * 30;
        var win = new WindowState
        {
            Id = _nextId++,
            Panel = panel,
            Title = $"{DisplayTitle(panel)} — {agentName ?? agentId}",
            AgentId = agentId,
            AgentName = agentName,
            AgentUuid = agentUuid,
            Relay = relay,
            SearchPath = searchPath,
            X = 100 + offset,
            Y = 80 + offset,
            Width = panel switch { "FileSystem" => 800, "AgentInfo" => 480, "Shell" => 700, "Screen" => 900, _ => 700 },
            Height = panel switch { "Screen" => 600, _ => 500 },
            ZIndex = _topZ
        };
        ClampToViewport(win);
        Windows.Add(win);
        NotifyChanged();
    }

    public async Task CloseWindow(WindowState win)
    {
        Windows.Remove(win);
        // Only disconnect relay if no other window still uses it
        // and no background service (e.g. upload) is actively using it
        if (win.Relay is not null && !Windows.Any(w => w.Relay == win.Relay) && win.Relay.InUseCount <= 0)
            await win.Relay.Disconnect();
        NotifyChanged();
    }

    public async Task CloseAllWindows()
    {
        foreach (var win in Windows)
        {
            if (win.Relay is not null && win.Relay.InUseCount <= 0)
                await win.Relay.Disconnect();
        }
        Windows.Clear();
        NotifyChanged();
    }

    public void ToggleMinimize(WindowState win)
    {
        win.Minimized = !win.Minimized;
        NotifyChanged();
    }

    public void ToggleMaximize(WindowState win)
    {
        win.Maximized = !win.Maximized;
        if (win.Maximized) win.Minimized = false;
        NotifyChanged();
    }

    private void ClampToViewport(WindowState win)
    {
        if (_vpWidth <= 0 || _vpHeight <= 0) return;

        const double taskbarHeight = 40;
        var maxW = _vpWidth;
        var maxH = _vpHeight - taskbarHeight;

        if (win.Width > maxW) win.Width = maxW;
        if (win.Height > maxH) win.Height = maxH;

        if (win.X + win.Width > _vpWidth) win.X = Math.Max(0, _vpWidth - win.Width);
        if (win.Y + win.Height > _vpHeight - taskbarHeight) win.Y = Math.Max(0, _vpHeight - taskbarHeight - win.Height);
    }

    private static string DisplayTitle(string panel) => panel switch
    {
        "Transfers" => "Transfer Manager",
        "ExtensionGroups" => "Extension Groups",
        "FileSystem" => "File System",
        "AgentInfo" => "Agent Info",
        "Shell" => "Shell",
        "Screen" => "Screen",
        "Settings" => "Settings",
        "PythonLoader" => "Python Loader",
        _ => panel
    };

    public async Task DisconnectAgent(string agentId)
    {
        var agentWindows = Windows.Where(w => w.AgentId == agentId).ToList();
        foreach (var win in agentWindows)
        {
            if (win.Relay is not null && win.Relay.InUseCount <= 0)
                await win.Relay.Disconnect();
        }
        NotifyChanged();
    }

    // --- Silent updates (called from JS callbacks — DOM is already correct) ---

    public void UpdatePositionSilent(int id, double x, double y)
    {
        var win = Windows.FirstOrDefault(w => w.Id == id);
        if (win is null) return;
        win.X = x;
        win.Y = y;
        // No NotifyChanged — DOM already matches
    }

    public void UpdateGeometrySilent(int id, double x, double y, double width, double height)
    {
        var win = Windows.FirstOrDefault(w => w.Id == id);
        if (win is null) return;
        win.X = x;
        win.Y = y;
        win.Width = width;
        win.Height = height;
    }

    // --- Snap state ---

    public void ApplySnap(int id, double x, double y, double w, double h, string zone)
    {
        var win = Windows.FirstOrDefault(wi => wi.Id == id);
        if (win is null) return;

        // Save pre-snap geometry for restore
        if (!win.IsSnapped)
        {
            win.PreSnapX = win.X;
            win.PreSnapY = win.Y;
            win.PreSnapWidth = win.Width;
            win.PreSnapHeight = win.Height;
        }

        win.X = x;
        win.Y = y;
        win.Width = w;
        win.Height = h;
        win.IsSnapped = true;
        win.SnapZone = zone;
        win.Maximized = zone == "maximize";
    }

    public void ClearSnap(int id)
    {
        var win = Windows.FirstOrDefault(w => w.Id == id);
        if (win is null || !win.IsSnapped) return;

        win.X = win.PreSnapX;
        win.Y = win.PreSnapY;
        win.Width = win.PreSnapWidth;
        win.Height = win.PreSnapHeight;
        win.IsSnapped = false;
        win.SnapZone = null;
        win.Maximized = false;
    }

    // --- Tiling ---

    public void ApplyTilePositions(int[] ids, double[] xs, double[] ys, double[] ws, double[] hs)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            var win = Windows.FirstOrDefault(w => w.Id == ids[i]);
            if (win is null) continue;
            win.X = xs[i];
            win.Y = ys[i];
            win.Width = ws[i];
            win.Height = hs[i];
            win.Maximized = false;
            win.IsSnapped = false;
            win.SnapZone = null;
        }
    }

    // --- Persistence DTO ---

    public WindowLayoutDto[] ExportLayout()
    {
        return Windows.Select(w => new WindowLayoutDto
        {
            Panel = w.Panel,
            X = w.X, Y = w.Y,
            Width = w.Width, Height = w.Height,
            Minimized = w.Minimized,
            Maximized = w.Maximized,
            SnapZone = w.SnapZone,
            AgentUuid = w.AgentUuid
        }).ToArray();
    }
}

public class WindowLayoutDto
{
    public string Panel { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Minimized { get; set; }
    public bool Maximized { get; set; }
    public string? SnapZone { get; set; }
    public string? AgentUuid { get; set; }
}
