using cc.Models;

namespace cc.Services;

public class WindowManager
{
    private int _nextId;

    public List<WindowState> Windows { get; } = new();
    public RelaySocket Relay { get; } = new();

    // Command panel state
    public string CommandPath { get; set; } = "";
    public long ReadCount { get; set; } = 4096;
    public long FileOffset { get; set; }
    public long ChunkSize { get; set; } = 4096;
    public long HashOffset { get; set; }
    public string Output { get; set; } = "";
    public bool Sending { get; set; }

    public event Action? OnChanged;

    public void NotifyChanged() => OnChanged?.Invoke();

    public void OpenWindow(string panel)
    {
        var existing = Windows.FirstOrDefault(w => w.Panel == panel);
        if (existing is not null)
        {
            existing.Minimized = false;
            OnChanged?.Invoke();
            return;
        }

        var offset = Windows.Count * 30;
        Windows.Add(new WindowState
        {
            Id = _nextId++,
            Panel = panel,
            Title = panel,
            X = 100 + offset,
            Y = 80 + offset,
            Width = panel switch { "File Manager" => 800, "Cache Manager" => 600, "Relay" => 900, "Agents" => 950, _ => 700 },
            Height = 500
        });
        OnChanged?.Invoke();
    }

    public async Task CloseWindow(WindowState win)
    {
        Windows.Remove(win);
        if (Windows.Count == 0 && Relay.IsConnected)
        {
            await Relay.Disconnect();
            Output = "";
        }
        OnChanged?.Invoke();
    }

    public async Task CloseAllWindows()
    {
        Windows.Clear();
        if (Relay.IsConnected)
        {
            await Relay.Disconnect();
            Output = "";
        }
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

    public async Task DisconnectRelay()
    {
        await Relay.Disconnect();
        Windows.Clear();
        Output = "";
        OnChanged?.Invoke();
    }
}
