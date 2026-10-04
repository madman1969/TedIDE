using Tedide.App.Views;
using Tedide.Core;

namespace Tedide.App.Tests;

/// <summary>
/// Checking as you type through <see cref="LiveErrorChecking"/>, with the compiler replaced by a
/// scripted checker and the timers run by hand - so "one check after a burst of edits", "never two
/// at once" and "a stale result is dropped" can be pinned down exactly.
/// </summary>
public sealed class LiveErrorCheckingTests : IDisposable
{
    private const string MainText = "int main(void)\n{\n    return 0\n}\n";

    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-live-").FullName;
    private readonly List<(TimeSpan Delay, Func<bool> Callback)> _timers = [];
    private readonly List<string> _checkedTexts = [];

    /// <summary>What the next check reports; a pending one holds it until the test completes it.</summary>
    private Func<string, Task<IReadOnlyList<BuildDiagnostic>?>> _answer = _ => Task.FromResult<IReadOnlyList<BuildDiagnostic>?>([]);

    public LiveErrorCheckingTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "include"));
        File.WriteAllText(MainC, MainText);
        File.WriteAllText(ScreenH, "void draw(void);\n");
        new TedideProject { Name = "Game", Target = Cc65Target.C64, SourceFiles = ["src/main.c"] }.Save(Path.Combine(_dir, "Game.tproj"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MainC => Path.Combine(_dir, "src", "main.c");
    private string ScreenH => Path.Combine(_dir, "include", "screen.h");

    /// <summary>Everything made on the test's UI thread, which the editor's documents need.</summary>
    private sealed record Setup(EditorPane Editor, ErrorListView ErrorList, FakeShell Shell, LiveErrorChecking Live);

    private Setup Make()
    {
        var workspace = new Workspace();
        workspace.OpenProject(Path.Combine(_dir, "Game.tproj"));
        var editor = new EditorPane();
        var errorList = new ErrorListView();
        var shell = new FakeShell(editor);
        var live = new LiveErrorChecking(shell, workspace, editor, errorList, path => Path.GetRelativePath(_dir, path),
            (_, _, text) =>
            {
                _checkedTexts.Add(text);
                return _answer(text);
            },
            (delay, callback) => _timers.Add((delay, callback)));
        editor.ActiveDocumentChanged += live.ActiveDocumentChanged;
        return new Setup(editor, errorList, shell, live);
    }

    /// <summary>Runs the timers due, then what they started.</summary>
    private async Task FireTimersAsync(FakeShell shell)
    {
        var due = _timers.ToList();
        _timers.Clear();
        foreach (var (_, callback) in due)
            callback();
        await shell.SettleAsync();
    }

    private static BuildDiagnostic Problem(string file, int line, DiagnosticSeverity severity, string message) => new(file, line, severity, message);

    [Fact]
    public Task ABurstOfEdits_RunsOneCheck_AfterThePause() => UiThread.Run(async () =>
    {
        var (editor, _, shell, _) = Make();
        editor.Open(MainC);

        editor.Editor.Document!.Insert(0, "a");
        editor.Editor.Document.Insert(0, "b");
        editor.Editor.Document.Insert(0, "c");
        Assert.All(_timers, t => Assert.Equal(LiveErrorChecking.Delay, t.Delay));
        await FireTimersAsync(shell);

        Assert.Equal(["cba" + MainText], _checkedTexts);  // only the last request's timer checks
    });

    [Fact]
    public Task Problems_AreUnderlined_AndTheCaretLinesShownOnTheTabRow() => UiThread.Run(async () =>
    {
        var (editor, errorList, shell, live) = Make();
        _answer = _ => Task.FromResult<IReadOnlyList<BuildDiagnostic>?>(
        [
            Problem(MainC, 3, DiagnosticSeverity.Warning, "Statement has no effect"),
            Problem(MainC, 3, DiagnosticSeverity.Error, "';' expected"),
            Problem(MainC, 1, DiagnosticSeverity.Warning, "Unused parameter"),
            Problem(ScreenH, 1, DiagnosticSeverity.Error, "Header problem"),
        ]);
        editor.Open(MainC);
        await FireTimersAsync(shell);

        Assert.Equal(new Dictionary<int, DiagnosticSeverity> { [3] = DiagnosticSeverity.Error, [1] = DiagnosticSeverity.Warning }, live.MarkedLines);
        Assert.Equal(4, errorList.Table!.Rows);  // the header's problem is listed, not underlined

        editor.Editor.CaretOffset = editor.Editor.Document!.GetLineByNumber(3).Offset;
        Assert.Equal("Error: ';' expected (+1 more)", editor.Notice);
        editor.Editor.CaretOffset = 0;
        Assert.Equal("Warning: Unused parameter", editor.Notice);
        editor.Editor.CaretOffset = editor.Editor.Document.GetLineByNumber(2).Offset;
        Assert.Equal("", editor.Notice);
    });

    [Fact]
    public Task ARequest_WhileACheckRuns_WaitsForIt_ThenChecksTheNewText() => UiThread.Run(async () =>
    {
        var (editor, _, shell, live) = Make();
        var first = new TaskCompletionSource<IReadOnlyList<BuildDiagnostic>?>();
        _answer = _ => first.Task;
        editor.Open(MainC);
        foreach (var (_, callback) in _timers.ToList())
            callback();
        _timers.Clear();
        Assert.Single(_checkedTexts);

        _answer = _ => Task.FromResult<IReadOnlyList<BuildDiagnostic>?>([Problem(MainC, 2, DiagnosticSeverity.Error, "new")]);
        editor.Editor.Document!.Insert(0, "x");
        // Fired by hand: settling would wait for the first check, which is still being held.
        foreach (var (_, callback) in _timers.ToList())
            callback();
        _timers.Clear();
        Assert.Single(_checkedTexts);  // still one: the second waits

        first.SetResult([Problem(MainC, 4, DiagnosticSeverity.Error, "stale")]);
        await shell.SettleAsync();

        Assert.Equal(2, _checkedTexts.Count);
        Assert.Equal("x" + MainText, _checkedTexts[1]);
        Assert.Equal([2], live.MarkedLines.Keys);  // the first check's result was for old text - dropped
    });

    [Fact]
    public Task OnlySourceFilesInAProject_AreChecked() => UiThread.Run(async () =>
    {
        var (editor, _, shell, _) = Make();
        var outside = Path.Combine(Path.GetTempPath(), $"tedide-outside-{Guid.NewGuid():N}.c");
        File.WriteAllText(outside, "int x;\n");
        try
        {
            editor.Open(ScreenH);
            await FireTimersAsync(shell);
            editor.Open(outside);
            await FireTimersAsync(shell);
        }
        finally
        {
            File.Delete(outside);
        }

        Assert.Empty(_checkedTexts);
    });

    [Fact]
    public Task NoCompiler_ChangesNothing_AndClosingAFile_DropsItsProblems() => UiThread.Run(async () =>
    {
        var (editor, errorList, shell, live) = Make();
        _answer = _ => Task.FromResult<IReadOnlyList<BuildDiagnostic>?>([Problem(MainC, 3, DiagnosticSeverity.Error, "';' expected")]);
        editor.Open(MainC);
        await FireTimersAsync(shell);
        Assert.Equal(1, errorList.Table!.Rows);

        _answer = _ => Task.FromResult<IReadOnlyList<BuildDiagnostic>?>(null);
        editor.Editor.Document!.Insert(0, "x");
        await FireTimersAsync(shell);
        Assert.Single(live.MarkedLines);  // the last real result stays

        editor.Close(MainC);
        Assert.Equal(0, errorList.Table!.Rows);
        Assert.Null(live.ResultsFor(MainC));
    });

    [Fact]
    public Task TurningItOff_ClearsWhatItFound_AndStopsChecking() => UiThread.Run(async () =>
    {
        var (editor, errorList, shell, live) = Make();
        _answer = _ => Task.FromResult<IReadOnlyList<BuildDiagnostic>?>([Problem(MainC, 3, DiagnosticSeverity.Error, "';' expected")]);
        editor.Open(MainC);
        await FireTimersAsync(shell);
        editor.Editor.CaretOffset = editor.Editor.Document!.GetLineByNumber(3).Offset;
        Assert.NotEqual("", editor.Notice);

        live.Enabled = false;
        Assert.Empty(live.MarkedLines);
        Assert.Equal("", editor.Notice);
        Assert.Equal(0, errorList.Table!.Rows);

        editor.Editor.Document.Insert(0, "x");
        await FireTimersAsync(shell);
        Assert.Single(_checkedTexts);  // no more checks

        live.Enabled = true;
        await FireTimersAsync(shell);
        Assert.Equal(2, _checkedTexts.Count);
        Assert.Single(live.MarkedLines);
    });
}

public class ErrorListMergeTests
{
    private static readonly BuildDiagnostic BuildMain = new(@"C:\g\main.c", 3, DiagnosticSeverity.Error, "old");
    private static readonly BuildDiagnostic BuildSound = new(@"C:\g\sound.c", 7, DiagnosticSeverity.Warning, "kept");
    private static readonly BuildDiagnostic LiveMain = new(@"C:\g\main.c", 4, DiagnosticSeverity.Error, "new");

    [Fact]
    public void ACheckedFile_ReplacesItsBuildEntries_OthersStay()
    {
        var merged = ErrorListView.Merge([BuildMain, BuildSound],
            new Dictionary<string, IReadOnlyList<BuildDiagnostic>>(StringComparer.OrdinalIgnoreCase) { [@"c:\G\MAIN.C"] = [LiveMain] });

        Assert.Equal([BuildSound, LiveMain], merged);
    }

    [Fact]
    public void ANewBuild_ReplacesEarlierChecks()
    {
        var list = new ErrorListView();
        list.SetLiveDiagnostics(LiveMain.FilePath, [LiveMain]);

        list.SetDiagnostics([BuildSound]);
        list.SetLiveDiagnostics(@"C:\g\other.c", []);

        Assert.Equal(1, list.Table!.Rows);
    }
}
