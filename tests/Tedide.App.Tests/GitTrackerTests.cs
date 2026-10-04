using Tedide.Core;

namespace Tedide.App.Tests;

/// <summary>
/// <see cref="GitTracker"/> against a real git in a temporary folder, with its timers and its
/// UI thread replaced by queues the test runs by hand - so "a refresh requested while one is
/// running" happens the same way every time.
/// </summary>
public sealed class GitTrackerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-tracker-").FullName;
    private readonly List<(TimeSpan Delay, Func<bool> Callback)> _timers = [];
    private readonly Queue<Action> _uiThread = new();
    private IReadOnlyList<string> _projects = [];
    private string? _primary;
    private int _sourceReads;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private GitTracker MakeTracker() =>
        new(
            () =>
            {
                _sourceReads++;
                return (_projects, _primary);
            },
            action =>
            {
                lock (_uiThread)
                    _uiThread.Enqueue(action);
            },
            (delay, callback) =>
            {
                lock (_timers)
                    _timers.Add((delay, callback));
            });

    /// <summary>Runs every timer due, keeping the ones that ask to repeat.</summary>
    private void FireTimers()
    {
        List<(TimeSpan, Func<bool>)> due;
        lock (_timers)
        {
            due = [.. _timers];
            _timers.Clear();
        }
        foreach (var timer in due)
        {
            if (timer.Item2())
                lock (_timers)
                    _timers.Add(timer);
        }
    }

    private void PumpUiThread()
    {
        while (true)
        {
            Action action;
            lock (_uiThread)
            {
                if (!_uiThread.TryDequeue(out action!))
                    return;
            }
            action();
        }
    }

    /// <summary>Requests a refresh and runs it to the end, the way the app's UI loop would.</summary>
    private async Task RefreshAsync(GitTracker tracker)
    {
        tracker.RequestRefresh();
        FireTimers();
        await tracker.LastRefresh;
        PumpUiThread();
    }

    private static async Task Git(string directory, params string[] args)
    {
        var result = await ToolProcess.RunAsync(ToolProcess.StartInfo("git", args, directory));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)}: {result.Error}");
    }

    private static async Task InitAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await Git(directory, "init", "-q", "-b", "main");
    }

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task Requests_CloseTogether_ShareOneRefresh()
    {
        var tracker = MakeTracker();

        tracker.RequestRefresh();
        tracker.RequestRefresh();
        tracker.RequestRefresh();

        var timer = Assert.Single(_timers);
        Assert.Equal(TimeSpan.FromMilliseconds(300), timer.Delay);
        FireTimers();
        Assert.Equal(1, _sourceReads);
        await tracker.LastRefresh;
    }

    [Fact]
    public async Task ARefresh_ReportsTheChangedFiles_AndTheSolutionsRepository()
    {
        await InitAsync(_dir);
        var project = Folder("hello");
        var main = Path.Combine(project, "main.c");
        File.WriteAllText(main, "int main(void) { return 0; }\n");
        (_projects, _primary) = ([project], project);
        var tracker = MakeTracker();
        var changed = 0;
        tracker.Changed += () => changed++;

        await RefreshAsync(tracker);

        Assert.Equal(1, changed);
        Assert.Equal(Path.GetFullPath(_dir), tracker.Primary!.Root, ignoreCase: true);
        Assert.NotNull(tracker.PrimaryStatus);
        Assert.True(tracker.Files[main].IsUntracked);
    }

    [Fact]
    public async Task OutsideARepository_EverythingStaysEmpty()
    {
        var project = Folder("hello");
        (_projects, _primary) = ([project], project);
        var tracker = MakeTracker();
        var changed = 0;
        tracker.Changed += () => changed++;

        await RefreshAsync(tracker);

        Assert.Equal(1, changed);
        Assert.Null(tracker.Primary);
        Assert.Null(tracker.PrimaryStatus);
        Assert.Empty(tracker.Files);
        Assert.Null(tracker.RepositoryFor(Path.Combine(project, "main.c")));
    }

    [Fact]
    public async Task ARequest_WhileARefreshIsRunning_RefreshesAgainAfterIt()
    {
        var project = Folder("hello");
        (_projects, _primary) = ([project], project);
        var tracker = MakeTracker();
        tracker.RequestRefresh();
        FireTimers();
        await tracker.LastRefresh;  // finished, but hasn't told the UI thread yet - still "running"

        tracker.RequestRefresh();
        FireTimers();
        Assert.Equal(1, _sourceReads);

        PumpUiThread();
        Assert.Single(_timers);
        FireTimers();
        Assert.Equal(2, _sourceReads);
    }

    [Fact]
    public async Task RepositoryFor_PicksTheInnermostRepository()
    {
        await InitAsync(_dir);
        var inner = Folder("libs", "sprites");
        await InitAsync(inner);
        var outerProject = Folder("game");
        var innerProject = Folder("libs", "sprites", "src");
        (_projects, _primary) = ([outerProject, innerProject], outerProject);
        var tracker = MakeTracker();

        await RefreshAsync(tracker);

        Assert.Equal(Path.GetFullPath(inner), tracker.RepositoryFor(Path.Combine(innerProject, "sprite.c"))!.Root, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(_dir), tracker.RepositoryFor(Path.Combine(outerProject, "main.c"))!.Root, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(_dir), tracker.Primary!.Root, ignoreCase: true);
    }

    [Fact]
    public async Task ProjectsChanged_LooksForRepositoriesAgain()
    {
        var project = Folder("hello");
        (_projects, _primary) = ([project], project);
        var tracker = MakeTracker();
        await RefreshAsync(tracker);
        await InitAsync(_dir);

        // A plain refresh trusts what it found before...
        await RefreshAsync(tracker);
        Assert.Null(tracker.Primary);

        // ...but after the projects change, it looks again.
        tracker.ProjectsChanged();
        FireTimers();
        await tracker.LastRefresh;
        PumpUiThread();
        Assert.NotNull(tracker.Primary);
    }

    [Fact]
    public async Task Start_RefreshesEveryFiveSeconds()
    {
        var tracker = MakeTracker();

        tracker.Start();

        var periodic = Assert.Single(_timers);
        Assert.Equal(TimeSpan.FromSeconds(5), periodic.Delay);
        FireTimers();  // the periodic timer asks for a refresh, and keeps itself
        Assert.Equal(2, _timers.Count);
        FireTimers();
        Assert.Equal(1, _sourceReads);
        await tracker.LastRefresh;
    }
}
