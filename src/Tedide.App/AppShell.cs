using Tedide.App.Theming;
using Tedide.App.Views;
using Tedide.Build;
using Tedide.Core;
using Terminal.Gui.App;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App;

/// <summary>
/// The main window of the Tedide IDE: menu bar, solution explorer, a single-file editor pane,
/// build output pane and status bar. Only one file can be open at a time - selecting another one
/// in the Solution Explorer replaces it (prompting to save first if the current one is modified).
///
/// The menu and status bars are Terminal.Gui.Editor's own <see cref="EditorMenuBar"/> and
/// <see cref="EditorStatusBar"/> - the same components its reference app "ted" uses - rather than
/// hand-rolled equivalents. Their auto-generated Edit/View menus (Find/Replace/Undo/Redo/Cut/Copy/
/// Paste/Select All, Line Numbers/Fold Indicators/Word Wrap/Show Tabs/Scrollbars) and the status
/// bar's row/column indicator come wired to the editor already; the default File menu is replaced
/// with our own project-aware one (New/Open Project instead of a single-file New/Open).
/// </summary>
public sealed class AppShell : Window
{
    private readonly Workspace _workspace = new();
    private readonly Cc65Toolchain _toolchain = new();
    private readonly ViceEmulator _vice = new();
    private readonly RecentProjectsSettings _recentProjects = RecentProjectsSettings.Load();

    /// <summary>The File menu's "Recent Projects and Solutions" item - kept as a field so its
    /// SubMenu can be rebuilt in place whenever <see cref="_recentProjects"/> changes.</summary>
    private MenuItem _recentProjectsMenuItem = null!;

    private readonly SolutionExplorerTree _solutionExplorer = new();
    private readonly EditorPane _editorPane = new();
    private readonly FrameView _editorFrame;
    private readonly TextView _outputView = new()
    {
        ReadOnly = true,
        // Auto-shown (only appears once output overflows the viewport) - same as EditorPane's editor.
        ViewportSettings = ViewportSettingsFlags.HasScrollBars,
    };
    private readonly ErrorListView _errorListView = new();
    private readonly EditorMenuBar _menuBar;
    private readonly EditorStatusBar _statusBar;

    public AppShell()
    {
        Title = "Tedide - CC65 IDE";
        Width = Dim.Fill();
        Height = Dim.Fill();

        _menuBar = BuildMenuBar();
        _statusBar = BuildStatusBar();

        var explorerFrame = new FrameView
        {
            Title = "Solution Explorer",
            X = 0,
            Y = Pos.Bottom(_menuBar),
            // Fills whatever _editorFrame's current width (initially 75%, then whatever the user
            // drags it to below) doesn't use, so the two panes always exactly share the row
            // between them, with _editorFrame's own left border acting as the draggable divider.
            Width = Dim.Fill(Dim.Func(_ => _editorFrame!.Frame.Width)),
            Height = Dim.Percent(70),
        };
        _solutionExplorer.Width = Dim.Fill();
        _solutionExplorer.Height = Dim.Fill();
        _solutionExplorer.FileActivated += OpenFile;
        _solutionExplorer.NewFileRequested += NewFile;
        _solutionExplorer.DeleteFileRequested += DeleteFile;
        explorerFrame.Add(_solutionExplorer);

        _editorFrame = new FrameView
        {
            Title = NoFileOpenTitle,
            X = Pos.Right(explorerFrame),
            Y = Pos.Bottom(_menuBar),
            Width = Dim.Percent(75),
            Height = Dim.Percent(70),
            // Makes this frame's left border a draggable splitter between it and the Solution
            // Explorer - explorerFrame's Width (above) tracks this frame's Frame.Width live, so
            // dragging the border resizes both panes together. CanFocus is required for the
            // border-drag mouse interaction to register.
            Arrangement = ViewArrangement.LeftResizable,
            CanFocus = true,
        };
        _editorFrame.Add(_editorPane);

        var outputTabs = new Tabs
        {
            X = 0,
            Y = Pos.Bottom(_editorFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        var outputTab = new View { Title = "_Output", Width = Dim.Fill(), Height = Dim.Fill() };
        _outputView.Width = Dim.Fill();
        _outputView.Height = Dim.Fill();
        outputTab.Add(_outputView);

        var errorListTab = new View { Title = "_Error List", Width = Dim.Fill(), Height = Dim.Fill() };
        _errorListView.Width = Dim.Fill();
        _errorListView.Height = Dim.Fill();
        _errorListView.DiagnosticActivated += OpenDiagnostic;
        errorListTab.Add(_errorListView);

        outputTabs.Add(outputTab);
        outputTabs.Add(errorListTab);

        Add([_menuBar, explorerFrame, _editorFrame, outputTabs, _statusBar]);
    }

    private const string NoFileOpenTitle = "(no file open)";

    private EditorMenuBar BuildMenuBar()
    {
        var menuBar = new EditorMenuBar(_editorPane.Editor);

        _recentProjectsMenuItem = new MenuItem("_Recent Projects and Solutions", "", BuildRecentProjectsMenu());
        var fileMenu = new MenuBarItem("_File", new List<MenuItem>
        {
            new("_New Project...", "", NewProject, Key.N.WithCtrl),
            new("_Open Project...", "", OpenProject, Key.O.WithCtrl),
            _recentProjectsMenuItem,
            new("Close Sol_ution", "", CloseSolution, Key.Empty),
            new("_Save", "", SaveAll, Key.S.WithCtrl),
            new("_Close File", "", CloseActiveFile, Key.W.WithCtrl),
            new("_Quit", "", () => Application.RequestStop(this), Key.Q.WithCtrl),
        });

        var buildMenu = new MenuBarItem("_Build", new List<MenuItem>
        {
            new("_Build Project", "", () => _ = BuildActiveProjectAsync(), Key.F5),
            new("_Clean Project", "", CleanActiveProject, Key.Empty),
            new("_Run Project", "", () => _ = RunActiveProjectAsync(), Key.F6),
        });

        var projectMenu = new MenuBarItem("_Project", new List<MenuItem>
        {
            new("_Settings...", "", ShowProjectSettings, Key.Empty),
        });

        var searchMenu = new MenuBarItem("_Search", new List<MenuItem>
        {
            new("_Find in Files...", "", ShowFindInFiles, Key.F.WithCtrl.WithShift),
        });

        var themeMenu = new MenuBarItem("_Theme", new List<MenuItem>
        {
            new("VS2026 _Dark", "", () => ThemeSwitcher.Apply(AppTheme.Vs2026Dark), Key.Empty),
            new("VS2026 _Light", "", () => ThemeSwitcher.Apply(AppTheme.Vs2026Light), Key.Empty),
            new("_Borland Turbo C", "", () => ThemeSwitcher.Apply(AppTheme.BorlandTurboC), Key.Empty),
            new("_Monokai", "", () => ThemeSwitcher.Apply(AppTheme.Monokai), Key.Empty),
            new("_Dracula", "", () => ThemeSwitcher.Apply(AppTheme.Dracula), Key.Empty),
            new("Solarized D_ark", "", () => ThemeSwitcher.Apply(AppTheme.SolarizedDark), Key.Empty),
            new("Solarized Li_ght", "", () => ThemeSwitcher.Apply(AppTheme.SolarizedLight), Key.Empty),
            new("_Commodore 64", "", () => ThemeSwitcher.Apply(AppTheme.Commodore64), Key.Empty),
            new("_Amber Phosphor", "", () => ThemeSwitcher.Apply(AppTheme.AmberPhosphor), Key.Empty),
        });

        // Replace the library's default single-file File menu (New/Open/Save/Save As/Quit) with
        // our own project-aware one, but keep its auto-generated EditMenu/ViewMenu - already wired
        // directly to the editor (Find/Replace/Undo/Redo/Cut/Copy/Paste/Select All; Line Numbers/
        // Fold Indicators/Word Wrap/Show Tabs/Scrollbars) - for free.
        menuBar.Menus = [fileMenu, menuBar.EditMenu, menuBar.ViewMenu, buildMenu, projectMenu, searchMenu, themeMenu];
        menuBar.X = 0;
        menuBar.Y = 0;
        menuBar.Width = Dim.Fill();
        return menuBar;
    }

    private EditorStatusBar BuildStatusBar()
    {
        var statusBar = new EditorStatusBar(_editorPane.Editor);
        // ThemeDropDown drives Terminal.Gui's own ConfigurationManager-based ThemeManager, separate
        // from our own SchemeManager-based ThemeSwitcher (see Theming/ThemeSwitcher.cs) - leaving
        // both active would let this dropdown silently overwrite our custom Schemes. Hide it; the
        // Theme menu above is our one theme switcher.
        statusBar.ThemeDropDown.Visible = false;
        statusBar.Add(new Shortcut(Key.F5, "~F5~ Build", () => _ = BuildActiveProjectAsync()));
        statusBar.Add(new Shortcut(Key.Q.WithCtrl, "~^Q~ Quit", () => Application.RequestStop(this)));
        statusBar.X = 0;
        statusBar.Y = Pos.AnchorEnd(1);
        statusBar.Width = Dim.Fill();
        return statusBar;
    }

    private void NewProject()
    {
        var dialog = new NewProjectDialog();
        Application.Run(dialog);
        if (dialog.Target is { } target && !string.IsNullOrWhiteSpace(dialog.ProjectName))
        {
            var project = _workspace.NewProject(dialog.Directory, dialog.ProjectName, target);
            _solutionExplorer.Rebuild(_workspace);
            RememberRecentProject(project.FilePath!);
        }
    }

    private void OpenProject()
    {
        var dialog = new OpenDialog { Title = "Open Project" };
        Application.Run(dialog);
        var path = dialog.FilePaths.FirstOrDefault();
        if (path is null)
            return;

        OpenProjectOrSolution(path);
    }

    /// <summary>
    /// Opens a .tproj or .tsln file directly by path, without going through the Open Project file
    /// dialog - used by both <see cref="OpenProject"/> and the File menu's Recent Projects and
    /// Solutions submenu. Drops the path from the recent list (rather than opening it) if it no
    /// longer exists on disk, since the file may have been moved/deleted since it was recorded.
    /// </summary>
    private void OpenProjectOrSolution(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.ErrorQuery(Application.Instance, "File Not Found", $"'{path}' no longer exists.", ["OK"]);
            _recentProjects.Remove(path);
            RefreshRecentProjectsMenu();
            return;
        }

        if (path.EndsWith(TedideSolution.FileExtension, StringComparison.OrdinalIgnoreCase))
            _workspace.OpenSolution(path);
        else if (path.EndsWith(TedideProject.FileExtension, StringComparison.OrdinalIgnoreCase))
            _workspace.OpenProject(path);
        else
        {
            MessageBox.ErrorQuery(Application.Instance, "Unsupported file",
                $"Expected a {TedideProject.FileExtension} or {TedideSolution.FileExtension} file.", ["OK"]);
            return;
        }

        _solutionExplorer.Rebuild(_workspace);
        RememberRecentProject(path);
    }

    /// <summary>Records <paramref name="path"/> as the most-recently-used project/solution and refreshes the File menu's submenu to reflect it.</summary>
    private void RememberRecentProject(string path)
    {
        _recentProjects.Touch(path);
        RefreshRecentProjectsMenu();
    }

    /// <summary>Rebuilds the "Recent Projects and Solutions" submenu in place from <see cref="_recentProjects"/>.</summary>
    private void RefreshRecentProjectsMenu()
    {
        _recentProjectsMenuItem.SubMenu = BuildRecentProjectsMenu();
    }

    private Menu BuildRecentProjectsMenu()
    {
        if (_recentProjects.Paths.Count == 0)
            return new Menu([new MenuItem("(No Recent Projects or Solutions)", "", () => { }, Key.Empty)]);

        // "_1 Foo.tproj" through "_9 ..." give Alt+1..9 accelerators for the first nine entries,
        // matching Visual Studio's own numbered Recent Projects and Solutions list; the (rare)
        // 10th entry just doesn't get a single-digit accelerator.
        var items = _recentProjects.Paths.Select((path, i) => new MenuItem(
            $"_{i + 1} {Path.GetFileName(path)}",
            Path.GetDirectoryName(path) ?? "",
            () => OpenProjectOrSolution(path),
            Key.Empty));
        return new Menu(items.ToList());
    }

    /// <summary>
    /// Creates a new source/header file directly in <paramref name="directory"/> - the folder (or
    /// project root) the user right-clicked in the Solution Explorer to get here. Compilable files
    /// (.c/.s/.asm) are added to the owning project's SourceFiles and the project is saved; headers
    /// are not, since cl65 never compiles them directly. The new file is opened in the editor once
    /// created.
    /// </summary>
    private void NewFile(string directory)
    {
        var project = _workspace.Projects.FirstOrDefault(p =>
            directory.StartsWith(p.Directory, StringComparison.OrdinalIgnoreCase));
        if (project is null)
            return;

        var dialog = new NewFileDialog(directory);
        Application.Run(dialog);
        if (dialog.FileName is not { } fileName)
            return;

        var filePath = Path.Combine(directory, fileName);
        if (File.Exists(filePath))
        {
            MessageBox.ErrorQuery(Application.Instance, "File Exists", $"'{fileName}' already exists.", ["OK"]);
            return;
        }

        File.WriteAllText(filePath, string.Empty);

        if (SolutionExplorerTree.CompilableExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase))
        {
            project.SourceFiles.Add(Path.GetRelativePath(project.Directory, filePath));
            project.Save();
        }

        _solutionExplorer.Rebuild(_workspace);
        OpenFile(filePath);
    }

    /// <summary>
    /// Deletes the given file from disk after confirming with the user, closing it first if it's
    /// the currently open file, and removing it from any project's SourceFiles that references it.
    /// </summary>
    private void DeleteFile(string path)
    {
        var choice = MessageBox.Query(Application.Instance, "Delete File",
            $"Delete '{Path.GetFileName(path)}'? This cannot be undone.", ["Delete", "Cancel"]);
        if (choice != 0)
            return;

        if (string.Equals(_editorPane.OpenPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _editorPane.Close();
            _editorFrame.Title = NoFileOpenTitle;
            UpdateLanguageIndicator();
        }

        File.Delete(path);

        foreach (var project in _workspace.Projects)
        {
            var relativePath = Path.GetRelativePath(project.Directory, path);
            if (project.SourceFiles.RemoveAll(f => string.Equals(f, relativePath, StringComparison.OrdinalIgnoreCase)) > 0)
                project.Save();
        }

        _solutionExplorer.Rebuild(_workspace);
    }

    /// <summary>
    /// Opens the given file in the (single) editor pane, replacing whatever's currently open -
    /// prompting to save first if it has unsaved changes. Does nothing if it's already open.
    /// </summary>
    private void OpenFile(string path)
    {
        if (_editorPane.OpenPath is not null &&
            string.Equals(_editorPane.OpenPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        if (!ConfirmReplaceCurrentFile())
            return;

        _editorPane.Open(path);
        _editorFrame.Title = Path.GetFileName(path);
        UpdateLanguageIndicator();
    }

    /// <summary>
    /// Opens the Find in Files dialog, searching every loaded project's directory tree. If the
    /// user activates a result, opens its file (prompting to save the currently open one first,
    /// same as <see cref="OpenFile"/>) and moves the caret to the matched line.
    /// </summary>
    private void ShowFindInFiles()
    {
        if (_workspace.Projects.Count == 0)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        var dialog = new FindInFilesDialog(_workspace);
        Application.Run(dialog);
        if (dialog.SelectedMatch is { } match)
            OpenMatch(match);
    }

    /// <summary>
    /// Opens a Find in Files match's file (unless it's already the open file) and moves the
    /// caret to the start of the matched line/column, scrolling it into view.
    /// </summary>
    private void OpenMatch(FindInFilesDialog.Match match)
    {
        if (!string.Equals(_editorPane.OpenPath, match.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            OpenFile(match.FilePath);
            if (!string.Equals(_editorPane.OpenPath, match.FilePath, StringComparison.OrdinalIgnoreCase))
                return; // User cancelled replacing the currently open (modified) file.
        }

        var document = _editorPane.Editor.Document;
        if (document is null || match.LineNumber < 1 || match.LineNumber > document.LineCount)
            return;

        var line = document.GetLineByNumber(match.LineNumber);
        var column = Math.Clamp(match.ColumnNumber - 1, 0, line.Length);
        _editorPane.Editor.CaretOffset = line.Offset + column;
        _editorPane.Editor.SetFocus();
    }

    /// <summary>
    /// Opens a build diagnostic's file (unless it's already the open file) and highlights its
    /// line by selecting the whole line's text, scrolling it into view - the diagnostic has no
    /// column, unlike a Find in Files <see cref="FindInFilesDialog.Match"/>, so there's nothing
    /// more specific to place the caret at.
    /// </summary>
    private void OpenDiagnostic(BuildDiagnostic diagnostic)
    {
        var project = _workspace.ActiveProject;
        if (project is null)
            return;

        var filePath = Path.IsPathRooted(diagnostic.FilePath)
            ? diagnostic.FilePath
            : Path.Combine(project.Directory, diagnostic.FilePath);
        if (!File.Exists(filePath))
            return;

        if (!string.Equals(_editorPane.OpenPath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            OpenFile(filePath);
            if (!string.Equals(_editorPane.OpenPath, filePath, StringComparison.OrdinalIgnoreCase))
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

    private void CloseActiveFile()
    {
        if (_editorPane.OpenPath is null || !ConfirmReplaceCurrentFile())
            return;

        _editorPane.Close();
        _editorFrame.Title = NoFileOpenTitle;
        UpdateLanguageIndicator();
    }

    /// <summary>
    /// Closes the currently loaded solution/project(s) - clearing the Solution Explorer and
    /// closing the open file first (prompting to save it if modified, same as
    /// <see cref="CloseActiveFile"/>). Does nothing if nothing is loaded. Files already saved to
    /// disk are untouched; this only clears the in-memory session, same as <see cref="Workspace.Close"/>.
    /// </summary>
    private void CloseSolution()
    {
        if (_workspace.Projects.Count == 0)
            return;

        if (!ConfirmReplaceCurrentFile())
            return;

        _editorPane.Close();
        _editorFrame.Title = NoFileOpenTitle;
        UpdateLanguageIndicator();

        _workspace.Close();
        _solutionExplorer.Rebuild(_workspace);
    }

    /// <summary>
    /// Sets the status bar's language indicator (normally frozen at "Plain Text" - see
    /// <see cref="EditorStatusBar.UpdateLanguageShortcut"/>, which is only ever called once, at
    /// construction) to identify C source vs. header files specifically, since cc65's bundled
    /// syntax highlighter uses one generic "C++" definition for both and so can't tell them apart
    /// via <see cref="Editor.HighlightingDefinition"/> alone. Any other open file type (including
    /// .s/.asm, via <see cref="Highlighting.Cc65AssemblyHighlighting"/>) falls back to its own
    /// highlighter's name, same as the library's default behavior.
    /// </summary>
    private void UpdateLanguageIndicator()
    {
        _statusBar.LanguageShortcut.Title = _editorPane.OpenPath is { } path
            ? Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".c" => "C Source File",
                ".h" => "C Header File",
                _ => _editorPane.Editor.HighlightingDefinition?.Name ?? "Plain Text",
            }
            : "Plain Text";
        _statusBar.LanguageShortcut.SetNeedsDraw();
    }

    /// <summary>
    /// If the currently open file has unsaved changes, asks the user whether to save, discard, or
    /// cancel. Returns true if it's fine to proceed with replacing/closing it (nothing was open,
    /// it wasn't modified, or the user chose Save/Discard), false if the user cancelled.
    /// </summary>
    private bool ConfirmReplaceCurrentFile()
    {
        if (_editorPane.OpenPath is not { } currentPath || !_editorPane.IsModified)
            return true;

        var choice = MessageBox.Query(Application.Instance, "Unsaved Changes",
            $"Save changes to {Path.GetFileName(currentPath)}?", ["Save", "Discard", "Cancel"]);
        switch (choice)
        {
            case null or 2: // Esc or Cancel
                return false;
            case 0: // Save
                _editorPane.Save();
                break;
        }

        return true;
    }

    private void SaveAll()
    {
        _editorPane.Save();
        _workspace.SaveAll();
    }

    /// <summary>Builds the active project. Returns null (having already reported it) if none is loaded.</summary>
    private async Task<BuildResult?> BuildActiveProjectAsync()
    {
        var project = _workspace.ActiveProject;
        if (project is null)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return null;
        }

        SaveAll();
        _outputView.Text = string.Empty;
        _errorListView.SetDiagnostics([]);
        AppendOutputLine($"------ Build started: {project.Name} ({project.Target.ToCl65Id()}) ------");

        var result = await _toolchain.BuildAsync(project, onOutputLine: line =>
            Application.Invoke(() => AppendOutputLine(line)));

        _errorListView.SetDiagnostics(result.Diagnostics);
        AppendOutputLine(result.Succeeded
            ? $"------ Build succeeded in {result.Duration.TotalSeconds:0.0}s ------"
            : $"------ Build FAILED ({result.Errors.Count()} error(s)) in {result.Duration.TotalSeconds:0.0}s ------");

        // Refreshes the Solution Explorer's "Generated Files" node - e.g. a newly-written
        // assembler listing (see SolutionExplorerTree.AddGeneratedFilesNode) only appears once
        // the tree is rebuilt after this build actually wrote it.
        _solutionExplorer.Rebuild(_workspace);

        return result;
    }

    /// <summary>Builds the active project, then launches it in the VICE emulator matching its target if the build succeeded.</summary>
    private async Task RunActiveProjectAsync()
    {
        var result = await BuildActiveProjectAsync();
        if (result is null)
            return;

        if (!result.Succeeded)
        {
            AppendOutputLine("Not launching emulator - build failed.");
            return;
        }

        var project = _workspace.ActiveProject!;
        try
        {
            _vice.Launch(project, onOutputLine: line => Application.Invoke(() => AppendOutputLine(line)));
            AppendOutputLine($"------ Launched {ViceEmulator.ExecutableNameFor(project.Target)} ------");
        }
        catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException or InvalidOperationException)
        {
            AppendOutputLine($"Could not launch emulator: {ex.Message}");
        }
    }

    /// <summary>Deletes the active project's build artifacts (object files and the linked output binary) without rebuilding.</summary>
    private void CleanActiveProject()
    {
        var project = _workspace.ActiveProject;
        if (project is null)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        var removed = _toolchain.Clean(project);
        AppendOutputLine($"------ Clean: {project.Name} ------");
        if (removed.Count == 0)
        {
            AppendOutputLine("Nothing to clean.");
            return;
        }

        foreach (var path in removed)
            AppendOutputLine($"Deleted {Path.GetFileName(path)}");
        AppendOutputLine($"------ Clean complete: {removed.Count} file(s) removed ------");

        // Drops the Solution Explorer's "Generated Files" node if the assembler listing it was
        // showing is one of the files just deleted.
        _solutionExplorer.Rebuild(_workspace);
    }

    /// <summary>
    /// Opens the settings dialog for the active project - its Settings tab (name, target, output
    /// file, extra cl65 arguments) and Optimizer tab (cc65 optimization preset). Rebuilds the
    /// Solution Explorer afterward since its project node label includes the name and target,
    /// which the dialog may have just changed.
    /// </summary>
    private void ShowProjectSettings()
    {
        var project = _workspace.ActiveProject;
        if (project is null)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        var dialog = new ProjectSettingsDialog(project);
        Application.Run(dialog);
        if (dialog.Saved)
            _solutionExplorer.Rebuild(_workspace);
    }

    private void AppendOutputLine(string line)
    {
        _outputView.Text += line + "\n";
        _outputView.MoveEnd();
    }
}
