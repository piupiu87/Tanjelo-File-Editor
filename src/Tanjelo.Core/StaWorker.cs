using System.Collections.Concurrent;

namespace Tanjelo.Core;

/// <summary>A dedicated STA thread for Office automation, which must stay on one apartment for its lifetime.</summary>
public sealed class StaWorker : IDisposable
{
    readonly BlockingCollection<Action> _queue = [];
    readonly Thread _thread;

    public StaWorker(string name)
    {
        _thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable())
                work();
        })
        {
            IsBackground = true,
            Name = name,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public Task InvokeAsync(Action work) => InvokeAsync(() => { work(); return true; });

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(30));
        _queue.Dispose();
    }
}
