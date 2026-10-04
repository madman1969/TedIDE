using Tedide.App.Views;
using Tedide.Core.Navigation;
using Terminal.Gui.App;

namespace Tedide.App;

/// <summary>
/// Keeps the Document Outline (<see cref="DocumentOutlineView"/>) on the shown file: rebuilt from the
/// editor's text after a tab switch and once typing pauses for <see cref="Delay"/>, its selection
/// following the caret. A scan takes a few milliseconds, and only happens while the outline is on
/// screen - one that's hidden catches up when it's shown (<see cref="Shown"/>).
/// </summary>
internal sealed class DocumentOutlineTracking
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);

    private readonly Workspace _workspace;
    private readonly EditorPane _editorPane;
    private readonly DocumentOutlineView _view;
    private readonly Func<bool> _isShown;
    private readonly Debouncer _refresh;
    private bool _stale = true;

    public DocumentOutlineTracking(Workspace workspace, EditorPane editorPane, DocumentOutlineView view, Func<bool> isShown,
        Action<TimeSpan, Func<bool>>? addTimeout = null)
    {
        _workspace = workspace;
        _editorPane = editorPane;
        _view = view;
        _isShown = isShown;
        _refresh = new Debouncer(Delay, Refresh, addTimeout ?? ((delay, callback) => Application.AddTimeout(delay, callback)));

        _editorPane.Editor.ContentChanged += (_, _) => RequestRefresh();
        _editorPane.Editor.CaretChanged += (_, _) => FollowCaret();
    }

    /// <summary>After a tab switch, open or close: the new file's outline at once.</summary>
    public void ActiveDocumentChanged()
    {
        _refresh.Cancel();
        Refresh();
    }

    /// <summary>The outline has come on screen: bring it up to date if it missed changes.</summary>
    public void Shown()
    {
        if (_stale)
            Refresh();
    }

    private void RequestRefresh()
    {
        _stale = true;
        if (!_isShown())
            return;
        _refresh.Request();
    }

    internal void Refresh()
    {
        if (!_isShown())
        {
            _stale = true;
            return;
        }
        _stale = false;

        if (_editorPane.OpenPath is not { } path || SourceTokenizer.LanguageOf(path) == SourceLanguage.Other)
        {
            _view.Show(_editorPane.OpenPath, null);
            return;
        }
        var symbols = FileSymbols.Scan(path, _editorPane.Editor.Text, _workspace.MacrosFor(path).ToHashSet(StringComparer.Ordinal),
            (_, _, _) => null, DateTime.MaxValue);
        _view.Show(path, DocumentOutline.Build(symbols));
        FollowCaret();
    }

    private void FollowCaret()
    {
        if (!_isShown() || _editorPane.OpenPath is null || _editorPane.Editor.Document is not { } document)
            return;
        _view.FollowCaret(document.GetLineByOffset(Math.Min(_editorPane.Editor.CaretOffset, document.TextLength)).LineNumber);
    }
}
