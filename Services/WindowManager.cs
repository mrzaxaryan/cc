using cc.Models;

namespace cc.Services;

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
            Title = panel,
            X = 100 + offset,
            Y = 80 + offset,
            Width = panel switch { "File Manager" => 800, "Cache Manager" => 600, "Relay" => 900, "Agents" => 950, _ => 700 },
            Height = 500,
            ZIndex = _topZ
        });
        OnChanged?.Invoke();
    }

    public void OpenAgentWindow(string panel, string agentId, RelaySocket? relay = null)
    {
        // Reuse existing window for same panel + agent
        var existing = Windows.FirstOrDefault(w => w.Panel == panel && w.AgentId == agentId);
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
            Title = $"{panel} — {agentId}",
            AgentId = agentId,
            Relay = relay,
            X = 100 + offset,
            Y = 80 + offset,
            Width = panel switch { "File Manager" => 800, _ => 700 },
            Height = 500,
            ZIndex = _topZ
        });
        OnChanged?.Invoke();
    }

    public async Task CloseWindow(WindowState win)
    {
        Windows.Remove(win);
        if (win.Relay is not null)
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
        if (win.Minimized) win.Maximized = false;
        OnChanged?.Invoke();
    }

    public void ToggleMaximize(WindowState win)
    {
        win.Maximized = !win.Maximized;
        if (win.Maximized) win.Minimized = false;
        OnChanged?.Invoke();
    }

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
