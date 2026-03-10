using Microsoft.AspNetCore.Components;

namespace cc.Infrastructure;

/// <summary>
/// Base component that simplifies event subscriptions.
/// Call <see cref="On{T}(Action{T})"/> or <see cref="On{T}(Action)"/> in OnInitialized
/// and subscriptions are auto-disposed + UI is auto-refreshed.
/// </summary>
public abstract class EventSubscriber : ComponentBase, IDisposable
{
    [Inject] protected IEventBus Bus { get; set; } = default!;

    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposed;

    /// <summary>Subscribe to event T. Runs handler then refreshes UI on the render thread.</summary>
    protected void On<T>(Action<T> handler) where T : IEvent
    {
        _subscriptions.Add(Bus.Subscribe<T>(evt =>
        {
            handler(evt);
            _ = InvokeAsync(StateHasChanged);
        }));
    }

    /// <summary>Subscribe to event T. Just refreshes UI (no custom handler).</summary>
    protected void On<T>() where T : IEvent
    {
        _subscriptions.Add(Bus.Subscribe<T>(evt =>
        {
            _ = InvokeAsync(StateHasChanged);
        }));
    }

    /// <summary>Subscribe to event T with an async handler, then refreshes UI.</summary>
    protected void OnAsync<T>(Func<T, Task> handler) where T : IEvent
    {
        _subscriptions.Add(Bus.Subscribe<T>(async evt =>
        {
            await InvokeAsync(async () =>
            {
                await handler(evt);
                StateHasChanged();
            });
        }));
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
    }
}
