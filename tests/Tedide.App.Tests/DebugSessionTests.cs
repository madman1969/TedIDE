using Tedide.App.Views;
using Tedide.Build;
using Tedide.Core;

namespace Tedide.App.Tests;

/// <summary>
/// The parts of <see cref="DebugSession"/> that don't need VICE running: breakpoints (F9,
/// Ctrl+F9, conditions, the Breakpoints dialog), watches, the Memory tab's address, and the
/// checks Start Debugging makes before it launches anything.
/// </summary>
public sealed class DebugSessionTests : IDisposable
{
    private const string MainText = "int main(void)\n{\n    return 0;\n}\n";

    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-debug-").FullName;
    private readonly Workspace _workspace = new();
    private readonly EditorPane _editorPane = new();
    private readonly DebugPanelView _debugPanel = new();
    private readonly MemoryView _memoryView = new();
    private readonly BreakpointLineTransformer _breakpointLines = new();
    private readonly FakeDebugHost _host = new();
    private readonly DebugSession _debug;
    private readonly TedideProject _project;

    public DebugSessionTests()
    {
        _debug = new DebugSession(_host, _workspace, _editorPane, _debugPanel, new DisassemblyView(), _memoryView, _breakpointLines);
        File.WriteAllText(Main, MainText);
        _project = new TedideProject { Name = "Game", Target = Cc65Target.C64, SourceFiles = ["main.c"], GenerateDebugInfo = true };
        _project.Save(Path.Combine(_dir, "Game.tproj"));
        _workspace.Projects.Add(_project);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Main => Path.Combine(_dir, "main.c");

    private void OpenMainAtLine(int line)
    {
        _editorPane.Open(Main);
        _editorPane.Editor.CaretOffset = _editorPane.Editor.Document!.GetLineByNumber(line).Offset;
    }

    private List<BreakpointEntry> SavedBreakpoints() => BreakpointsFile.Load(_project.ResolvedBreakpointsFile).Breakpoints;

    [Fact]
    public void F9_SetsABreakpoint_AndAgainRemovesIt()
    {
        OpenMainAtLine(3);

        _debug.ToggleBreakpointAtCursor();
        Assert.Equal("Breakpoint set: main.c:3", _host.Output[^1]);
        Assert.Equal([new BreakpointEntry("main.c", 3)], SavedBreakpoints());
        Assert.Equal([3], _breakpointLines.BreakpointLines);

        _debug.ToggleBreakpointAtCursor();
        Assert.Equal("Breakpoint removed: main.c:3", _host.Output[^1]);
        Assert.Empty(SavedBreakpoints());
        Assert.Empty(_breakpointLines.BreakpointLines);
    }

    [Fact]
    public void CtrlF9_DisablesABreakpoint_WithoutLosingIt()
    {
        OpenMainAtLine(3);
        _debug.EnableBreakpointAtCursor();  // no breakpoint there yet - nothing happens
        Assert.Empty(_host.Output);
        _debug.ToggleBreakpointAtCursor();

        _debug.EnableBreakpointAtCursor();
        Assert.Equal("Breakpoint disabled: main.c:3", _host.Output[^1]);
        Assert.False(Assert.Single(SavedBreakpoints()).Enabled);
        Assert.Empty(_breakpointLines.BreakpointLines);

        _debug.EnableBreakpointAtCursor();
        Assert.Equal("Breakpoint enabled: main.c:3", _host.Output[^1]);
        Assert.Equal([3], _breakpointLines.BreakpointLines);
    }

    [Fact]
    public void BreakpointCondition_CreatesTheBreakpoint_AndCanBeCleared()
    {
        OpenMainAtLine(3);
        _host.Dialogs.Answer = dialog => Assert.IsType<BreakpointConditionDialog>(dialog).Condition = "A == $01";

        _debug.EditBreakpointConditionAtCursor();
        Assert.Equal("Breakpoint set: main.c:3 if A == $01", _host.Output[^1]);
        Assert.Equal("A == $01", Assert.Single(SavedBreakpoints()).Condition);

        _host.Dialogs.Answer = dialog => Assert.IsType<BreakpointConditionDialog>(dialog).Condition = "";
        _debug.EditBreakpointConditionAtCursor();
        Assert.Null(Assert.Single(SavedBreakpoints()).Condition);

        _host.Dialogs.Answer = null;  // cancelled
        _debug.EditBreakpointConditionAtCursor();
        Assert.Equal("Breakpoint set: main.c:3", _host.Output[^1]);
    }

    [Fact]
    public void Breakpoints_AreLoadedForTheProject_AndABrokenFileIsReported()
    {
        new BreakpointsFile { Breakpoints = [new BreakpointEntry("main.c", 2)] }.Save(_project.ResolvedBreakpointsFile);
        _editorPane.Open(Main);

        _debug.LoadBreakpointsForActiveProject();
        Assert.Equal([2], _breakpointLines.BreakpointLines);

        File.WriteAllText(_project.ResolvedBreakpointsFile, "{ broken");
        _debug.LoadBreakpointsForActiveProject();
        Assert.Single(_host.Output);
        Assert.Empty(_debug.Breakpoints.Breakpoints);
    }

    [Fact]
    public void TheBreakpointsDialog_NeedsAProject()
    {
        _debug.ShowBreakpointsDialog();
        Assert.IsType<BreakpointsDialog>(Assert.Single(_host.Dialogs.Shown));

        _workspace.Projects.Clear();
        _debug.ShowBreakpointsDialog();
        Assert.StartsWith("No project loaded.", _host.Output[^1]);
    }

    [Fact]
    public void AddWatch_TakesAnAddress_AndRejectsWhatIsntOne()
    {
        _host.Dialogs.Answer = dialog => Assert.IsType<AddWatchDialog>(dialog).Expression = "$D012";
        _debug.ShowAddWatchDialog();
        Assert.Equal(["$D012 ($D012) = ?"], _debugPanel.WatchRows);

        _host.Dialogs.Answer = dialog => Assert.IsType<AddWatchDialog>(dialog).Expression = "no_such_symbol";
        _debug.ShowAddWatchDialog();
        Assert.Single(_host.Output);
        Assert.Single(_debugPanel.WatchRows);

        _debug.ClearWatches();
        Assert.DoesNotContain("$D012 ($D012) = ?", _debugPanel.WatchRows);
    }

    [Fact]
    public void ShowMemoryAt_WaitsForTheNextStop_OutsideASession()
    {
        _debug.ShowMemoryAt("$0400");
        Assert.Equal((ushort)0x0400, _debug.MemoryAddress);
        Assert.Equal("$0400 will be shown when execution next stops.", _memoryView.Status);

        _debug.ShowMemoryAt("1024");
        Assert.Equal((ushort)0x0400, _debug.MemoryAddress);

        _debug.ShowMemoryAt("nowhere");
        Assert.NotEqual("$0400 will be shown when execution next stops.", _memoryView.Status);
    }

    [Fact]
    public async Task StartDebugging_NeedsDebugInfo_AndABuildThatMadeIt()
    {
        _project.GenerateDebugInfo = false;
        await _debug.StartDebuggingAsync();
        Assert.StartsWith("Debug Info Required:", Assert.Single(_host.Dialogs.Errors));
        Assert.Equal(0, _host.Builds);

        _project.GenerateDebugInfo = true;
        _host.BuildSucceeds = false;
        await _debug.StartOrContinueDebuggingAsync();
        Assert.Equal(1, _host.Builds);
        Assert.Equal(1, _host.DebugTabShown);
        Assert.Empty(_host.Output);

        _host.BuildSucceeds = true;
        await _debug.StartDebuggingAsync();
        Assert.Equal("Build succeeded but no debug info file was produced.", _host.Output[^1]);
    }

    [Fact]
    public async Task StartDebugging_NeedsAProjectThatRuns()
    {
        _host.StartupProjectRuns = false;  // a library - the shell has said so
        await _debug.StartDebuggingAsync();
        Assert.Equal(0, _host.DebugTabShown);

        _workspace.Projects.Clear();
        await _debug.StartDebuggingAsync();
        Assert.StartsWith("No project loaded.", _host.Output[^1]);
        Assert.Equal(0, _host.Builds);

        await _debug.StopDebuggingAsync();  // no session - nothing to stop
        await _debug.StepDebuggingAsync(stepInto: true);
        await _debug.ContinueDebuggingAsync();
    }
}

/// <summary>The shell as <see cref="DebugSession"/> sees it, recording what it's asked to do and
/// opening files in <paramref name="editorPane"/>. Work posted from another thread goes to the
/// synchronization context it was made under - <see cref="UiThread"/>'s, for a live session.</summary>
internal sealed class FakeDebugHost(EditorPane? editorPane = null) : IDebugSessionHost
{
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;

    public ViceEmulator Vice { get; } = new(Path.Combine(Path.GetTempPath(), "no-vice-here"));
    public FakeDialogs Dialogs { get; } = new();
    IDialogs IDebugSessionHost.Dialogs => Dialogs;
    public List<string> Output { get; } = [];
    public List<Task> Fired { get; } = [];
    public bool BuildSucceeds { get; set; } = true;
    public int Builds { get; private set; }
    public int DebugTabShown { get; private set; }

    public void AppendOutputLine(string line) => Output.Add(line);
    public string? DebugStatus { get; private set; }
    public (string FilePath, int Line)? DebugLine { get; private set; }

    public void OnUiThread(Action action)
    {
        if (_ui is null || Environment.CurrentManagedThreadId == _uiThread)
            action();
        else
            _ui.Post(_ => action(), null);
    }

    /// <summary>Waits - letting posted work run - until <paramref name="condition"/> holds.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}. Status: {DebugStatus}. Output:\n{string.Join('\n', Output)}");
            await Task.Delay(20);
        }
    }
    public void Fire(Task task, string what) => Fired.Add(task);

    public bool Guard(string action, Action body)
    {
        body();
        return true;
    }

    public void OpenSymbol((string FilePath, int LineNumber) entry) => editorPane?.Open(entry.FilePath);
    public void CenterEditorOnLine(string filePath, int lineNumber) { }
    public void SetDebugLine((string FilePath, int Line)? location) => DebugLine = location;
    public void SetDebugStatus(string? status) => DebugStatus = status;
    public void ShowDebugTab() => DebugTabShown++;

    public Task<BuildResult?> BuildActiveProjectAsync()
    {
        Builds++;
        return Task.FromResult<BuildResult?>(new BuildResult(BuildSucceeds, BuildSucceeds ? 0 : 1, [], [], TimeSpan.Zero));
    }

    /// <summary>What <see cref="CheckStartupProjectRuns"/> answers - false for a library.</summary>
    public bool StartupProjectRuns { get; set; } = true;

    public bool CheckStartupProjectRuns(string action) => StartupProjectRuns;
}
