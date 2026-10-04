using System.Text;
using Tedide.Core;
using Terminal.Gui.App;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Document.Folding;
using Terminal.Gui.Editor.Highlighting;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// The editor area: any number of open files, one shown at a time, with a <see cref="DocumentTabStrip"/>
/// above a single shared <see cref="Editor"/>. Each open file keeps its own <see cref="TextDocument"/>
/// (and so its own undo history), encoding, highlighting and caret; switching tabs swaps that
/// document into the editor, the same document-swapping pattern Terminal.Gui.Editor's reference app
/// ("ted") uses for its one file - ClearSelection() before the swap, a fresh TextDocument per load,
/// MarkAsOriginalFile() only after a save, and never a custom Cursor/CursorStyle.
/// <para>
/// Nothing here prompts: callers check <see cref="IsModified(string)"/> and save or confirm with the
/// user before closing a modified file.
/// </para>
/// </summary>
public sealed class EditorPane : View
{
    /// <summary>One open file.</summary>
    private sealed class OpenDocument(string path, TextDocument document, Encoding encoding)
    {
        public string Path { get; set; } = path;
        public TextDocument Document { get; } = document;
        public Encoding Encoding { get; set; } = encoding;
        public int CaretOffset { get; set; }
        public bool IsModified => !Document.UndoStack.IsOriginalFile;

        /// <summary>Its {...} blocks - see <see cref="UpdateFolds"/>. Kept with the tab, so what was
        /// collapsed stays collapsed when it's shown again.</summary>
        public FoldingManager? Folds { get; set; }

        /// <summary>Its text changed since <see cref="Folds"/> were last brought up to date.</summary>
        public bool FoldsStale { get; set; } = true;
    }

    private readonly List<OpenDocument> _documents = [];
    private OpenDocument? _active;
    private readonly DocumentTabStrip _tabStrip = new() { X = 0, Y = 0 };
    private bool _readOnly;

    /// <summary>The single shared Editor instance, exposed so the host can wire up an
    /// <see cref="Terminal.Gui.Editor.EditorMenuBar"/>/<see cref="Terminal.Gui.Editor.EditorStatusBar"/>
    /// against it, the same way ted wires its own menu/status bar to its one Editor.</summary>
    public Editor Editor { get; }

    /// <summary>Shows a problem with the caret's line at the right of the tab row, in place of
    /// <see cref="Annotation"/> - see <see cref="DocumentTabStrip.Notice"/>. Empty clears it.</summary>
    public void SetNotice(string text, bool isError)
    {
        _tabStrip.NoticeIsError = isError;
        _tabStrip.Notice = text;
    }

    /// <summary>The problem shown for the caret's line, or empty.</summary>
    public string Notice => _tabStrip.Notice;

    /// <summary>Shows the signature of the call being typed on the tab row, its active argument
    /// picked out - see <see cref="DocumentTabStrip.Hint"/>. Empty text clears it.</summary>
    public void SetSignatureHint(string text, int activeStart = 0, int activeLength = 0) =>
        _tabStrip.SetHint(text, activeStart, activeLength);

    /// <summary>The signature shown, or empty.</summary>
    public string SignatureHint => _tabStrip.Hint;

    /// <summary>Text shown at the right of the tab row - see <see cref="DocumentTabStrip.Annotation"/>.</summary>
    public string Annotation
    {
        get => _tabStrip.Annotation;
        set => _tabStrip.Annotation = value;
    }

    /// <summary>The file shown in the editor, or null if none is open.</summary>
    public string? OpenPath => _active?.Path;

    /// <summary>Every open file, in tab order.</summary>
    public IReadOnlyList<string> OpenPaths => _documents.Select(d => d.Path).ToList();

    /// <summary>Raised whenever a different file (or none) becomes the one shown, and when the
    /// shown file is renamed - so the host can refresh anything tied to "the open file".</summary>
    public event Action? ActiveDocumentChanged;

    /// <summary>Raised when "Find in Files..." is chosen from the editor's right-click context
    /// menu, carrying the current selection (empty if there is none) to pre-populate the Find in
    /// Files dialog's search field with. Just the selection's first line - the search field it
    /// feeds is single-line, and a multi-line selection is rarely a meaningful search term anyway.</summary>
    public event Action<string>? FindInFilesRequested;

    /// <summary>Raised by the context menu's Go To Definition, for the symbol at the caret.</summary>
    public event Action? GoToDefinitionRequested;

    /// <summary>Raised by the context menu's Find All References, for the symbol at the caret.</summary>
    public event Action? FindReferencesRequested;

    /// <summary>Raised by the context menu's Rename Symbol, for the symbol at the caret.</summary>
    public event Action? RenameSymbolRequested;

    /// <summary>Raised by the context menu's Compare with Last Commit, for the file shown.</summary>
    public event Action? CompareWithHeadRequested;

    /// <summary>Raised by the context menu's Blame, for the file shown.</summary>
    public event Action? BlameRequested;

    /// <summary>Raised by the context menu's File History, for the file shown.</summary>
    public event Action? FileHistoryRequested;

    /// <summary>Raised when a tab's close button (or a middle-click, or Ctrl+W in the editor) asks
    /// to close that file. The host decides - it may need to ask about unsaved changes first.</summary>
    public event Action<string>? CloseRequested;

    /// <summary>The caret's 1-based line and column, in characters - the same coordinates
    /// <see cref="Tedide.Core.Navigation.CodeNavigator"/> works in.</summary>
    public (int Line, int Column) CaretPosition
    {
        get
        {
            var line = Editor.Document!.GetLineByOffset(Editor.CaretOffset);
            return (line.LineNumber, Editor.CaretOffset - line.Offset + 1);
        }
    }

    /// <summary>Makes every open file read-only (a debug session), or editable again. Applies to
    /// whichever file is shown and to any switched to while it's set.</summary>
    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            Editor.ReadOnly = value || _active is null;
        }
    }

    public EditorPane()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
        // A View's SuperView chain must all have CanFocus = true for SetFocus() to reach a
        // descendant (see Terminal.Gui's View.CanFocus docs) - without this, Editor.SetFocus()
        // in Activate() below silently fails since this plain View defaults to CanFocus = false.
        CanFocus = true;

        Editor = new Editor
        {
            X = 0,
            Y = Pos.Bottom(_tabStrip),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            GutterOptions = GutterOptions.LineNumbers | GutterOptions.Folding,
            ReadOnly = true, // no file open yet
            Document = new TextDocument(string.Empty),
            // No FoldingStrategy: the folds are kept here instead - see UpdateFolds.
            // HasScrollBars uses ScrollBarVisibilityMode.Auto - scrollbars only appear once the
            // document overflows the viewport, rather than being permanently shown.
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };

        // Editor.FindRequested/ReplaceRequested fire whenever Command.Find/Command.Replace runs -
        // both Ctrl+F/Ctrl+H and the library's own Edit menu - but it's the consumer's job to
        // subscribe and open a dialog. FindReplaceDialog(Editor, bool) is the exact call ted's own
        // ShowFindReplaceDialog makes.
        Editor.FindRequested += (_, _) => Application.Run(new FindReplaceDialog(Editor, false));
        Editor.ReplaceRequested += (_, _) => Application.Run(new FindReplaceDialog(Editor, true));

        // Ctrl+PgDn/Ctrl+PgUp move between open files, as in VS Code (Windows Terminal keeps
        // Ctrl+Tab for its own tabs). Caught on KeyDown, before the editor's own key handling.
        Editor.KeyDown += (_, key) =>
        {
            if (key == Key.PageDown.WithCtrl)
                key.Handled = CycleDocument(1);
            else if (key == Key.PageUp.WithCtrl)
                key.Handled = CycleDocument(-1);
            // File > Close File's key. The editor claims Ctrl+W for itself, so the menu item's
            // application-wide binding never sees it while the editor has focus (confirmed live).
            else if (key == Key.W.WithCtrl && _active is not null)
            {
                CloseRequested?.Invoke(_active.Path);
                key.Handled = true;
            }
        };

        // The editor's own right-click context menu covers Undo/Redo/Cut/Copy/Paste/Select All;
        // our navigation and search items are appended to it, invoking the same commands the Edit
        // menu does rather than constructing dialogs a second way.
        var contextMenuItems = Editor.ContextMenu!.Root!;
        contextMenuItems.Add(new Line());
        // All three act on the symbol at the caret. The keys are shown as help text only: AppShell's
        // OnKeyDown handles F12/Shift+F12/F2, since this menu only exists while it's open.
        contextMenuItems.Add(new MenuItem("Go To Definition", "F12", () => GoToDefinitionRequested?.Invoke()));
        contextMenuItems.Add(new MenuItem("Find All References", "Shift+F12", () => FindReferencesRequested?.Invoke()));
        contextMenuItems.Add(new MenuItem("Rename Symbol...", "F2", () => RenameSymbolRequested?.Invoke()));
        contextMenuItems.Add(new Line());
        contextMenuItems.Add(new MenuItem("Find...", "", () => Editor.InvokeCommand(Command.Find)));
        contextMenuItems.Add(new MenuItem("Replace...", "", () => Editor.InvokeCommand(Command.Replace)));
        contextMenuItems.Add(new MenuItem("Find in Files...", "", () =>
            FindInFilesRequested?.Invoke(Editor.SelectedText.Split(['\r', '\n'], 2)[0])));
        contextMenuItems.Add(new Line());
        contextMenuItems.Add(new MenuItem("Collapse All Folds", "", () => SetAllFolded(true)));
        contextMenuItems.Add(new MenuItem("Expand All Folds", "", () => SetAllFolded(false)));
        contextMenuItems.Add(new Line());
        contextMenuItems.Add(new MenuItem("Compare with Last Commit", "", () => CompareWithHeadRequested?.Invoke()));
        contextMenuItems.Add(new MenuItem("Blame", "", () => BlameRequested?.Invoke()));
        contextMenuItems.Add(new MenuItem("File History", "", () => FileHistoryRequested?.Invoke()));

        _tabStrip.TabSelected += index => Activate(_documents[index]);
        _tabStrip.TabCloseRequested += index => CloseRequested?.Invoke(_documents[index].Path);
        _tabStrip.TabCycleRequested += direction => CycleDocument(direction);

        Add(_tabStrip, Editor);
    }

    private OpenDocument? Find(string path) =>
        _documents.FirstOrDefault(d => SamePath(d.Path, path));

    /// <summary>
    /// Whether two paths name the same file, however they're spelled: the debugger hands over
    /// "Game\src/main.c" and "Game\..\Gfx\src\gfx.c" for files already open as "Game\src\main.c"
    /// and "Gfx\src\gfx.c", and comparing them as text opened each a second time (confirmed live).
    /// </summary>
    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => System.IO.Path.GetFullPath(path);

    /// <summary>Whether <paramref name="path"/> is the file shown in the editor.</summary>
    public bool IsShown(string? path) => SamePath(_active?.Path, path);

    public bool IsOpen(string path) => Find(path) is not null;

    /// <summary>
    /// Shows <paramref name="filePath"/>: switches to its tab if it's already open, otherwise
    /// loads it into a new tab just after the current one. A file that can't be read throws
    /// before anything changes.
    /// </summary>
    public void Open(string filePath)
    {
        filePath = Normalize(filePath);
        if (Find(filePath) is { } existing)
        {
            Activate(existing);
            return;
        }

        // SourceFileText, not File.ReadAllText: keeps a non-UTF-8 file's bytes intact across a
        // save (see that class). Read before touching any state, so a file that can't be read
        // leaves everything as it was.
        var (text, encoding) = File.Exists(filePath)
            ? SourceFileText.Read(filePath)
            : (string.Empty, (Encoding)new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        // A fresh TextDocument per load, as ted's own SetDocument() does - its undo stack starts clean.
        var document = new OpenDocument(filePath, new TextDocument(text), encoding);
        // Keep the "*" on the tab current as the file is edited, undone, and saved.
        // Folds follow edits that could change them - once the change is complete (TextChanged),
        // not during it (Changed): updated mid-change, the fold manager then shifted them again
        // for the same edit, leaving every fold off by the edit's length.
        document.Document.Changed += (_, e) =>
        {
            if (_foldingStrategy.ChangeMayAffectFoldings(e))
                document.FoldsStale = true;
        };
        document.Document.TextChanged += (_, _) =>
        {
            if (document.FoldsStale && document == _active)
                UpdateFolds(document);
            RefreshTabs();
        };
        document.Document.UndoStack.PropertyChanged += (_, _) => RefreshTabs();

        var index = _active is null ? _documents.Count : _documents.IndexOf(_active) + 1;
        _documents.Insert(index, document);
        Activate(document);
    }

    /// <summary>Swaps <paramref name="document"/> into the editor, remembering where the caret was
    /// in the one it replaces.</summary>
    private void Activate(OpenDocument document)
    {
        if (_active == document)
        {
            Editor.SetFocus();
            return;
        }

        if (_active is not null)
            _active.CaretOffset = Editor.CaretOffset;

        // ted's own SetDocument() clears any selection before swapping in a document.
        Editor.ClearSelection();
        // The last tab's folds go first: swapping the document under them reopened them.
        Editor.FoldingManager = null;
        Editor.Document = document.Document;
        document.Folds ??= new FoldingManager(document.Document);
        if (document.FoldsStale)
            UpdateFolds(document, attach: true);
        else
            Editor.FoldingManager = document.Folds;
        Editor.HighlightingDefinition = HighlightingManager.Instance.GetDefinitionByExtension(System.IO.Path.GetExtension(document.Path));
        Editor.CaretOffset = Math.Clamp(document.CaretOffset, 0, document.Document.TextLength);
        Editor.ReadOnly = _readOnly;
        _active = document;
        RefreshTabs();
        ActiveDocumentChanged?.Invoke();

        // Selecting a file in the persistent Solution Explorer doesn't hand focus back the way
        // closing a modal would, so the editor takes it explicitly.
        Editor.SetFocus();
    }

    /// <summary>
    /// What folds: any {...} spanning more than one line, which covers C; ca65 assembly has no
    /// braces, so .s/.asm/.inc files show no fold points. Run here rather than handed to the editor
    /// as its FoldingStrategy: the editor adds each fold to a manager it's watching, and redoes its
    /// layout for every one - 2.4 seconds to open FarMem's 814-line farmem.c, with 119 folds.
    /// </summary>
    private readonly BraceFoldingStrategy _foldingStrategy = new();

    /// <summary>
    /// Brings a tab's folds up to date with its text, with the editor not watching while it happens
    /// (0.3ms for farmem.c, against a second watched) - then hands them to the editor if
    /// <paramref name="attach"/> is set or the tab is the one shown. Collapsed blocks stay
    /// collapsed: the strategy's update reopened every block holding another.
    /// </summary>
    private void UpdateFolds(OpenDocument document, bool attach = false)
    {
        if (document.Folds is not { } folds)
            return;
        var shown = attach || document == _active;
        if (shown && Editor.FoldingManager is not null)
            Editor.FoldingManager = null;
        var collapsed = folds.AllFoldings.Where(f => f.IsFolded).Select(f => (f.StartOffset, f.EndOffset)).ToHashSet();
        _foldingStrategy.UpdateFoldings(folds, document.Document);
        if (collapsed.Count > 0)
            foreach (var fold in folds.AllFoldings)
                if (!fold.IsFolded && collapsed.Contains((fold.StartOffset, fold.EndOffset)))
                    fold.IsFolded = true;
        document.FoldsStale = false;
        if (shown)
            Editor.FoldingManager = folds;
    }

    /// <summary>
    /// Collapse All Folds / Expand All Folds: folds, or unfolds, every foldable region in the shown
    /// file, nested ones included - so expanding one function afterwards still leaves its inner
    /// blocks folded, as in VS Code. A caret left inside a collapsed region moves to its first
    /// line (the editor's own FoldingChanged handling). Returns how many regions changed.
    /// </summary>
    public int SetAllFolded(bool folded)
    {
        if (_active is null || Editor.FoldingManager is not { } folding)
            return 0;
        var changing = folding.AllFoldings.Where(f => f.IsFolded != folded).ToList();
        if (changing.Count == 0)
            return 0;
        // All but one with the editor not watching: it redoes its layout for every fold that
        // changes (half a minute for 600 folds), and reopened some of the outer ones as it went.
        Editor.FoldingManager = null;
        foreach (var section in changing.Skip(1))
            section.IsFolded = folded;
        Editor.FoldingManager = folding;
        // The last one with it watching: attaching a manager doesn't rebuild the editor's map of
        // which lines are hidden - only a FoldingChanged does - so the markers changed but the
        // text didn't until a fold was next toggled.
        changing[0].IsFolded = folded;
        // What the editor did itself for each change it saw: a caret left in a hidden line moves to
        // the start of the outermost collapsed block holding it, the line that still shows.
        var caret = Editor.CaretOffset;
        if (Editor.Document is { } document && folding.IsLineHidden(document.GetLineByOffset(caret).LineNumber)
            && folding.GetFoldingsContaining(caret).Where(f => f.IsFolded).MinBy(f => f.StartOffset) is { } outer)
            Editor.CaretOffset = outer.StartOffset;
        Editor.SetNeedsDraw();
        return changing.Count;
    }

    /// <summary>Moves to the next (+1) or previous (-1) open file, wrapping round. False if
    /// there's nothing to move to.</summary>
    public bool CycleDocument(int direction)
    {
        if (_documents.Count < 2 || _active is null)
            return false;
        var index = (_documents.IndexOf(_active) + direction + _documents.Count) % _documents.Count;
        Activate(_documents[index]);
        return true;
    }

    /// <summary>Closes the file shown in the editor, if any. No save prompt - see the class summary.</summary>
    public void Close()
    {
        if (_active is not null)
            Close(_active.Path);
    }

    /// <summary>Closes <paramref name="path"/> if it's open, showing the neighbouring tab (the one
    /// to its right, else its left) if it was the shown one. No save prompt.</summary>
    public void Close(string path)
    {
        if (Find(path) is not { } document)
            return;

        var index = _documents.IndexOf(document);
        _documents.RemoveAt(index);
        if (document != _active)
        {
            RefreshTabs();
            return;
        }

        _active = null;
        if (_documents.Count > 0)
        {
            Activate(_documents[Math.Min(index, _documents.Count - 1)]);
            return;
        }

        Editor.ClearSelection();
        Editor.FoldingManager = null;
        Editor.Document = new TextDocument(string.Empty);
        Editor.ReadOnly = true;
        RefreshTabs();
        ActiveDocumentChanged?.Invoke();
    }

    /// <summary>Closes every open file. No save prompt.</summary>
    public void CloseAll()
    {
        foreach (var path in OpenPaths)
            Close(path);
    }

    /// <summary>Whether the shown file has unsaved changes.</summary>
    public bool IsModified => _active?.IsModified ?? false;

    public bool IsModifiedFile(string path) => Find(path)?.IsModified ?? false;

    /// <summary>Every open file with unsaved changes, in tab order.</summary>
    public IReadOnlyList<string> ModifiedPaths => _documents.Where(d => d.IsModified).Select(d => d.Path).ToList();

    /// <summary>An open file's current text, unsaved edits included - null if it isn't open.</summary>
    public string? TextOf(string path) => Find(path)?.Document.Text;

    /// <summary>An open file's document, for editing it in place (e.g. a rename) - null if it isn't open.</summary>
    public TextDocument? DocumentOf(string path) => Find(path)?.Document;

    /// <summary>Points an open file at the new path its file was just moved to, keeping its buffer
    /// (unsaved edits included).</summary>
    public void Rename(string oldPath, string newPath)
    {
        if (Find(oldPath) is not { } document)
            return;
        document.Path = Normalize(newPath);
        RefreshTabs();
        if (document == _active)
        {
            Editor.HighlightingDefinition = HighlightingManager.Instance.GetDefinitionByExtension(System.IO.Path.GetExtension(newPath));
            ActiveDocumentChanged?.Invoke();
        }
    }

    /// <summary>
    /// Re-reads an open file from disk - after git discarded its changes, say - replacing its text
    /// (unsaved edits included) and starting its undo history afresh. Does nothing if it isn't open.
    /// </summary>
    public void Reload(string path)
    {
        if (Find(path) is not { } document)
            return;
        var (text, encoding) = SourceFileText.Read(document.Path);
        if (document == _active)
            Editor.ClearSelection();
        document.Document.Text = text;
        document.Document.UndoStack.ClearAll();
        document.Document.UndoStack.MarkAsOriginalFile();
        document.Encoding = encoding;
        document.CaretOffset = Math.Min(document.CaretOffset, text.Length);
        RefreshTabs();
    }

    /// <summary>Saves the shown file - see <see cref="Save(string)"/>.</summary>
    public string? Save() => _active is null ? null : Save(_active.Path);

    /// <summary>
    /// Writes an open file back in the encoding it was read in (see <see cref="SourceFileText"/>).
    /// Returns a message for the user if that wasn't possible - the text gained a character the
    /// original encoding can't hold, so it was saved as UTF-8 instead - otherwise null. Throws on a
    /// file-system error (read-only file, locked, ...); callers guard that - see AppShell.Guard.
    /// </summary>
    public string? Save(string path)
    {
        if (Find(path) is not { } document)
            return null;

        var used = SourceFileText.Write(document.Path, document.Document.Text, document.Encoding);
        var notice = used.CodePage != document.Encoding.CodePage
            ? $"{System.IO.Path.GetFileName(document.Path)} contained characters {document.Encoding.WebName} can't represent, so it was saved as UTF-8."
            : null;
        document.Encoding = used;
        // The just-written text is now the on-disk baseline; without this, IsModified would keep
        // reporting true even immediately after a successful save.
        document.Document.UndoStack.MarkAsOriginalFile();
        RefreshTabs();
        return notice;
    }

    /// <summary>Tab titles: the file name, or "folder/name" where two open files share a name.</summary>
    private void RefreshTabs()
    {
        var names = _documents.Select(d => System.IO.Path.GetFileName(d.Path)).ToList();
        var tabs = _documents.Select((d, i) =>
        {
            var title = names.Count(n => string.Equals(n, names[i], StringComparison.OrdinalIgnoreCase)) > 1
                ? $"{System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(d.Path))}/{names[i]}"
                : names[i];
            return new DocumentTabStrip.Tab(title, d.IsModified);
        }).ToList();
        _tabStrip.SetTabs(tabs, _active is null ? -1 : _documents.IndexOf(_active));
    }
}
