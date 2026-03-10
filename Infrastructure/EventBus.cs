using System.Collections.Concurrent;

namespace cc.Infrastructure;

public interface IEventBus
{
    /// <summary>Publish an event to all subscribers.</summary>
    void Publish<T>(T evt) where T : IEvent;

    /// <summary>Subscribe to events of type T. Returns a disposable that unsubscribes when disposed.</summary>
    IDisposable Subscribe<T>(Action<T> handler) where T : IEvent;

    /// <summary>Subscribe to events of type T with an async handler.</summary>
    IDisposable Subscribe<T>(Func<T, Task> handler) where T : IEvent;
}

public class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<object>> _handlers = new();
    private readonly object _lock = new();

    public void Publish<T>(T evt) where T : IEvent
    {
        var type = typeof(T);
        if (!_handlers.TryGetValue(type, out var handlers)) return;

        List<object> snapshot;
        lock (_lock)
        {
            snapshot = handlers.ToList();
        }

        foreach (var handler in snapshot)
        {
            try
            {
                if (handler is Action<T> sync)
                    sync(evt);
                else if (handler is Func<T, Task> async_)
                    _ = async_(evt);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EventBus] Handler error for {type.Name}: {ex.Message}");
            }
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : IEvent
    {
        return AddHandler<T>(handler);
    }

    public IDisposable Subscribe<T>(Func<T, Task> handler) where T : IEvent
    {
        return AddHandler<T>(handler);
    }

    private IDisposable AddHandler<T>(object handler) where T : IEvent
    {
        var type = typeof(T);
        var handlers = _handlers.GetOrAdd(type, _ => new List<object>());

        lock (_lock)
        {
            handlers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_lock)
            {
                handlers.Remove(handler);
            }
        });
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
        }
    }
}
