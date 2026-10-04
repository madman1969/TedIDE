using Tedide.App.Views;
using Tedide.Core;
using Tedide.Core.Navigation;
using Terminal.Gui.App;
using Terminal.Gui.Editor.Completion;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;

namespace Tedide.App;

/// <summary>
/// Code completion and signature help in the editor: suggestions as a name is typed (after two
/// letters, straight after <c>.</c> or <c>-&gt;</c>, or on Ctrl+Space), and the signature
/// of the call being typed on the tab row.
/// <para>
/// The editor asks for suggestions synchronously on every keystroke, so they come from a
/// <see cref="SymbolIndex"/> kept in memory (well under a millisecond a request). Keeping it current
/// is what costs: the shown file is re-scanned from the editor's text <see cref="ReindexDelay"/>
/// after typing pauses, and project files and the headers they include are read in the background,
/// one refresh at a time, when the projects change, the shown file changes, or a file is saved.
/// </para>
/// </summary>
internal sealed class CodeCompletion : IEditorCompletionProvider
{
    /// <summary>How long after the last edit the shown file is re-scanned.</summary>
    internal static readonly TimeSpan ReindexDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>Asks for suggestions whatever's been typed. (Ctrl+J, Visual Studio's other key for
    /// it, never arrives: Windows Terminal sends it as a line feed.)</summary>
    internal static readonly Key[] TriggerKeys = [Key.Space.WithCtrl];

    private readonly IShell _shell;
    private readonly Workspace _workspace;
    private readonly EditorPane _editorPane;
    private readonly Action<TimeSpan, Func<bool>> _addTimeout;
    private readonly Action<Action> _post;
    private readonly Func<IReadOnlyList<string>> _libraryDirectories;

    /// <summary>Set by a trigger key, for the request that key causes.</summary>
    private bool _requested;
    private int _reindexGeneration;
    private bool _refreshing, _refreshAgain;
    private bool _enabled = true;

    /// <param name="post">Runs an action on the UI thread after the current key is handled -
    /// Terminal.Gui's main loop, or a test's.</param>
    /// <param name="libraryDirectories">cc65's include folders - found from CC65_HOME or PATH, or a test's.</param>
    public CodeCompletion(IShell shell, Workspace workspace, EditorPane editorPane, Action<TimeSpan, Func<bool>>? addTimeout = null,
        Action<Action>? post = null, Func<IReadOnlyList<string>>? libraryDirectories = null)
    {
        _shell = shell;
        _workspace = workspace;
        _editorPane = editorPane;
        _addTimeout = addTimeout ?? ((delay, callback) => Application.AddTimeout(delay, callback));
        // A zero timeout, not Application.Invoke: on the UI thread Invoke runs the action at once -
        // inside the key - and the editor then closed the list it opened (confirmed live).
        _post = post ?? (action => Application.AddTimeout(TimeSpan.Zero, () =>
        {
            action();
            return false;
        }));
        _libraryDirectories = libraryDirectories ?? (() => CodeNavigator.Cc65LibraryDirectories(
            Environment.GetEnvironmentVariable("CC65_HOME"), Environment.GetEnvironmentVariable("PATH")));

        _editorPane.Editor.CompletionProvider = this;
        _editorPane.Editor.ContentChanged += OnContentChanged;
        _editorPane.Editor.CaretChanged += (_, _) => UpdateHint();
        _workspace.Changed += RequestRefresh;
    }

    /// <summary>Everything completion knows - for tests.</summary>
    internal SymbolIndex Index { get; } = new();

    /// <summary>The background refresh running or last run - for tests to wait on.</summary>
    internal Task LastRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>View > Code Completion. Turned off, there are no suggestions or signatures, and
    /// nothing is indexed.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            _editorPane.Editor.CompletionProvider = value ? this : null;
            if (value)
            {
                ReindexShownFile();
                RequestRefresh();
            }
            UpdateHint();
        }
    }

    /// <summary>After a tab switch, open or close: the shown file is indexed from the editor at
    /// once, closed files go back to their saved copies, and the rest is refreshed.</summary>
    public void ActiveDocumentChanged()
    {
        if (!_enabled)
            return;
        foreach (var path in Index.ProjectFiles.Concat(_released).Where(p => !_editorPane.IsOpen(p)).ToList())
            Index.Release(path);
        _released.RemoveWhere(p => !_editorPane.IsOpen(p));
        ReindexShownFile();
        RequestRefresh();
        UpdateHint();
    }

    /// <summary>Files indexed from the editor that aren't project files (a cc65 header opened with
    /// Go To Definition), to release when they close.</summary>
    private readonly HashSet<string> _released = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A file was saved: other files may include it.</summary>
    public void FileSaved()
    {
        if (_enabled)
            RequestRefresh();
    }

    private void OnContentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (!_enabled)
            return;
        RequestReindex();

        // "." or "->" just typed: offer the members. Posted, so it runs after the editor has
        // finished with this key (which would otherwise close the list again at once).
        if (e.RemovalLength == 0 && e.InsertionLength == 1 && _editorPane.OpenPath is { } path
            && SourceTokenizer.LanguageOf(path) == SourceLanguage.C && _editorPane.Editor.Document is { } document)
        {
            var typed = e.InsertedText.Text;
            if (typed == "." || (typed == ">" && e.Offset > 0 && document.GetCharAt(e.Offset - 1) == '-'))
                _post(() =>
                {
                    if (_editorPane.IsShown(path) && !_editorPane.Editor.IsCompletionActive)
                        _editorPane.Editor.NewKeyDownEvent(TriggerKeys[0]);
                });
        }
    }

    /// <summary>Re-scans the shown file once edits pause for <see cref="ReindexDelay"/>.</summary>
    private void RequestReindex()
    {
        var generation = ++_reindexGeneration;
        _addTimeout(ReindexDelay, () =>
        {
            if (generation == _reindexGeneration)
                ReindexShownFile();
            return false;
        });
    }

    /// <summary>The shown file from the editor's text - a few milliseconds even for the largest
    /// sample file.</summary>
    internal void ReindexShownFile()
    {
        if (!_enabled || _editorPane.OpenPath is not { } path || SourceTokenizer.LanguageOf(path) == SourceLanguage.Other)
            return;
        Index.Update(path, _editorPane.Editor.Text);
        if (!Index.ProjectFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            _released.Add(path);
    }

    /// <summary>
    /// Brings the index up to date with the projects in the background: what they are is read here,
    /// on the UI thread; their files are listed and read on a thread-pool thread. One refresh at a
    /// time - a request while one runs is run once afterwards.
    /// </summary>
    public void RequestRefresh()
    {
        if (!_enabled)
            return;
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }

        var projects = _workspace.Projects.ToList();
        var includes = _workspace.Projects
            .SelectMany(p => p.IncludePaths.Select(i => Path.GetFullPath(Path.Combine(p.Directory, i))))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var macros = MacrosFor(_editorPane.OpenPath);
        _refreshing = true;
        LastRefresh = Task.Run(() =>
        {
            var files = projects
                .SelectMany(SolutionExplorerTree.EnumerateProjectFiles)
                .Where(f => SourceTokenizer.LanguageOf(f) != SourceLanguage.Other)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Index.Configure(files, includes, _libraryDirectories(), macros);
            Index.Refresh();
        });
        _shell.Fire(FinishRefreshAsync(LastRefresh), "Indexing symbols for code completion");
    }

    private async Task FinishRefreshAsync(Task refresh)
    {
        try
        {
            await refresh;
        }
        finally
        {
            _refreshing = false;
            if (_refreshAgain)
            {
                _refreshAgain = false;
                RequestRefresh();
            }
        }
    }

    /// <summary>The macros that decide which #if branches count: the target's own and the -D
    /// defines of the project <paramref name="path"/> belongs to (else the startup project).</summary>
    private IReadOnlyList<string> MacrosFor(string? path)
    {
        var project = (path is null ? null : _workspace.ProjectFor(path)) ?? _workspace.ActiveProject;
        return project is null ? [] : [.. project.Target.PredefinedMacros(), .. project.PreprocessorDefines.Select(d => d.Split('=', 2)[0].Trim())];
    }

    public IReadOnlyList<CompletionItem> GetCompletions(TextDocument document, int caretOffset, string prefix)
    {
        var requested = _requested || _editorPane.Editor.IsCompletionActive;
        _requested = false;
        if (!_enabled || _editorPane.ReadOnly || _editorPane.OpenPath is not { } path)
            return [];

        var cpu = _workspace.ProjectFor(path)?.ResolvedCc65Cpu ?? "6502";
        var found = CompletionEngine.Complete(Index, path, document.Text, caretOffset, prefix, requested, cpu);
        return Format(found);
    }

    /// <summary>Each suggestion as "name    kind", the kinds lined up in a column.</summary>
    internal static IReadOnlyList<CompletionItem> Format(IReadOnlyList<CompletionCandidate> candidates)
    {
        if (candidates.Count == 0)
            return [];
        var width = Math.Min(32, candidates.Max(c => c.Name.Length));
        return candidates
            .Select(c => new CompletionItem { Label = $"{c.Name.PadRight(width)}  {c.Kind}", InsertText = c.Name })
            .ToList();
    }

    public bool ShouldTrigger(Key key)
    {
        if (!_enabled || !TriggerKeys.Contains(key))
            return false;
        _requested = true;
        return true;
    }

    /// <summary>The signature of the call the caret is in, on the tab row - or nothing.</summary>
    private void UpdateHint()
    {
        if (!_enabled || _editorPane.OpenPath is not { } path || _editorPane.Editor.Document is null
            || SourceTokenizer.LanguageOf(path) != SourceLanguage.C)
        {
            _editorPane.SetSignatureHint("");
            return;
        }

        var hint = CompletionEngine.SignatureAt(Index, path, _editorPane.Editor.Text, _editorPane.Editor.CaretOffset);
        _editorPane.SetSignatureHint(hint?.Text ?? "", hint?.ActiveStart ?? 0, hint?.ActiveLength ?? 0);
    }
}
