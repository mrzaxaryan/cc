using Microsoft.JSInterop;

namespace cc.Services;

public enum Theme { Dark, Light }

public class ThemeService
{
    private readonly IJSRuntime _js;
    private Theme _current = Theme.Dark;

    public Theme Current => _current;

    public event Action? OnChanged;

    public ThemeService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task InitializeAsync()
    {
        var saved = await _js.InvokeAsync<string?>("localStorage.getItem", "cc-theme");
        _current = saved == "light" ? Theme.Light : Theme.Dark;
        await ApplyTheme();
    }

    public async Task Toggle()
    {
        _current = _current == Theme.Dark ? Theme.Light : Theme.Dark;
        await ApplyTheme();
        await _js.InvokeVoidAsync("localStorage.setItem", "cc-theme", _current == Theme.Light ? "light" : "dark");
        OnChanged?.Invoke();
    }

    private async Task ApplyTheme()
    {
        var themeName = _current == Theme.Light ? "light" : "dark";
        await _js.InvokeVoidAsync("eval", $"document.documentElement.setAttribute('data-theme', '{themeName}'); document.documentElement.setAttribute('data-bs-theme', '{themeName}')");
    }
}
