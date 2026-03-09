using Microsoft.JSInterop;

namespace cc.Services;

public enum Theme { Dark, Light }

public class ThemeService
{
    private readonly IJSRuntime _js;
    private readonly LocalStorageService _storage;
    private const string StorageKey = "cc-theme";
    private Theme _current = Theme.Dark;

    public Theme Current => _current;

    public event Action? OnChanged;

    public ThemeService(IJSRuntime js, LocalStorageService storage)
    {
        _js = js;
        _storage = storage;
    }

    public async Task InitializeAsync()
    {
        var saved = await _storage.GetAsync(StorageKey);
        _current = saved == "light" ? Theme.Light : Theme.Dark;
        await ApplyTheme();
    }

    public async Task Toggle()
    {
        _current = _current == Theme.Dark ? Theme.Light : Theme.Dark;
        await ApplyTheme();
        await _storage.SetAsync(StorageKey, _current == Theme.Light ? "light" : "dark");
        OnChanged?.Invoke();
    }

    private async Task ApplyTheme()
    {
        var themeName = _current == Theme.Light ? "light" : "dark";
        await _js.InvokeVoidAsync("ccInterop.setTheme", themeName);
    }
}
