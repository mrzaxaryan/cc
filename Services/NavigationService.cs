namespace cc.Services;

public enum AppView
{
    Agents,
    Files,
    Sync,
    Relay,
    Cache,
    Settings
}

public class TabInfo
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public AppView View { get; set; }
    public string? AgentId { get; set; }
    public string? AgentName { get; set; }
    public string? AgentUuid { get; set; }
    public string? RelayUrl { get; set; }
    public RelaySocket? Relay { get; set; }
}

public class NavigationService
{
    private int _nextTabId;

    public AppView ActiveView { get; private set; } = AppView.Agents;
    public List<TabInfo> Tabs { get; } = new();
    public int? ActiveTabId { get; private set; }

    public string? SelectedAgentId { get; private set; }
    public string? SelectedAgentUuid { get; private set; }

    public event Action? OnChanged;

    public void Navigate(AppView view)
    {
        ActiveView = view;
        OnChanged?.Invoke();
    }

    public TabInfo OpenTab(AppView view, string title, string? agentId = null, string? agentName = null,
        string? agentUuid = null, string? relayUrl = null, RelaySocket? relay = null)
    {
        // Reuse existing tab for same view + agent
        var existing = Tabs.FirstOrDefault(t => t.View == view && t.AgentId == agentId);
        if (existing is not null)
        {
            ActiveTabId = existing.Id;
            ActiveView = view;
            OnChanged?.Invoke();
            return existing;
        }

        var tab = new TabInfo
        {
            Id = _nextTabId++,
            Title = title,
            View = view,
            AgentId = agentId,
            AgentName = agentName,
            AgentUuid = agentUuid,
            RelayUrl = relayUrl,
            Relay = relay
        };
        Tabs.Add(tab);
        ActiveTabId = tab.Id;
        ActiveView = view;
        OnChanged?.Invoke();
        return tab;
    }

    public void ActivateTab(int tabId)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;
        ActiveTabId = tabId;
        ActiveView = tab.View;
        OnChanged?.Invoke();
    }

    public void CloseTab(int tabId)
    {
        var idx = Tabs.FindIndex(t => t.Id == tabId);
        if (idx < 0) return;
        Tabs.RemoveAt(idx);
        if (ActiveTabId == tabId)
        {
            if (Tabs.Count > 0)
            {
                var newIdx = Math.Min(idx, Tabs.Count - 1);
                ActiveTabId = Tabs[newIdx].Id;
                ActiveView = Tabs[newIdx].View;
            }
            else
            {
                ActiveTabId = null;
            }
        }
        OnChanged?.Invoke();
    }

    public TabInfo? GetActiveTab() => Tabs.FirstOrDefault(t => t.Id == ActiveTabId);

    public void SelectAgent(string? agentId, string? agentUuid = null)
    {
        SelectedAgentId = agentId;
        SelectedAgentUuid = agentUuid;
        OnChanged?.Invoke();
    }

    public void NotifyChanged() => OnChanged?.Invoke();
}
