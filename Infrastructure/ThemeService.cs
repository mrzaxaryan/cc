using Microsoft.JSInterop;

namespace cc.Infrastructure;

public enum Theme { Dark, Light }

public class ThemeService
{
    private readonly IJSRuntime _js;
    private readonly LocalStorageService _storage;
    private readonly IEventBus _bus;
    private const string StorageKey = "cc-theme";
    private Theme _current = Theme.Dark;

    public Theme Current => _current;

    public ThemeService(IJSRuntime js, LocalStorageService storage, IEventBus bus)
    {
        _js = js;
        _storage = storage;
        _bus = bus;
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
        _bus.Publish(new ThemeChangedEvent());
    }

    private async Task ApplyTheme()
    {
        var themeName = _current == Theme.Light ? "light" : "dark";
        await _js.InvokeVoidAsync("ccInterop.setTheme", themeName);
    }
}
