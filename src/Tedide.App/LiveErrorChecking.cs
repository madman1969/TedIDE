using Tedide.App.Views;
using Tedide.Build;
using Tedide.Core;
using Terminal.Gui.App;

namespace Tedide.App;

/// <summary>
/// Checking as you type: the file being shown is compiled - not built - once typing pauses (see
/// <see cref="SourceChecker"/>), and what it finds is underlined in the editor, named on the tab
/// row while the caret is on its line, and listed in the Error List.
/// <para>
/// Kept cheap: a check starts only after <see cref="Delay"/> without an edit, tab switch or save
/// (a burst of typing runs one), only for the shown file, and never while another is running -
/// a request then waits for it and runs once after. A check's result is dropped if the text
/// changed while it ran; the check that follows replaces it.
/// </para>
/// </summary>
internal sealed class LiveErrorChecking
{
    /// <summary>How long after the last edit a check starts.</summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(600);

    /// <summary>Checks <c>text</c>, the current text of <c>path</c> in <c>project</c> - see
    /// <see cref="SourceChecker.CheckAsync"/>. Null if the compiler couldn't be run.</summary>
    internal delegate Task<IReadOnlyList<BuildDiagnostic>?> Checker(TedideProject project, string path, string text);

    private readonly IShell _shell;
    private readonly Workspace _workspace;
    private readonly EditorPane _editorPane;
    private readonly ErrorListView _errorList;
    private readonly Func<string, string> _displayPath;
    private readonly Checker _check;
    private readonly Action<TimeSpan, Func<bool>> _addTimeout;
    private readonly DiagnosticLineTransformer _lines = new();

    /// <summary>The last check's problems for each open file it has checked.</summary>
    private readonly Dictionary<string, IReadOnlyList<BuildDiagnostic>> _results = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bumped by every request; a check whose number is no longer current is stale.</summary>
    private int _generation;
    private bool _checking, _checkAgain;

    public LiveErrorChecking(IShell shell, Workspace workspace, EditorPane editorPane, ErrorListView errorList,
        Func<string, string> displayPath, Checker? check = null, Action<TimeSpan, Func<bool>>? addTimeout = null)
    {
        _shell = shell;
        _workspace = workspace;
        _editorPane = editorPane;
        _errorList = errorList;
        _displayPath = displayPath;
        _check = check ?? CheckWithCc65;
        _addTimeout = addTimeout ?? ((delay, callback) => Application.AddTimeout(delay, callback));

        _editorPane.Editor.ContentChanged += (_, _) => RequestCheck();
        _editorPane.Editor.CaretChanged += (_, _) => UpdateNotice();
    }

    /// <summary>
    /// View > Check As You Type. Turned off, nothing is checked, and what earlier checks found goes
    /// from the editor and the Error List at once; turned back on, the shown file is checked soon.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            if (value)
            {
                RequestCheck();
                return;
            }
            ++_generation; // a check still running is now stale
            foreach (var file in _results.Keys.ToList())
                _errorList.SetLiveDiagnostics(file, null, _displayPath);
            _results.Clear();
            ShowResults();
        }
    }

    private bool _enabled = true;

    /// <summary>The problems the last check found in <paramref name="path"/>, if it's been checked.</summary>
    internal IReadOnlyList<BuildDiagnostic>? ResultsFor(string path) => _results.GetValueOrDefault(path);

    /// <summary>Underlines the problem lines. The shell registers it after syntax highlighting and
    /// before the breakpoint and debugger highlights, which win on the same line.</summary>
    internal DiagnosticLineTransformer LineTransformer => _lines;

    /// <summary>The lines underlined in the shown file - for tests.</summary>
    internal IReadOnlyDictionary<int, DiagnosticSeverity> MarkedLines => _lines.Lines;

    private SourceChecker? _sourceChecker;

    /// <summary>The real check: the project's linked libraries' include folders come after its own,
    /// as in a build. cc65 and ca65 are found on PATH, as cl65 is for a build.</summary>
    private Task<IReadOnlyList<BuildDiagnostic>?> CheckWithCc65(TedideProject project, string path, string text)
    {
        IReadOnlyList<string> libraryIncludes;
        try
        {
            libraryIncludes = ProjectGraph.LinkedLibraries(_workspace.Projects, project)
                .SelectMany(l => l.ResolvedIncludePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (InvalidDataException)
        {
            libraryIncludes = []; // a bad reference - the build reports it
        }
        _sourceChecker ??= new SourceChecker();
        return _sourceChecker.CheckAsync(project, path, text, libraryIncludes);
    }

    /// <summary>After a tab switch, open or close: the shown file's last results at once, and a
    /// fresh check soon. Results for files no longer open are dropped.</summary>
    public void ActiveDocumentChanged()
    {
        foreach (var closed in _results.Keys.Where(p => !_editorPane.IsOpen(p)).ToList())
        {
            _results.Remove(closed);
            _errorList.SetLiveDiagnostics(closed, null, _displayPath);
        }
        ShowResults();
        RequestCheck();
    }

    /// <summary>Checks the shown file once nothing else has asked for <see cref="Delay"/> - after
    /// an edit, a tab switch, or a save (which may have changed a header it includes).</summary>
    public void RequestCheck()
    {
        if (!_enabled)
            return;
        var generation = ++_generation;
        _addTimeout(Delay, () =>
        {
            if (generation == _generation)
                _shell.Fire(CheckAsync(), "Checking for errors");
            return false;
        });
    }

    private async Task CheckAsync()
    {
        if (_checking)
        {
            _checkAgain = true;
            return;
        }
        if (_editorPane.OpenPath is not { } path || !SourceChecker.CanCheck(path) || _workspace.ProjectFor(path) is not { } project)
            return;

        // Read here, on the UI thread - the document belongs to it.
        var generation = _generation;
        var text = _editorPane.Editor.Text;
        IReadOnlyList<BuildDiagnostic>? found;
        _checking = true;
        try
        {
            found = await _check(project, path, text);
        }
        finally
        {
            _checking = false;
        }

        // A result for text that has changed since is dropped: the check after it is on its way.
        if (generation == _generation && found is not null && _editorPane.IsOpen(path))
        {
            // Named in the Error List's Project column like a build's, when there's more than one.
            if (_workspace.Projects.Count > 1)
                found = found.Select(d => d with { Project = project.Name }).ToList();
            _results[path] = found;
            _errorList.SetLiveDiagnostics(path, found, _displayPath);
            ShowResults();
        }

        if (_checkAgain)
        {
            _checkAgain = false;
            _shell.Fire(CheckAsync(), "Checking for errors");
        }
    }

    /// <summary>Underlines the shown file's problem lines, and updates the tab row's notice.</summary>
    private void ShowResults()
    {
        _lines.Lines.Clear();
        if (_editorPane.OpenPath is { } path && _results.TryGetValue(path, out var found))
        {
            foreach (var diagnostic in found.Where(d => d.Line > 0 && EditorPane.SamePath(d.FilePath, path)))
            {
                if (!_lines.Lines.TryGetValue(diagnostic.Line, out var worst) || diagnostic.Severity > worst)
                    _lines.Lines[diagnostic.Line] = diagnostic.Severity;
            }
        }
        _editorPane.Editor.SetNeedsDraw();
        UpdateNotice();
    }

    /// <summary>The caret line's problem on the tab row - the first error, else the first warning.</summary>
    private void UpdateNotice()
    {
        if (_editorPane.OpenPath is not { } path || _editorPane.Editor.Document is null
            || !_lines.Lines.ContainsKey(_editorPane.CaretPosition.Line) || !_results.TryGetValue(path, out var found))
        {
            _editorPane.SetNotice("", isError: false);
            return;
        }

        var line = _editorPane.CaretPosition.Line;
        var onLine = found.Where(d => d.Line == line && EditorPane.SamePath(d.FilePath, path))
            .OrderByDescending(d => d.Severity).ToList();
        var first = onLine[0];
        var more = onLine.Count > 1 ? $" (+{onLine.Count - 1} more)" : "";
        _editorPane.SetNotice($"{first.Severity}: {first.Message}{more}", first.Severity == DiagnosticSeverity.Error);
    }
}
