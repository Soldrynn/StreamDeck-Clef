namespace AppleMusicBridge;

/// <summary>
/// Keeps callback owners alive briefly after native unregistration so an
/// already-dispatched COM callback cannot target a collected object. Both age
/// and count are bounded so recovery/rebinding cannot become a memory leak.
/// </summary>
internal sealed class GraceRetainer<T>(TimeSpan gracePeriod, int maximumCount, Action<T> release) : IDisposable
{
    private readonly Queue<Entry> _entries = new();

    internal int Count => _entries.Count;

    public void Retain(T value, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        _entries.Enqueue(new(timestamp, value));
        Trim(timestamp);
    }

    public void Trim(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        while (_entries.Count > 0 &&
               (_entries.Count > maximumCount || timestamp - _entries.Peek().RetiredAt >= gracePeriod))
            release(_entries.Dequeue().Value);
    }

    public void Dispose()
    {
        while (_entries.Count > 0) release(_entries.Dequeue().Value);
    }

    private sealed record Entry(DateTimeOffset RetiredAt, T Value);
}
