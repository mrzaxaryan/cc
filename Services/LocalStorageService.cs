using Microsoft.JSInterop;

namespace cc.Services;

public class LocalStorageService
{
    private readonly IJSRuntime _js;

    public LocalStorageService(IJSRuntime js) => _js = js;

    public async Task<string?> GetAsync(string key)
        => await _js.InvokeAsync<string?>("localStorage.getItem", key);

    public async Task SetAsync(string key, string value)
        => await _js.InvokeVoidAsync("localStorage.setItem", key, value);

    public async Task RemoveAsync(string key)
        => await _js.InvokeVoidAsync("localStorage.removeItem", key);
}
