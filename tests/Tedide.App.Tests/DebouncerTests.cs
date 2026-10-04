namespace Tedide.App.Tests;

public class DebouncerTests
{
    private readonly List<Func<bool>> _timers = [];
    private int _runs;

    private Debouncer Create() =>
        new(TimeSpan.FromMilliseconds(400), () => _runs++, (_, callback) => _timers.Add(callback));

    private void FireTimers()
    {
        foreach (var timer in _timers.ToList())
            timer();
        _timers.Clear();
    }

    [Fact]
    public void A_burst_of_requests_runs_once()
    {
        var debouncer = Create();
        debouncer.Request();
        debouncer.Request();
        debouncer.Request();
        FireTimers();
        Assert.Equal(1, _runs);
    }

    [Fact]
    public void Cancel_drops_a_pending_run()
    {
        var debouncer = Create();
        debouncer.Request();
        debouncer.Cancel();
        FireTimers();
        Assert.Equal(0, _runs);
    }

    [Fact]
    public void Work_remembering_an_older_generation_is_stale()
    {
        var debouncer = Create();
        debouncer.Request();
        var generation = debouncer.Generation;
        Assert.True(debouncer.IsCurrent(generation));
        debouncer.Request();
        Assert.False(debouncer.IsCurrent(generation));
    }
}
