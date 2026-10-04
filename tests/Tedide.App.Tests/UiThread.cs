using System.Collections.Concurrent;

namespace Tedide.App.Tests;

/// <summary>
/// Runs an async test the way the app runs its UI code: on one thread of its own, with
/// <see cref="UiSynchronizationContext"/> installed, so the code after every await comes back to
/// it. The editor's documents need that - they refuse calls from any thread but the one that
/// made them.
/// </summary>
internal static class UiThread
{
    public static Task Run(Func<Task> test)
    {
        var done = new TaskCompletionSource();
        var queue = new BlockingCollection<Action>();
        void Post(Action action)
        {
            try
            {
                queue.Add(action);
            }
            catch (InvalidOperationException)
            {
                // The test has finished; whatever it left running no longer matters.
            }
        }

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(Environment.CurrentManagedThreadId, Post));
            Post(async () =>
            {
                try
                {
                    await test();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
                finally
                {
                    queue.CompleteAdding();
                }
            });
            foreach (var action in queue.GetConsumingEnumerable())
                action();
        }) { IsBackground = true, Name = "Test UI thread" };
        thread.Start();
        return done.Task;
    }
}
