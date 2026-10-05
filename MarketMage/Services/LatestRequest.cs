using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMage.Services;

// Owned by the UI thread. Background work produces values; only TryTake publishes them.
public sealed class LatestRequest<T> : IDisposable
{
    private CancellationTokenSource? cancellation;
    private Task<T>? pending;
    public bool IsRunning => pending != null;
    public void Start(Func<CancellationToken, Task<T>> operation)
    {
        Cancel();
        cancellation = new CancellationTokenSource();
        pending = operation(cancellation.Token);
    }
    public bool TryTake([MaybeNullWhen(false)] out T result)
    {
        result = default;
        if (pending == null || !pending.IsCompleted) return false;
        var task = pending;
        pending = null;
        cancellation?.Dispose(); cancellation = null;
        if (!task.IsCompletedSuccessfully) { _ = task.Exception; return false; }
        result = task.Result;
        return true;
    }
    public void Cancel()
    {
        cancellation?.Cancel();
        cancellation?.Dispose(); cancellation = null;
        if (pending != null)
            _ = pending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        pending = null;
    }
    public void Dispose() => Cancel();
}
