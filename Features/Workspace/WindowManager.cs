using cc.Features.Relay;


namespace cc.Features.Workspace;

public class WindowManager
{
    private int _nextId;
    private int _topZ = 1040;

    public List<WindowState> Windows { get; } = new();

    public event Action? OnChanged;

    public void NotifyChanged() => OnChanged?.Invoke();

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
        OnChanged?.Invoke();
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
        Windows.Add(new WindowState
        {
            Id = _nextId++,
            Panel = panel,
            Title = DisplayTitle(panel),
            X = 100 + offset,
            Y = 80 + offset,
            Width = panel switch { "FileManager" => 800, "Settings" => 650, "Relay" => 900, "Agents" => 950, "Downloads" => 600, "SearchJobs" => 700, "ExtensionGroups" => 500, _ => 700 },
            Height = 500,
            ZIndex = _topZ
        });
        OnChanged?.Invoke();
    }

    public void OpenAgentWindow(string panel, string agentId, RelaySocket? relay = null, string? agentName = null, string? agentUuid = null, string? searchPath = null)
    {
        // For FileSearch, don't reuse — each search path gets its own window
        if (panel != "FileSearch")
        {
            var existing = Windows.FirstOrDefault(w => w.Panel == panel && w.AgentId == agentId);
            if (existing is not null)
            {
                existing.Minimized = false;
                BringToFront(existing);
                return;
            }
        }

        _topZ++;
        var offset = Windows.Count * 30;
        Windows.Add(new WindowState
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
            Width = panel switch { "FileManager" => 800, "FileSearch" => 600, "AgentInfo" => 480, "Shell" => 700, _ => 700 },
            Height = 500,
            ZIndex = _topZ
        });
        OnChanged?.Invoke();
    }

    public async Task CloseWindow(WindowState win)
    {
        Windows.Remove(win);
        // Only disconnect relay if no other window still uses it
        if (win.Relay is not null && !Windows.Any(w => w.Relay == win.Relay))
            await win.Relay.Disconnect();
        OnChanged?.Invoke();
    }

    public async Task CloseAllWindows()
    {
        foreach (var win in Windows)
        {
            if (win.Relay is not null)
                await win.Relay.Disconnect();
        }
        Windows.Clear();
        OnChanged?.Invoke();
    }

    public void ToggleMinimize(WindowState win)
    {
        win.Minimized = !win.Minimized;
        OnChanged?.Invoke();
    }

    public void ToggleMaximize(WindowState win)
    {
        win.Maximized = !win.Maximized;
        if (win.Maximized) win.Minimized = false;
        OnChanged?.Invoke();
    }

    private static string DisplayTitle(string panel) => panel switch
    {
        "Uploads" => "File Uploads",
        "Downloads" => "Download Manager",
        "FileSearch" => "File Search",
        "SearchJobs" => "Search Jobs",
        "ExtensionGroups" => "Extension Groups",
        "FileManager" => "File Manager",
        "AgentInfo" => "Agent Info",
        "Shell" => "Shell",
        "Settings" => "Settings",
        _ => panel
    };

    public async Task DisconnectAgent(string agentId)
    {
        var agentWindows = Windows.Where(w => w.AgentId == agentId).ToList();
        foreach (var win in agentWindows)
        {
            if (win.Relay is not null)
                await win.Relay.Disconnect();
        }
        OnChanged?.Invoke();
    }
}
