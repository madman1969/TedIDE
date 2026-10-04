using Terminal.Gui.App;

namespace Tedide.App;

/// <summary>
/// Brings <c>await</c> back to the UI thread. Terminal.Gui installs no SynchronizationContext, so
/// without this the code after any await runs on whichever thread-pool thread finished the task -
/// where touching a view races its drawing (crashes seen: "Collection was modified" in OutputView,
/// "Call from invalid thread" in TextDocument). Installed on the UI thread at startup, it makes
/// every await started there resume there, as WinForms and WPF do.
/// <para>
/// The library projects (Build, Debug, Git) use <c>ConfigureAwait(false)</c> throughout, so their
/// own internal awaits - one VICE round trip per instruction while stepping - don't queue behind
/// the UI's main loop; only the app's own code comes back here.
/// </para>
/// </summary>
internal sealed class UiSynchronizationContext : SynchronizationContext
{
    private readonly int _uiThreadId;
    private readonly Action<Action> _post;

    /// <param name="post">Runs an action on the UI thread later - <see cref="Application.Invoke(Action)"/>.</param>
    public UiSynchronizationContext(int uiThreadId, Action<Action> post)
    {
        _uiThreadId = uiThreadId;
        _post = post;
    }

    /// <summary>Installs one on the current (UI) thread, posting through Terminal.Gui's main loop.</summary>
    public static void Install() =>
        SetSynchronizationContext(new UiSynchronizationContext(Environment.CurrentManagedThreadId, action => Application.Invoke(action)));

    public override void Post(SendOrPostCallback callback, object? state) => _post(() => callback(state));

    /// <summary>Runs <paramref name="callback"/> on the UI thread and waits for it. On the UI thread
    /// already, it runs straight away - waiting for the main loop from inside it would never end.</summary>
    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            callback(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        _post(() =>
        {
            try
            {
                callback(state);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (failure is not null)
            throw new InvalidOperationException("A callback sent to the UI thread failed.", failure);
    }

    public override SynchronizationContext CreateCopy() => this;
}
