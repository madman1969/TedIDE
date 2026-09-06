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

    private readonly SolutionExplorerTree _solutionExplorer = new();
    private readonly EditorPane _editorPane = new();
    private readonly FrameView _editorFrame;
    private readonly TextView _outputView = new() { ReadOnly = true };
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
            Width = Dim.Percent(25),
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
            Width = Dim.Fill(),
            Height = Dim.Percent(70),
        };
        _editorFrame.Add(_editorPane);

        var outputFrame = new FrameView
        {
            Title = "Output",
            X = 0,
            Y = Pos.Bottom(_editorFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        _outputView.Width = Dim.Fill();
        _outputView.Height = Dim.Fill();
        outputFrame.Add(_outputView);

        Add([_menuBar, explorerFrame, _editorFrame, outputFrame, _statusBar]);
    }

    private const string NoFileOpenTitle = "(no file open)";

    private EditorMenuBar BuildMenuBar()
    {
        var menuBar = new EditorMenuBar(_editorPane.Editor);

        var fileMenu = new MenuBarItem("_File", new List<MenuItem>
        {
            new("_New Project...", "", NewProject, Key.N.WithCtrl),
            new("_Open Project...", "", OpenProject, Key.O.WithCtrl),
            new("_Save", "", SaveAll, Key.S.WithCtrl),
            new("_Close File", "", CloseActiveFile, Key.W.WithCtrl),
            new("_Quit", "", () => Application.RequestStop(this), Key.Q.WithCtrl),
        });

        var buildMenu = new MenuBarItem("_Build", new List<MenuItem>
        {
            new("_Build Project", "", () => _ = BuildActiveProjectAsync(), Key.F5),
            new("_Clean Project", "", CleanActiveProject, Key.Empty),
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
        menuBar.Menus = [fileMenu, menuBar.EditMenu, menuBar.ViewMenu, buildMenu, themeMenu];
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
            _workspace.NewProject(dialog.Directory, dialog.ProjectName, target);
            _solutionExplorer.Rebuild(_workspace);
        }
    }

    private void OpenProject()
    {
        var dialog = new OpenDialog { Title = "Open Project" };
        Application.Run(dialog);
        var path = dialog.FilePaths.FirstOrDefault();
        if (path is null)
            return;

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
    }

    private void CloseActiveFile()
    {
        if (_editorPane.OpenPath is null || !ConfirmReplaceCurrentFile())
            return;

        _editorPane.Close();
        _editorFrame.Title = NoFileOpenTitle;
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

    private async Task BuildActiveProjectAsync()
    {
        var project = _workspace.ActiveProject;
        if (project is null)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        SaveAll();
        _outputView.Text = string.Empty;
        AppendOutputLine($"------ Build started: {project.Name} ({project.Target.ToCl65Id()}) ------");

        var result = await _toolchain.BuildAsync(project, onOutputLine: line =>
            Application.Invoke(() => AppendOutputLine(line)));

        AppendOutputLine(result.Succeeded
            ? $"------ Build succeeded in {result.Duration.TotalSeconds:0.0}s ------"
            : $"------ Build FAILED ({result.Errors.Count()} error(s)) in {result.Duration.TotalSeconds:0.0}s ------");
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
    }

    private void AppendOutputLine(string line)
    {
        _outputView.Text += line + "\n";
        _outputView.MoveEnd();
    }
}
