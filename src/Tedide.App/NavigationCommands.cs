using Serilog;
using Tedide.App.Views;
using Tedide.Core;
using Tedide.Core.Navigation;
using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App;

/// <summary>
/// Finding and going places in the code: Find in Files, Go To Line, Go To Definition, Find All
/// References, Rename Symbol, Navigate Backward/Forward, F1 context help, and opening a file at a
/// line for the Error List, Symbols and References tabs and the debugger. Calls back into the
/// shell through <see cref="IShell"/>.
/// </summary>
internal sealed class NavigationCommands(IShell shell, Workspace workspace, EditorPane editorPane,
    NavigationHistory navigationHistory, ReferencesView referencesView)
{
    private readonly IShell _shell = shell;
    private readonly Workspace _workspace = workspace;
    private readonly EditorPane _editorPane = editorPane;
    private readonly NavigationHistory _navigationHistory = navigationHistory;
    private readonly ReferencesView _referencesView = referencesView;

    /// <summary>
    /// Opens the Find in Files dialog, searching every loaded project's directory tree. If the
    /// user activates a result, opens its file in a tab (or switches to it) and moves the caret to
    /// the matched line.
    /// </summary>
    /// <param name="initialSearchText">Pre-populates (and immediately searches for) this text -
    /// e.g. the editor's current selection, via <see cref="EditorPane.FindInFilesRequested"/>.
    /// Empty for the Edit menu/Ctrl+Shift+F path, which starts with a blank search field.</param>
    public void ShowFindInFiles(string initialSearchText = "")
    {
        if (_workspace.Projects.Count == 0)
        {
            _shell.AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        var dialog = new FindInFilesDialog(_workspace, initialSearchText);
        _shell.Dialogs.Run(dialog);
        if (dialog.SelectedMatch is { } match)
            OpenMatch(match);
    }

    /// <summary>
    /// Prompts for a line number (pre-filled with the caret's current line) and moves the caret
    /// to the start of that line, scrolling it into view - same CaretOffset-assignment mechanism
    /// as <see cref="OpenMatch"/>, just without a column.
    /// </summary>
    public void ShowGoToLine()
    {
        var document = _editorPane.Editor.Document;
        if (_editorPane.OpenPath is null || document is null)
            return;

        var currentLineNumber = document.GetLineByOffset(_editorPane.Editor.CaretOffset).LineNumber;
        var dialog = new GoToLineDialog(currentLineNumber, document.LineCount);
        _shell.Dialogs.Run(dialog);
        if (dialog.LineNumber is { } lineNumber)
        {
            RecordJump();
            _editorPane.Editor.CaretOffset = document.GetLineByNumber(lineNumber).Offset;
            _editorPane.Editor.SetFocus();
        }
    }

    /// <summary>
    /// Opens a Find in Files match's file (unless it's already the open file) and moves the
    /// caret to the start of the matched line/column, scrolling it into view.
    /// </summary>
    private void OpenMatch(FindInFilesDialog.Match match) => NavigateTo(match.FilePath, match.LineNumber, match.ColumnNumber);

    /// <summary>
    /// Opens <paramref name="filePath"/> (unless it's already the open file - prompting to save the
    /// current one first, same as <see cref="IShell.OpenFile"/>) and moves the caret to the 1-based line and
    /// column, scrolling it into view. Shared by Find in Files, Go To Definition and the References tab.
    /// </summary>
    /// <param name="highlightLength">When non-zero, that many characters from the column are
    /// selected, so the symbol a reference or definition points at stands out - the same
    /// SelectRange highlighting <see cref="OpenDiagnostic"/> gives a whole line.</param>
    public void NavigateTo(string filePath, int lineNumber, int columnNumber, int highlightLength = 0, bool recordJump = true)
    {
        if (recordJump)
            RecordJump();
        if (!_editorPane.IsShown(filePath))
        {
            _shell.OpenFile(filePath);
            if (!_editorPane.IsShown(filePath))
                return; // User cancelled replacing the currently open (modified) file.
        }

        var document = _editorPane.Editor.Document;
        if (document is null || lineNumber < 1 || lineNumber > document.LineCount)
            return;

        var line = document.GetLineByNumber(lineNumber);
        var column = Math.Clamp(columnNumber - 1, 0, line.Length);
        // Clamped to the line, in case the file has changed since the references were found.
        var length = Math.Min(highlightLength, line.Length - column);
        if (length > 0)
            _editorPane.Editor.SelectRange(line.Offset + column, length);
        else
            _editorPane.Editor.CaretOffset = line.Offset + column;
        _editorPane.Editor.SetFocus();
    }

    /// <summary>
    /// A <see cref="CodeNavigator"/> over every loaded project's sources, reading the open file's
    /// unsaved text from the editor so positions match the screen. cc65's own headers are searched
    /// too, for a definition the project doesn't have, and the active project's target macros and
    /// -D defines decide which <c>#if</c> branches count.
    /// </summary>
    private CodeNavigator CreateNavigator()
    {
        var files = _workspace.Projects
            .SelectMany(SolutionExplorerTree.EnumerateProjectFiles)
            .Where(f => SourceTokenizer.LanguageOf(f) != SourceLanguage.Other)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var includeDirectories = _workspace.Projects
            .SelectMany(p => p.IncludePaths.Select(i => Path.GetFullPath(Path.Combine(p.Directory, i))))
            .ToList();
        // Every open tab's current text, unsaved edits included, so positions match the editor.
        var openTexts = _editorPane.OpenPaths.ToDictionary(p => p, p => _editorPane.TextOf(p)!, StringComparer.OrdinalIgnoreCase);
        // The macros of the project the shown file belongs to - its target may not be the startup project's.
        var macroProject = (_editorPane.OpenPath is { } shown ? _workspace.ProjectFor(shown) : null) ?? _workspace.ActiveProject;
        IEnumerable<string> macros = macroProject is { } project
            ? project.Target.PredefinedMacros().Concat(project.PreprocessorDefines.Select(d => d.Split('=', 2)[0].Trim()))
            : [];

        return new CodeNavigator(
            files,
            path => openTexts.TryGetValue(path, out var text) ? text : CodeNavigator.ReadFromDisk(path),
            includeDirectories,
            CodeNavigator.Cc65LibraryDirectories(Environment.GetEnvironmentVariable("CC65_HOME"), Environment.GetEnvironmentVariable("PATH")),
            macros);
    }

    /// <summary>A path relative to the loaded project containing it, or as-is if it's outside them
    /// all (e.g. one of cc65's own headers).</summary>
    public string DisplayPath(string path)
    {
        foreach (var project in _workspace.Projects)
        {
            var relative = Path.GetRelativePath(project.Directory, path);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return relative;
        }
        return path;
    }

    /// <summary>
    /// Edit > Go To Definition (F12): jumps to where the symbol at the caret is defined - straight
    /// there when there's one candidate, via <see cref="DefinitionPickerDialog"/> when there are
    /// several. On a definition already, it goes to the declaration (e.g. the header's prototype),
    /// and on an #include line it opens that file.
    /// </summary>
    public void GoToDefinition() => _shell.Guard("Going to the definition", GoToDefinitionCore);

    private void GoToDefinitionCore()
    {
        if (_editorPane.OpenPath is not { } path)
            return;

        var (line, column) = _editorPane.CaretPosition;
        var result = CreateNavigator().GoToDefinition(path, line, column);
        if (result.Definitions.Count == 0)
        {
            ReportNavigation(result.Message ?? "No definition found.");
            return;
        }

        var target = result.Definitions[0];
        if (result.Definitions.Count > 1)
        {
            var dialog = new DefinitionPickerDialog(result.Symbol ?? string.Empty, result.Definitions, SourceLineOf, DisplayPath);
            _shell.Dialogs.Run(dialog);
            if (dialog.SelectedDefinition is not { } chosen)
                return;
            target = chosen;
        }
        else if (result.Message is { } note)
        {
            ReportNavigation(note);
        }

        NavigateTo(target.FilePath, target.Line, target.Column, target.Kind == SymbolKind.File ? 0 : target.Name.Length);
    }

    /// <summary>Remembers where the caret is, before a jump moves it, for Navigate Backward. Called
    /// by every jump: Go To Definition, the References and Error List tabs, Find in Files, Go To
    /// Line and the Symbols tab - not by the debugger following execution.</summary>
    public void RecordJump()
    {
        if (_editorPane.OpenPath is { } path)
        {
            var (line, column) = _editorPane.CaretPosition;
            _navigationHistory.RecordJump(new CaretLocation(path, line, column));
        }
    }

    private CaretLocation? CurrentCaretLocation()
    {
        if (_editorPane.OpenPath is not { } path)
            return null;
        var (line, column) = _editorPane.CaretPosition;
        return new CaretLocation(path, line, column);
    }

    /// <summary>Edit > Navigate Backward (Alt+Left): back to where the caret was before the last jump.</summary>
    public void NavigateBackward() => _shell.Guard("Navigating backward", () =>
    {
        if (_navigationHistory.GoBack(CurrentCaretLocation()) is { } target)
            GoToHistoryLocation(target);
        else
            ReportNavigation("Nothing to navigate back to.");
    });

    /// <summary>Edit > Navigate Forward (Alt+Right): undoes a Navigate Backward.</summary>
    public void NavigateForward() => _shell.Guard("Navigating forward", () =>
    {
        if (_navigationHistory.GoForward(CurrentCaretLocation()) is { } target)
            GoToHistoryLocation(target);
        else
            ReportNavigation("Nothing to navigate forward to.");
    });

    private void GoToHistoryLocation(CaretLocation target)
    {
        if (!File.Exists(target.FilePath))
        {
            _navigationHistory.RemoveFile(target.FilePath);
            ReportNavigation($"{Path.GetFileName(target.FilePath)} no longer exists.");
            return;
        }
        NavigateTo(target.FilePath, target.Line, target.Column, recordJump: false);
    }

    /// <summary>
    /// Help > Context Help (F1): opens the Doc Viewer at the word under the caret - the section
    /// whose heading names it, or a search for it - or at its first page if there's no word.
    /// </summary>
    public void ShowContextHelp() => _shell.Guard("Opening the Doc Viewer", () =>
    {
        string? topic = null;
        if (_editorPane.OpenPath is not null && _editorPane.Editor.Document is { } document)
        {
            var (line, column) = _editorPane.CaretPosition;
            var lineText = document.GetText(document.GetLineByNumber(line));
            topic = ContextHelp.WordAt(lineText, column);
        }

        var candidates = ContextHelp.CandidatePaths(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(ContextHelp.DocViewerPathVariable));
        if (ContextHelp.FindDocViewer(candidates) is not { } docViewer)
        {
            _shell.Dialogs.ErrorQuery("Doc Viewer not found",
                $"{ContextHelp.DocViewerFileName} wasn't found. Looked in:\n\n{string.Join('\n', candidates)}\n\n"
                + $"Publish it beside Tedide, or set {ContextHelp.DocViewerPathVariable} to its full path.", ["OK"]);
            return;
        }

        var insideWindowsTerminal = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"));
        using var _ = System.Diagnostics.Process.Start(ContextHelp.CreateStartInfo(docViewer, topic, insideWindowsTerminal));
        _shell.AppendOutputLine(topic is null ? "Opened the Doc Viewer." : $"Opened the Doc Viewer at '{topic}'.");
    });

    /// <summary>The source line a definition sits on, for <see cref="DefinitionPickerDialog"/>'s list.</summary>
    private static string SourceLineOf(SymbolDefinition definition)
    {
        var lines = (CodeNavigator.ReadFromDisk(definition.FilePath) ?? string.Empty).Split('\n');
        return definition.Line - 1 < lines.Length ? lines[definition.Line - 1].TrimEnd('\r') : string.Empty;
    }

    /// <summary>
    /// Edit > Find All References (Shift+F12): lists every use of the symbol at the caret across the
    /// loaded projects in the References tab - C and assembly alike, skipping comments, strings and
    /// same-named locals - with its definition rows highlighted.
    /// </summary>
    public void FindAllReferences() => _shell.Guard("Finding references", FindAllReferencesCore);

    private void FindAllReferencesCore()
    {
        if (_editorPane.OpenPath is not { } path)
            return;

        var (line, column) = _editorPane.CaretPosition;
        var result = CreateNavigator().FindReferences(path, line, column);
        if (result.Symbol is null)
        {
            ReportNavigation(result.Message ?? "No symbol at the cursor.");
            return;
        }

        _referencesView.SetReferences(result.References, DisplayPath);
        var fileCount = result.References.Select(r => r.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        _shell.AppendOutputLine($"Find All References: '{result.Symbol}' - {result.References.Count} reference(s) in {fileCount} file(s).");
        _shell.ShowPane(_referencesView);
    }

    /// <summary>
    /// Edit > Rename Symbol (F2): renames the symbol at the caret everywhere Find All References
    /// finds it, after <see cref="RenameSymbolDialog"/> has checked the new name. Each file open in
    /// a tab is changed in the editor as one undoable step and left unsaved; every other file is
    /// rewritten on disk in its own encoding. Everything is worked out and checked before anything
    /// is changed, so a file that's changed underneath stops the rename before anything is touched.
    /// </summary>
    public void RenameSymbol() => _shell.Guard("Renaming the symbol", RenameSymbolCore);

    private void RenameSymbolCore()
    {
        if (_editorPane.OpenPath is not { } path)
            return;

        var (line, column) = _editorPane.CaretPosition;
        var navigator = CreateNavigator();
        var references = navigator.FindReferences(path, line, column);
        if (references.Symbol is not { } symbol)
        {
            ReportNavigation(references.Message ?? "No symbol at the cursor.");
            return;
        }

        // Refused up front when no new name could help, rather than after one has been typed.
        if (navigator.WhyNotRenamable(path, line, column) is { } blocker)
        {
            _shell.Dialogs.ErrorQuery("Can't Rename", RenameSymbolDialog.Wrap(blocker), ["OK"]);
            return;
        }

        var fileCount = references.References.Select(r => r.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var dialog = new RenameSymbolDialog(symbol, references.References.Count, fileCount, name => navigator.PlanRename(path, line, column, name));
        _shell.Dialogs.Run(dialog);
        if (dialog.Plan is not { } plan)
            return;

        var byFile = plan.Edits.GroupBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        var openFiles = byFile.Where(g => _editorPane.IsOpen(g.Key)).ToList();
        // Files that aren't open are worked out in full before any is written.
        var closedFiles = byFile
            .Where(g => !_editorPane.IsOpen(g.Key))
            .Select(group =>
            {
                var (text, encoding) = SourceFileText.Read(group.Key);
                return (Path: group.Key, Text: RenamePlan.Apply(text, group), Encoding: encoding);
            })
            .ToList();
        // Likewise every open file is checked before any is changed.
        var located = openFiles.Select(g => (Path: g.Key, Edits: LocateEdits(_editorPane.DocumentOf(g.Key)!, g.Key, g.ToList()))).ToList();

        foreach (var file in closedFiles)
            SourceFileText.Write(file.Path, file.Text, file.Encoding);
        foreach (var (openPath, edits) in located)
            ApplyEditsToOpenFile(openPath, edits);

        // Every listed position may have moved.
        _referencesView.SetReferences([], DisplayPath);
        var newName = plan.Edits.FirstOrDefault(e => e.FilePath == path)?.NewText ?? plan.Edits[0].NewText;
        _shell.AppendOutputLine($"Renamed '{symbol}' to '{newName}': {plan.Edits.Count} change(s) in {plan.FileCount} file(s)"
            + (located.Count > 0 ? $" - {located.Count} open file(s) changed in the editor and not yet saved." : "."));
    }

    /// <summary>A rename's edits for one open file as document offsets, last first, after checking
    /// each spot still holds the old name (it's the same text the plan was made from, so it should).</summary>
    private static List<(TextEdit Edit, int Offset)> LocateEdits(TextDocument document, string path, IReadOnlyList<TextEdit> edits)
    {
        var located = edits
            .Select(e => (Edit: e, Offset: document.GetLineByNumber(e.Line).Offset + e.Column - 1))
            .OrderByDescending(x => x.Offset)
            .ToList();
        if (located.Any(x => document.GetText(x.Offset, x.Edit.OldText.Length) != x.Edit.OldText))
            throw new InvalidDataException($"{Path.GetFileName(path)} changed while the rename was being worked out.");
        return located;
    }

    /// <summary>Applies a rename's located edits to an open file's document as a single undo step,
    /// keeping the caret on the same code when it's the file being shown.</summary>
    private void ApplyEditsToOpenFile(string path, List<(TextEdit Edit, int Offset)> located)
    {
        var document = _editorPane.DocumentOf(path)!;
        var isShown = _editorPane.IsShown(path);
        var editor = _editorPane.Editor;
        var caret = isShown ? editor.CaretOffset : 0;
        if (isShown)
            editor.ClearSelection();
        document.UndoStack.StartUndoGroup();
        try
        {
            foreach (var (edit, offset) in located)
            {
                document.Replace(offset, edit.OldText.Length, edit.NewText);
                // Keep the caret on the same code: shifted by renames before it, and at the start
                // of the renamed word if it was inside one.
                if (offset + edit.OldText.Length <= caret)
                    caret += edit.NewText.Length - edit.OldText.Length;
                else if (offset < caret)
                    caret = offset;
            }
        }
        finally
        {
            document.UndoStack.EndUndoGroup();
        }
        if (isShown)
        {
            editor.CaretOffset = Math.Clamp(caret, 0, document.TextLength);
            editor.SetFocus();
        }
    }

    /// <summary>Go To Definition/Find All References feedback ("No symbol at the cursor.", ...),
    /// written to the Output tab, which is brought forward so the message isn't missed.</summary>
    private void ReportNavigation(string message)
    {
        _shell.AppendOutputLine(message);
        _shell.BringOutputForward();
    }

    /// <summary>
    /// Opens a build diagnostic's file (unless it's already the open file) and highlights its
    /// line by selecting the whole line's text, scrolling it into view - the diagnostic has no
    /// column, unlike a Find in Files <see cref="FindInFilesDialog.Match"/>, so there's nothing
    /// more specific to place the caret at.
    /// </summary>
    public void OpenDiagnostic(BuildDiagnostic diagnostic)
    {
        var project = _workspace.ActiveProject;
        if (project is null)
            return;

        var filePath = Path.IsPathRooted(diagnostic.FilePath)
            ? diagnostic.FilePath
            : Path.Combine(project.Directory, diagnostic.FilePath);
        if (!File.Exists(filePath))
            return;

        RecordJump();
        if (!_editorPane.IsShown(filePath))
        {
            _shell.OpenFile(filePath);
            if (!_editorPane.IsShown(filePath))
                return; // User cancelled replacing the currently open (modified) file.
        }

        var document = _editorPane.Editor.Document;
        if (document is null || diagnostic.Line < 1 || diagnostic.Line > document.LineCount)
            return;

        var line = document.GetLineByNumber(diagnostic.Line);
        // SelectRange's second argument is a length, not an end offset - passing EndOffset here
        // (Offset + Length) previously selected roughly twice as far as intended, spilling into
        // one or more following lines instead of highlighting just this one.
        _editorPane.Editor.SelectRange(line.Offset, line.Length);
        _editorPane.Editor.SetFocus();
    }

    /// <summary>
    /// Opens a symbol panel entry's source file (lnk.map or .lbl - both plain text, unaffected by
    /// this being a structured panel over them, see <see cref="SymbolPanelView"/>) and moves the
    /// caret to its line, the same way <see cref="OpenMatch"/> does for a Find in Files result.
    /// </summary>
    public void OpenSymbol((string FilePath, int LineNumber) entry)
    {
        if (!File.Exists(entry.FilePath))
            return;

        if (!_editorPane.IsShown(entry.FilePath))
        {
            _shell.OpenFile(entry.FilePath);
            if (!_editorPane.IsShown(entry.FilePath))
                return; // User cancelled replacing the currently open (modified) file.
        }

        var document = _editorPane.Editor.Document;
        if (document is null || entry.LineNumber < 1 || entry.LineNumber > document.LineCount)
            return;

        var line = document.GetLineByNumber(entry.LineNumber);
        _editorPane.Editor.CaretOffset = line.Offset;
        _editorPane.Editor.SetFocus();
    }

    /// <summary>
    /// Scrolls the editor's viewport so the given 1-based line sits vertically centered, rather
    /// than just barely visible (the default behavior of CaretOffset's own EnsureCaretVisible,
    /// which merely clamps to the nearest edge) - used only for the currently-executing line
    /// during a debug session (checkpoint hit, step, or the initial jump to main()), so nearby
    /// code above and below stays visible without the user needing to scroll manually. Not used
    /// for other navigation (Find in Files, Go To Line, Symbol panel) - minimal-scroll there is
    /// the expected, less disruptive behavior.
    /// </summary>
    /// <remarks>
    /// Ignores folding: the fold-aware visible-row mapping (<c>Editor.GetVisibleLineNumbers</c>)
    /// is internal to Terminal.Gui.Editor, not accessible from here - centering by raw line number
    /// is a reasonable simplification since debug sessions don't typically have folds active.
    /// </remarks>
    /// <param name="filePath">The absolute path <see cref="OpenSymbol"/> was just asked to show -
    /// checked against <see cref="EditorPane.OpenPath"/> before centering, since OpenSymbol is a
    /// no-op when that file doesn't exist locally (e.g. stepping into cc65's own runtime library,
    /// whose original build-machine source path isn't present on disk) - without this check,
    /// centering would jump whatever file *is* still open to this unrelated line number instead.</param>
    public void CenterEditorOnLine(string filePath, int lineNumber)
    {
        if (!_editorPane.IsShown(filePath))
            return;

        var editor = _editorPane.Editor;
        if (editor.Document is not { } document)
            return;

        var viewport = editor.Viewport;
        if (viewport.Height <= 0)
            return;

        var maxY = Math.Max(0, document.LineCount - viewport.Height);
        var targetY = Math.Clamp(lineNumber - 1 - viewport.Height / 2, 0, maxY);
        editor.Viewport = viewport with { Y = targetY };
    }
}
