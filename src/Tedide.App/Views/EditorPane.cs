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
/// Hosts a single, shared <see cref="Editor"/> showing at most one open file at a time - this
/// app deliberately does not support multiple simultaneously open documents/tabs. Selecting a
/// different file (e.g. via the Solution Explorer) replaces whatever's currently open; callers
/// that care about unsaved changes should check <see cref="IsModified"/> and save/confirm with
/// the user before calling <see cref="Open"/> or <see cref="Close"/>, since neither prompts.
///
/// Document/caret handling here mirrors Terminal.Gui.Editor's own reference app (tui-cs/Editor's
/// "ted") as closely as our project-based (rather than single-file) app allows: a fresh
/// TextDocument per load (undo stack starts clean), ClearSelection() before swapping documents,
/// CaretOffset reset to 0, and MarkAsOriginalFile() only after an explicit save - never touching
/// Cursor/CursorStyle, since ted doesn't either and relies entirely on the library's own defaults.
/// </summary>
public sealed class EditorPane : View
{
    /// <summary>The single shared Editor instance, exposed so the host can wire up an
    /// <see cref="Terminal.Gui.Editor.EditorMenuBar"/>/<see cref="Terminal.Gui.Editor.EditorStatusBar"/>
    /// against it, the same way ted wires its own menu/status bar to its one Editor.</summary>
    public Editor Editor { get; }

    public string? OpenPath { get; private set; }

    /// <summary>Raised when "Find in Files..." is chosen from the editor's right-click context
    /// menu, carrying the current selection (empty if there is none) to pre-populate the Find in
    /// Files dialog's search field with. Just the selection's first line - the search field it
    /// feeds is single-line, and a multi-line selection is rarely a meaningful search term anyway.</summary>
    public event Action<string>? FindInFilesRequested;

    public EditorPane()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
        // A View's SuperView chain must all have CanFocus = true for SetFocus() to reach a
        // descendant (see Terminal.Gui's View.CanFocus docs) - without this, Editor.SetFocus()
        // in Open() below silently fails since this plain View defaults to CanFocus = false.
        CanFocus = true;

        Editor = new Editor
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            GutterOptions = GutterOptions.LineNumbers | GutterOptions.Folding,
            ReadOnly = true, // no file open yet
            Document = new TextDocument(string.Empty),
            // Assigning a strategy is what actually makes the Folding gutter above do anything -
            // ted's own TedApp.cs does the same. BraceFoldingStrategy is language-agnostic (any
            // {...} spanning multiple lines folds), which covers our C sources; ca65 assembly has
            // no brace blocks to fold, so .s/.asm/.inc files just show no fold points, same as
            // before. Assigning this also auto-enables Editor.AutomaticFolding (see
            // Editor.FoldingStrategy's setter), and the Document setter re-runs it on every Open()
            // below, so foldings stay current as files are opened/edited without any extra wiring.
            FoldingStrategy = new BraceFoldingStrategy(),
            // HasScrollBars uses ScrollBarVisibilityMode.Auto - scrollbars only appear once the
            // document overflows the viewport, rather than being permanently shown.
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };

        // Editor.FindRequested/ReplaceRequested fire whenever Command.Find/Command.Replace runs -
        // that's both Ctrl+F/Ctrl+H (EditorKeyBindingDefaults) and the library's own auto-generated
        // Edit menu (EditorMenuBar.cs calls ActiveEditor.InvokeCommand(Command.Find/Replace)) - but
        // per the library's own doc comment on FindRequested, it's the consumer's job to subscribe
        // and actually open a dialog; nothing here was doing that, so Find/Replace has been a
        // silent no-op everywhere in Tedide (Edit menu included) until now. FindReplaceDialog(Editor,
        // bool) is the exact call ted's own ShowFindReplaceDialog (TedApp.EditCommands.cs) makes in
        // its equivalent subscription - same dialog, same construction.
        Editor.FindRequested += (_, _) => Application.Run(new FindReplaceDialog(Editor, false));
        Editor.ReplaceRequested += (_, _) => Application.Run(new FindReplaceDialog(Editor, true));

        // The editor's own right-click context menu already covers Undo/Redo/Cut/Copy/Paste/
        // Select All (Terminal.Gui.Editor's built-in default) but not Find/Replace, unlike the
        // library's auto-generated Edit menu (see AppShell's editMenuItems comment) - append the
        // same search group (Find, Replace, then our own Find in Files) to the end of this menu,
        // the same way EditMenu.PopoverMenu.Root.Add is used in AppShell to extend the Edit menu,
        // rather than building a second, separate context menu. Invoking the same Command.Find/
        // Command.Replace the Edit menu uses (rather than constructing FindReplaceDialog here too)
        // keeps exactly one place responsible for how that dialog gets shown.
        var contextMenuItems = Editor.ContextMenu!.Root!;
        contextMenuItems.Add(new Line());
        contextMenuItems.Add(new MenuItem("Find...", "", () => Editor.InvokeCommand(Command.Find)));
        contextMenuItems.Add(new MenuItem("Replace...", "", () => Editor.InvokeCommand(Command.Replace)));
        contextMenuItems.Add(new MenuItem("Find in Files...", "", () =>
            FindInFilesRequested?.Invoke(Editor.SelectedText.Split(['\r', '\n'], 2)[0])));

        Add(Editor);
    }

    /// <summary>
    /// Opens the given file, discarding whatever was previously open. Does nothing if it's
    /// already the open file. No save prompt - see the class summary.
    /// </summary>
    public void Open(string filePath)
    {
        if (OpenPath is not null && string.Equals(OpenPath, filePath, StringComparison.OrdinalIgnoreCase))
            return;

        // ted's own SetDocument() clears any selection before swapping in the new document.
        Editor.ClearSelection();
        // A fresh TextDocument (rather than mutating the editor's existing one) is the pattern
        // ted's own SetDocument() uses to load a file - its undo stack starts clean, unlike
        // setting .Text on an existing document.
        Editor.Document = new TextDocument(File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty);
        Editor.HighlightingDefinition = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(filePath));
        Editor.CaretOffset = 0;
        Editor.ReadOnly = false;
        OpenPath = filePath;

        // ted itself never calls SetFocus() on its Editor explicitly - it doesn't need to, since
        // its only way to load a file is a modal Open dialog, and closing a modal naturally
        // restores focus to whatever was focused before it opened. Our Solution Explorer is a
        // persistent sidebar, not a modal dialog, so selecting a file there doesn't restore focus
        // to the editor on its own - this call is the necessary adaptation for that difference.
        Editor.SetFocus();
    }

    /// <summary>Closes the currently open file, if any. No save prompt - see the class summary.</summary>
    public void Close()
    {
        if (OpenPath is null)
            return;

        Editor.ClearSelection();
        Editor.Document = new TextDocument(string.Empty);
        Editor.ReadOnly = true;
        OpenPath = null;
    }

    public bool IsModified => OpenPath is not null && !Editor.Document!.UndoStack.IsOriginalFile;

    public void Save()
    {
        if (OpenPath is null)
            return;

        File.WriteAllText(OpenPath, Editor.Text);
        // The just-written text is now the on-disk baseline; without this, IsModified would keep
        // reporting true even immediately after a successful save.
        Editor.Document!.UndoStack.MarkAsOriginalFile();
    }
}
