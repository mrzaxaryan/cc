namespace cc.Infrastructure;

/// <summary>
/// Manages CancellationTokenSource instances keyed by record ID.
/// Shared by DownloadStore and SearchStore to eliminate duplicate CTS logic.
/// </summary>
public class CtsManager
{
    private readonly Dictionary<int, CancellationTokenSource> _activeCts = new();

    public CancellationTokenSource Register(int recordId)
    {
        var cts = new CancellationTokenSource();
        _activeCts[recordId] = cts;
        return cts;
    }

    public void Cancel(int recordId)
    {
        if (_activeCts.TryGetValue(recordId, out var cts))
            cts.Cancel();
    }

    public void Remove(int recordId)
    {
        if (_activeCts.Remove(recordId, out var cts))
            cts.Dispose();
    }

    public bool IsCancelled(int recordId) =>
        _activeCts.TryGetValue(recordId, out var cts) && cts.IsCancellationRequested;

    public bool HasActive(int recordId) => _activeCts.ContainsKey(recordId);

    public void CancelAll()
    {
        foreach (var cts in _activeCts.Values)
            cts.Cancel();
    }
}
