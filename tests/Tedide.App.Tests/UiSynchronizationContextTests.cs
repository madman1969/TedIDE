using System.Collections.Concurrent;

namespace Tedide.App.Tests;

public class UiSynchronizationContextTests
{
    /// <summary>A stand-in for Terminal.Gui's main loop: one thread running posted actions in order.</summary>
    private sealed class Loop : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public Loop()
        {
            _thread = new Thread(() =>
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                    action();
            }) { IsBackground = true };
            _thread.Start();
            Context = new UiSynchronizationContext(_thread.ManagedThreadId, _queue.Add);
            // Installed from the loop thread itself, as Program.cs does on the UI thread.
            var ready = new TaskCompletionSource();
            _queue.Add(() =>
            {
                SynchronizationContext.SetSynchronizationContext(Context);
                ready.SetResult();
            });
            ready.Task.Wait();
        }

        public UiSynchronizationContext Context { get; }
        public int ThreadId => _thread.ManagedThreadId;

        public Task<T> Run<T>(Func<Task<T>> work)
        {
            var result = new TaskCompletionSource<T>();
            _queue.Add(async () =>
            {
                try { result.SetResult(await work()); }
                catch (Exception ex) { result.SetException(ex); }
            });
            return result.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    [Fact]
    public async Task AnAwaitStartedOnTheUiThread_ResumesThere()
    {
        using var loop = new Loop();

        var resumedOn = await loop.Run(async () =>
        {
            await Task.Delay(20);    // completes on a thread-pool thread
            return Environment.CurrentManagedThreadId;
        });

        Assert.Equal(loop.ThreadId, resumedOn);
    }

    [Fact]
    public async Task ConfigureAwaitFalse_StaysOffTheUiThread()
    {
        // What the library projects do, so their internal awaits don't queue behind the UI.
        using var loop = new Loop();

        var resumedOn = await loop.Run(async () =>
        {
            await Task.Delay(20).ConfigureAwait(false);
            return Environment.CurrentManagedThreadId;
        });

        Assert.NotEqual(loop.ThreadId, resumedOn);
    }

    [Fact]
    public async Task Send_FromAnotherThread_RunsOnTheUiThreadAndWaits()
    {
        using var loop = new Loop();
        var ranOn = 0;

        await Task.Run(() => loop.Context.Send(_ => ranOn = Environment.CurrentManagedThreadId, null));

        Assert.Equal(loop.ThreadId, ranOn);
    }

    [Fact]
    public async Task Send_OnTheUiThread_RunsAtOnce()
    {
        // Waiting for the loop from inside the loop would never end.
        using var loop = new Loop();

        var ranInline = await loop.Run(() =>
        {
            var ran = false;
            loop.Context.Send(_ => ran = true, null);
            return Task.FromResult(ran);
        });

        Assert.True(ranInline);
    }
}
