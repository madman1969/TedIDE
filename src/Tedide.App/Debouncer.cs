namespace Tedide.App;

/// <summary>
/// Runs an action once requests stop coming for a while: a burst of edits, caret moves or tab
/// switches runs it once, after the last of them. Each <see cref="Request"/> or <see cref="Cancel"/>
/// starts a new generation; work that remembered an older one (see <see cref="Generation"/>) can
/// tell its result is stale with <see cref="IsCurrent"/>.
/// </summary>
/// <param name="addTimeout">Runs a callback after a delay on the UI thread - Terminal.Gui's
/// main loop, or a test's.</param>
internal sealed class Debouncer(TimeSpan delay, Action run, Action<TimeSpan, Func<bool>> addTimeout)
{
    private int _generation;

    /// <summary>The latest request's number - for work to remember, and check later with
    /// <see cref="IsCurrent"/>.</summary>
    public int Generation => _generation;

    /// <summary>Runs the action after the delay, unless another request or a cancel comes first.</summary>
    public void Request()
    {
        var generation = ++_generation;
        addTimeout(delay, () =>
        {
            if (generation == _generation)
                run();
            return false;
        });
    }

    /// <summary>Drops a pending run, and marks work already running as stale.</summary>
    public void Cancel() => ++_generation;

    /// <summary>Whether nothing has been requested or cancelled since <paramref name="generation"/>.</summary>
    public bool IsCurrent(int generation) => generation == _generation;
}
