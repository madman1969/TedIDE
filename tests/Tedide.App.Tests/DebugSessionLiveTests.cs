using Tedide.App.Views;
using Tedide.Core;
using Tedide.Debug;

namespace Tedide.App.Tests;

/// <summary>
/// Whole debug sessions through <see cref="DebugSession"/>: a real cc65 build of a small program
/// (Fixtures/DebugGame - its debug info, generated assembly and binary) against
/// <see cref="FakeVice"/>, on a test UI thread so VICE's events come back the way they do in the
/// app. The breakpoints are on main.c's lines 12 (<c>total = add(i, 2);</c>) and 14.
/// </summary>
public sealed class DebugSessionLiveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-session-").FullName;

    public DebugSessionLiveTests()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DebugGame");
        foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_dir, Path.GetRelativePath(fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Main => Path.Combine(_dir, "main.c");

    /// <summary>Everything a session needs, made on the test UI thread.</summary>
    private sealed class Session : IDisposable
    {
        public Session(string directory, int? viceExitCode = null, params int[] breakpointLines)
        {
            Project = TedideProject.Load(Path.Combine(directory, "Game.tproj"));
            Workspace.Projects.Add(Project);
            new BreakpointsFile { Breakpoints = [.. breakpointLines.Select(line => new BreakpointEntry("main.c", line))] }
                .Save(Project.ResolvedBreakpointsFile);
            Host = new FakeDebugHost(EditorPane);
            Vice = new FakeVice(Path.Combine(directory, "Game.prg"));
            Debug = new DebugSession(Host, Workspace, EditorPane, DebugPanel, new DisassemblyView(), MemoryView, new BreakpointLineTransformer(),
                (_, output) =>
                {
                    output("VICE is starting");
                    return new ViceInstance(Vice.Port, () => viceExitCode);
                });
            Debug.LoadBreakpointsForActiveProject();
        }

        public TedideProject Project { get; }
        public Workspace Workspace { get; } = new();
        public EditorPane EditorPane { get; } = new();
        public DebugPanelView DebugPanel { get; } = new();
        public MemoryView MemoryView { get; } = new();
        public FakeDebugHost Host { get; }
        public FakeVice Vice { get; }
        public DebugSession Debug { get; }

        public Task StoppedAtAsync(int line) =>
            Host.WaitUntilAsync(() => Host.DebugStatus?.Contains($"main.c:{line}", StringComparison.Ordinal) == true, $"a stop at line {line}");

        public void Dispose() => Vice.Dispose();
    }

    [Fact]
    public Task ASession_StopsAtBreakpoints_Steps_AndStops() => UiThread.Run(async () =>
    {
        using var session = new Session(_dir, null, 12, 14);
        await session.Vice.StartAsync();

        await session.Debug.StartDebuggingAsync();
        await session.StoppedAtAsync(12);

        Assert.Contains("(checkpoint #1)", session.Host.DebugStatus);
        Assert.Equal((Main, 12), session.Host.DebugLine);
        Assert.Equal(2, session.Vice.Checkpoints.Count);
        Assert.True(session.EditorPane.ReadOnly);  // no editing the source of what's running
        Assert.True(session.EditorPane.IsShown(Main));
        Assert.Contains("VICE is starting", session.Host.Output);

        await session.Debug.StepDebuggingAsync(stepInto: false);
        await session.StoppedAtAsync(13);

        await session.Debug.StartOrContinueDebuggingAsync();  // F5 while stopped continues
        await session.StoppedAtAsync(14);
        Assert.Contains("(checkpoint #2)", session.Host.DebugStatus);

        await session.Debug.StopDebuggingAsync();
        Assert.Null(session.Host.DebugStatus);
        Assert.Null(session.Host.DebugLine);
        Assert.False(session.EditorPane.ReadOnly);
        Assert.Empty(session.Vice.Checkpoints);  // or the detached program would still stop at them
        Assert.Equal(ViceMonitorCommand.ExitMonitor, session.Vice.Requests[^1]);
    });

    [Fact]
    public Task WatchesAndMemory_AreReadWhileStopped() => UiThread.Run(async () =>
    {
        using var session = new Session(_dir, null, 12);
        await session.Vice.StartAsync();
        await session.Debug.StartDebuggingAsync();
        await session.StoppedAtAsync(12);

        // "total" is the C global - _total in the debug info.
        session.Host.Dialogs.Answer = dialog => Assert.IsType<AddWatchDialog>(dialog).Expression = "total";
        session.Debug.ShowAddWatchDialog();
        var address = int.Parse(session.DebugPanel.WatchRows[0].Split("($")[1][..4], System.Globalization.NumberStyles.HexNumber);
        session.Vice.Memory[address] = 0x2A;
        await session.Host.WaitUntilAsync(() => session.DebugPanel.WatchRows[0].EndsWith("= $2A", StringComparison.Ordinal), "the watch's value");

        session.Debug.ShowMemoryAt("$0801");
        await session.Host.WaitUntilAsync(() => session.MemoryView.Status == "As of this stop.", "the memory read");
        Assert.Equal((ushort)0x0801, session.Debug.MemoryAddress);

        await session.Debug.StopDebuggingAsync();
        Assert.DoesNotContain(session.DebugPanel.WatchRows, row => row.Contains("total"));  // watches are per session
    });

    [Fact]
    public Task ABreakpointAddedMidSession_ReachesVice() => UiThread.Run(async () =>
    {
        using var session = new Session(_dir, null, 12);
        await session.Vice.StartAsync();
        await session.Debug.StartDebuggingAsync();
        await session.StoppedAtAsync(12);
        Assert.Single(session.Vice.Checkpoints);

        session.EditorPane.ReadOnly = false;  // the caret moves regardless; F9 doesn't edit
        session.EditorPane.Editor.CaretOffset = session.EditorPane.Editor.Document!.GetLineByNumber(14).Offset;
        session.Debug.ToggleBreakpointAtCursor();
        await Task.WhenAll(session.Host.Fired);

        Assert.Equal(2, session.Vice.Checkpoints.Count);
        await session.Debug.StopDebuggingAsync();
    });

    [Fact]
    public Task VICEClosing_EndsTheSession() => UiThread.Run(async () =>
    {
        using var session = new Session(_dir, null, 12);
        await session.Vice.StartAsync();
        await session.Debug.StartDebuggingAsync();
        await session.StoppedAtAsync(12);

        session.Vice.Dispose();

        await session.Host.WaitUntilAsync(() => session.Host.DebugStatus is null, "the session to end");
        Assert.Contains("VICE closed the debugging connection - debug session ended.", session.Host.Output);
        Assert.False(session.EditorPane.ReadOnly);
    });

    [Fact]
    public Task VICEExitingAtStartup_IsReported() => UiThread.Run(async () =>
    {
        using var session = new Session(_dir, viceExitCode: 1);

        await session.Debug.StartDebuggingAsync();

        Assert.Equal("VICE exited during startup (exit code 1) - see its output above.", session.Host.Output[^1]);
        Assert.Empty(session.Vice.Requests);
        Assert.Null(session.Host.DebugStatus);
    });
}
