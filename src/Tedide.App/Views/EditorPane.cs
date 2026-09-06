using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Highlighting;
using Terminal.Gui.ViewBase;

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
            GutterOptions = GutterOptions.LineNumbers,
            ReadOnly = true, // no file open yet
            Document = new TextDocument(string.Empty),
            // HasScrollBars uses ScrollBarVisibilityMode.Auto - scrollbars only appear once the
            // document overflows the viewport, rather than being permanently shown.
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };

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
