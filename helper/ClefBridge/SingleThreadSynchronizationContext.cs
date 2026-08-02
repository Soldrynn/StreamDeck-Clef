using System.Collections.Concurrent;

namespace ClefBridge;

/// <summary>
/// Keeps WinRT media-session objects and Core Audio RCWs on the MTA thread that
/// created them. Several Apple Music for Windows builds reject cross-thread media
/// commands with RPC_E_WRONG_THREAD even though metadata reads still succeed.
/// </summary>
internal sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = new();

    public override void Post(SendOrPostCallback callback, object? state)
    {
        if (!_work.IsAddingCompleted) _work.Add((callback, state));
    }

    public T Run<T>(Task<T> task)
    {
        task.ContinueWith(
            _ => _work.CompleteAdding(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        foreach (var item in _work.GetConsumingEnumerable())
            item.Callback(item.State);

        return task.GetAwaiter().GetResult();
    }

    public void Dispose() => _work.Dispose();
}
