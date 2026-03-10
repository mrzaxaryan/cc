using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.JSInterop;

namespace cc.Services;

public class ExtensionGroup
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("extensions")] public string Extensions { get; set; } = ""; // comma-separated e.g. ".txt,.pdf,.doc"
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public class ExtensionGroupStore
{
    private readonly IJSRuntime _js;
    private List<ExtensionGroup> _cache = new();
    private bool _loaded;

    public event Action? OnChanged;

    public ExtensionGroupStore(IJSRuntime js) => _js = js;

    public IReadOnlyList<ExtensionGroup> Groups => _cache;

    public bool SetupRequired => _loaded && _cache.Count == 0;

    public static readonly Dictionary<string, string[]> DefaultGroups = new()
    {
        ["Documents"] = [".txt", ".pdf", ".doc", ".docx", ".rtf", ".odt"],
        ["Spreadsheets"] = [".xls", ".xlsx", ".csv", ".ods"],
        ["Images"] = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".svg", ".webp", ".ico", ".tiff"],
        ["Code"] = [".cs", ".js", ".ts", ".py", ".java", ".cpp", ".c", ".h", ".go", ".rs", ".rb", ".php"],
        ["Web"] = [".html", ".htm", ".css", ".json", ".xml", ".yaml", ".yml"],
        ["Archives"] = [".zip", ".rar", ".7z", ".tar", ".gz", ".bz2"],
        ["Database"] = [".sql", ".db", ".sqlite", ".mdb", ".accdb", ".bak"],
        ["Config"] = [".ini", ".cfg", ".conf", ".env", ".toml", ".properties"],
    };

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var records = await _js.InvokeAsync<ExtensionGroup[]>("ccExtGroupDb.getAll");
            _cache = records.ToList();
        }
        catch
        {
            _cache = new();
        }
    }

    public async Task AddAsync(string name, string extensions, bool enabled = true)
    {
        var group = new ExtensionGroup { Name = name, Extensions = extensions, Enabled = enabled };
        await _js.InvokeVoidAsync("ccExtGroupDb.put", group);
        var existing = _cache.FindIndex(g => g.Name == name);
        if (existing >= 0)
            _cache[existing] = group;
        else
            _cache.Add(group);
        OnChanged?.Invoke();
    }

    public async Task RemoveAsync(string name)
    {
        _cache.RemoveAll(g => g.Name == name);
        await _js.InvokeVoidAsync("ccExtGroupDb.remove", name);
        OnChanged?.Invoke();
    }

    public async Task UpdateAsync(ExtensionGroup group)
    {
        await _js.InvokeVoidAsync("ccExtGroupDb.put", group);
        var idx = _cache.FindIndex(g => g.Name == group.Name);
        if (idx >= 0) _cache[idx] = group;
        else _cache.Add(group);
        OnChanged?.Invoke();
    }

    public async Task SeedDefaultsAsync()
    {
        foreach (var (name, exts) in DefaultGroups)
        {
            await AddAsync(name, string.Join(",", exts));
        }
    }

    public List<ExtensionGroup> GetEnabled() => _cache.Where(g => g.Enabled).ToList();

    // --- Shared extension validation utilities ---

    private static readonly Regex ValidExtRegex = new(@"^\.[a-zA-Z0-9]{1,15}$");

    public static string NormalizeExtensions(string raw) =>
        string.Join(",", raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : $".{e.ToLowerInvariant()}"));

    public static List<string> GetInvalidExtensions(string normalized) =>
        normalized.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => !ValidExtRegex.IsMatch(e))
            .ToList();
}
