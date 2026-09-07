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
    private readonly LayoutSettings _layoutSettings = LayoutSettings.Load();

    /// <summary>The File menu's "Recent Projects and Solutions" item - kept as a field so its
    /// SubMenu can be rebuilt in place whenever <see cref="_recentProjects"/> changes.</summary>
    private MenuItem _recentProjectsMenuItem = null!;

    private readonly SolutionExplorerTree _solutionExplorer = new();
    private readonly EditorPane _editorPane = new();
    private readonly FrameView _editorFrame;
    private readonly OutputView _outputView = new();
    private readonly ErrorListView _errorListView = new();
    private Tabs _outputTabs = null!;
    private View _outputTab = null!;
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
            // Tracks _editorFrame's own height directly - unlike Width, above, this is a straight
            // Dim.Func, not Dim.Fill(Dim.Func(...)): explorerFrame and _editorFrame sit side by side
            // at the same Y, sharing a row, so explorerFrame's height should just equal
            // _editorFrame's, not "fill down to a margin sized like it" (Dim.Fill(margin) computes
            // SuperViewHeight - margin, which only happens to equal _editorFrame.Frame.Height when
            // that's exactly half the window - wrong the rest of the time, and invisible until the
            // Solution Explorer had enough content to overflow a box that was quietly the wrong size
            // all along). See _editorFrame's own comment for why the draggable border has to live
            // there rather than on outputTabs, below.
            Height = Dim.Func(_ => _editorFrame!.Frame.Height),
        };
        _solutionExplorer.Width = Dim.Fill();
        _solutionExplorer.Height = Dim.Fill();
        _solutionExplorer.FileActivated += OpenFile;
        _solutionExplorer.NewFileRequested += NewFile;
        _solutionExplorer.RenameFileRequested += RenameFile;
        _solutionExplorer.DeleteFileRequested += DeleteFile;
        explorerFrame.Add(_solutionExplorer);

        _editorFrame = new FrameView
        {
            Title = NoFileOpenTitle,
            X = Pos.Right(explorerFrame),
            Y = Pos.Bottom(_menuBar),
            Width = Dim.Percent(Math.Clamp(_layoutSettings.ExplorerEditorSplitPercent, 10, 90)),
            // A Dim.Func, not a plain Dim.Percent(70), so this keeps recomputing fresh (still 70%,
            // floored at MinOutputPaneHeight for the Output/Error List pane below) on every layout
            // pass rather than being decided once - see ClampedTopRowHeight's own comment for why
            // deciding it once is exactly what caused the Solution Explorer to visibly jump/shrink
            // after opening a project.
            Height = Dim.Func(_ => ClampedTopRowHeight()),
            // Makes this frame's left AND bottom borders draggable splitters - left, between it and
            // the Solution Explorer (explorerFrame's Width, above, tracks this frame's Frame.Width
            // live); bottom, between the whole top row and the Output/Error List pane below
            // (explorerFrame's Height, above, tracks this frame's Frame.Height live, and outputTabs'
            // Y, below, is pinned to this frame's bottom edge). A plain Tabs control like
            // outputTabs has no border of its own for ViewArrangement to hook a drag onto - unlike
            // this FrameView, which already has one - so the draggable edge has to live here
            // instead, even though visually it's the boundary between the two rows either way.
            // CanFocus is required for the border-drag mouse interaction to register.
            Arrangement = ViewArrangement.LeftResizable | ViewArrangement.BottomResizable,
            CanFocus = true,
        };
        _editorPane.FindInFilesRequested += ShowFindInFiles;
        _editorFrame.Add(_editorPane);
        // explorerFrame's Width (above) reads _editorFrame.Frame.Width live, but within a single
        // layout pass explorerFrame is resolved before _editorFrame is - so it reads _editorFrame's
        // width from *before* this pass updates it, one pass stale (confirmed by instrumenting both
        // views' FrameChanged: on the pass where the window's true size first becomes known,
        // explorerFrame reads _editorFrame.Frame.Width as still 0 and claims the full window width;
        // _editorFrame then resolves its own real width later in that same pass, one step too late
        // for explorerFrame to have used it). Nothing naturally triggers a second pass to let
        // explorerFrame catch up - not even a plain SetNeedsLayout() called synchronously from here,
        // since that fires *during* the very pass it needs to correct, and gets superseded when that
        // pass finishes. Deferring it via Application.AddTimeout(TimeSpan.Zero, ...) - the same
        // technique that fixed the Recent Projects menu not closing - queues it for strictly after
        // the current pass completes, and (unlike a plain call) still fires even if the app is
        // otherwise idle with no user input driving further iterations. Without this,
        // explorerFrame stays at its full-window-width misreading (and _editorFrame, positioned via
        // Pos.Right(explorerFrame), ends up pushed off-screen) until something unrelated - e.g.
        // populating the Solution Explorer after opening a project - happens to force a full
        // re-layout.
        _editorFrame.FrameChanged += (_, _) => Application.AddTimeout(TimeSpan.Zero, () =>
        {
            SetNeedsLayout();
            return false;
        });
        // Keeps the Output/Error List pane from being dragged smaller than MinOutputPaneHeight.
        // Only needed for an actual BottomResizable drag: that's the one thing that overwrites
        // Height with a literal value, which ClampedTopRowHeight (above) can no longer correct once
        // it's no longer the formula in place. FrameChanged fires after Frame is resolved for any
        // cause, and the corrective Height assignment it triggers here resolves to a Frame that no
        // longer undershoots, so it does not re-trigger itself.
        _editorFrame.FrameChanged += (_, _) =>
        {
            if (_editorFrame.SuperView is not { } superView || superView.Frame.Height <= 0)
                return;

            var maxHeight = superView.Frame.Height - 1 - MinOutputPaneHeight - _editorFrame.Frame.Y;
            if (maxHeight > 0 && _editorFrame.Frame.Height > maxHeight)
                _editorFrame.Height = maxHeight;
        };

        _outputTabs = new Tabs
        {
            X = 0,
            Y = Pos.Bottom(_editorFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        _outputTab = new View { Title = "_Output", Width = Dim.Fill(), Height = Dim.Fill() };
        _outputView.Width = Dim.Fill();
        _outputView.Height = Dim.Fill();
        _outputTab.Add(_outputView);

        var errorListTab = new View { Title = "_Error List", Width = Dim.Fill(), Height = Dim.Fill() };
        _errorListView.Width = Dim.Fill();
        _errorListView.Height = Dim.Fill();
        _errorListView.DiagnosticActivated += OpenDiagnostic;
        errorListTab.Add(_errorListView);

        _outputTabs.Add(_outputTab);
        _outputTabs.Add(errorListTab);

        Add([_menuBar, explorerFrame, _editorFrame, _outputTabs, _statusBar]);
    }

    private const string NoFileOpenTitle = "(no file open)";

    /// <summary>The Output/Error List pane's minimum height, in rows - enforced both here and by
    /// <see cref="AppShell"/>'s constructor via <c>_editorFrame.FrameChanged</c> (see that handler's
    /// own comment for why both are needed).</summary>
    private const int MinOutputPaneHeight = 5;

    /// <summary>
    /// _editorFrame's natural (undragged) Height: 70% of the window, floored so the Output/Error
    /// List pane below always keeps at least <see cref="MinOutputPaneHeight"/> rows. Recomputed
    /// fresh on every layout pass (via Dim.Func, not a one-time Dim.Percent(70)) so it never gets
    /// stuck on a stale reading of the window's size from before the terminal's true size settled -
    /// which is exactly what previously made the Solution Explorer/Editor row visibly shrink the
    /// moment something (e.g. populating the Solution Explorer after opening a project) triggered
    /// the first layout pass after that settling.
    /// </summary>
    private int ClampedTopRowHeight()
    {
        var superViewHeight = _editorFrame.SuperView?.Frame.Height ?? 0;
        if (superViewHeight <= 0)
            return 1;

        var percent = Math.Clamp(_layoutSettings.TopRowHeightPercent, 10, 90);
        var desired = superViewHeight * percent / 100;
        var maxAllowed = superViewHeight - 1 - MinOutputPaneHeight - _editorFrame.Frame.Y;
        return Math.Clamp(desired, 1, Math.Max(1, maxAllowed));
    }

    /// <summary>
    /// Records the two splitters' current positions (as a percentage of the window's current
    /// width/height, so they still make sense after resizing the terminal or moving to a
    /// different one) so the next run starts back where this one left off. Called once, from
    /// Program.cs, right after <c>Application.Run(shell)</c> returns - i.e. when the user quits.
    /// </summary>
    public void SaveLayoutSettings()
    {
        if (Frame.Width <= 0 || Frame.Height <= 0)
            return;

        _layoutSettings.ExplorerEditorSplitPercent = _editorFrame.Frame.Width * 100 / Frame.Width;
        _layoutSettings.TopRowHeightPercent = _editorFrame.Frame.Height * 100 / Frame.Height;
        _layoutSettings.Save();
    }

    private EditorMenuBar BuildMenuBar()
    {
        var menuBar = new EditorMenuBar(_editorPane.Editor);

        _recentProjectsMenuItem = new MenuItem("_Recent Projects and Solutions", "", new Menu(BuildRecentProjectsMenuItems()));
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
        // Fold Indicators/Word Wrap/Show Tabs/Scrollbars) - for free. Append our own Find in Files
        // to the end of that same Edit menu rather than giving it a top-level menu of its own -
        // EditMenu.PopoverMenu.Root is the live Menu backing the dropdown (EditMenu itself only
        // holds the items it was constructed with), so items are added to it the same way the
        // library builds its own: a separator, then the MenuItem.
        var editMenuItems = menuBar.EditMenu.PopoverMenu!.Root!;
        editMenuItems.Add(new Line());
        editMenuItems.Add(new MenuItem("_Find in Files...", "", () => ShowFindInFiles(), Key.F.WithCtrl.WithShift));
        menuBar.Menus = [fileMenu, menuBar.EditMenu, menuBar.ViewMenu, buildMenu, projectMenu, themeMenu];
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

    /// <summary>
    /// Repopulates the "Recent Projects and Solutions" submenu in place from <see cref="_recentProjects"/>.
    /// </summary>
    /// <remarks>
    /// This is called from inside a recent entry's own click handler - i.e. while that very
    /// submenu is still the one on screen, and while the framework's own "activate this item, then
    /// close the whole menu" sequence for that click is still in progress. Two things about that
    /// matter here:
    /// <list type="bullet">
    /// <item>Mutating the existing <see cref="Menu"/> instance's items (rather than assigning a
    /// brand new one to <see cref="MenuItem.SubMenu"/>) avoids orphaning the menu the user is
    /// currently looking at - nothing would otherwise still reference it in order to close it.</item>
    /// <item>Even an in-place mutation still has to happen after the framework finishes closing the
    /// menu, not synchronously inside the click handler - the just-clicked <see cref="MenuItem"/>
    /// is that in-progress close sequence's own reference point, and <see cref="View.RemoveAll"/>
    /// would remove it (along with every sibling) out from under it. <see cref="Application.AddTimeout"/>
    /// with a zero delay defers the rebuild to the next main loop iteration, after this click has
    /// finished closing the menu normally.</item>
    /// </list>
    /// </remarks>
    private void RefreshRecentProjectsMenu()
    {
        Application.AddTimeout(TimeSpan.Zero, () =>
        {
            var menu = _recentProjectsMenuItem.SubMenu!;
            menu.RemoveAll();
            foreach (var item in BuildRecentProjectsMenuItems())
                menu.Add(item);
            return false;
        });
    }

    private List<MenuItem> BuildRecentProjectsMenuItems()
    {
        if (_recentProjects.Paths.Count == 0)
            return [new MenuItem("(No Recent Projects or Solutions)", "", () => { }, Key.Empty)];

        // "_1 Foo.tproj" through "_9 ..." give Alt+1..9 accelerators for the first nine entries,
        // matching Visual Studio's own numbered Recent Projects and Solutions list; the (rare)
        // 10th entry just doesn't get a single-digit accelerator.
        return _recentProjects.Paths.Select((path, i) => new MenuItem(
            $"_{i + 1} {Path.GetFileName(path)}",
            Path.GetDirectoryName(path) ?? "",
            () => OpenProjectOrSolution(path),
            Key.Empty)).ToList();
    }

    /// <summary>
    /// Creates a new source/header file directly in <paramref name="directory"/> - the folder the
    /// user right-clicked in the Solution Explorer to get here (New File isn't offered on the
    /// project root itself - see <see cref="SolutionExplorerTree.TryShowContextMenu"/>).
    /// Compilable files (.c/.s/.asm) are added to the owning project's SourceFiles and the
    /// project is saved; headers are not, since cl65 never compiles them directly. The new file
    /// is opened in the editor once created.
    /// </summary>
    private void NewFile(string directory)
    {
        var project = _workspace.Projects.FirstOrDefault(p =>
            directory.StartsWith(p.Directory, StringComparison.OrdinalIgnoreCase));
        if (project is null)
            return;

        // Default the new file's name/extension to match the convention the folder itself
        // implies - "include" is where headers live, "src" is where compiled sources live - so
        // the common case needs no manual edit beyond the base name. Anywhere else keeps the old
        // plain "newfile.c" default.
        var defaultFileName = Path.GetFileName(directory).ToLowerInvariant() switch
        {
            "include" => "newfile.h",
            _ => "newfile.c",
        };

        var dialog = new NewFileDialog(directory, defaultFileName);
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
            project.SourceFiles.Add(RelativeSourcePath(project, filePath));
            project.Save();
        }

        _solutionExplorer.Rebuild(_workspace);
        OpenFile(filePath);
    }

    /// <summary>
    /// Renames a file in place (same folder) after confirming a new name with the user. If the
    /// file being renamed is the one currently open in the editor, prompts to save unsaved
    /// changes first (same as <see cref="OpenFile"/> replacing it) - discarding, rather than
    /// losing them, since the on-disk (not the in-memory) content is what actually gets renamed -
    /// then reopens it from its new path afterward. Updates every project's SourceFiles that
    /// referenced the old path: removed if the new name's extension isn't one cl65 compiles,
    /// added under the new path if it is (covering a rename that changes the extension, e.g.
    /// .c -> .h, not just the base name), same as <see cref="NewFile"/>/<see cref="DeleteFile"/>'s
    /// own compilable-extension check.
    /// </summary>
    private void RenameFile(string path)
    {
        var dialog = new RenameFileDialog(Path.GetFileName(path));
        Application.Run(dialog);
        if (dialog.NewFileName is not { } newFileName)
            return;

        var newPath = Path.Combine(Path.GetDirectoryName(path)!, newFileName);
        if (File.Exists(newPath))
        {
            MessageBox.ErrorQuery(Application.Instance, "File Exists", $"'{newFileName}' already exists.", ["OK"]);
            return;
        }

        // Only ask about unsaved changes once the user has actually committed to a real,
        // non-colliding rename above - not before, or a Cancel out of either step here would
        // have already saved/discarded their in-progress edits for nothing.
        var isOpen = string.Equals(_editorPane.OpenPath, path, StringComparison.OrdinalIgnoreCase);
        if (isOpen && !ConfirmReplaceCurrentFile())
            return;

        File.Move(path, newPath);

        foreach (var project in _workspace.Projects)
        {
            var oldRelativePath = RelativeSourcePath(project, path);
            var wasTracked = project.SourceFiles.RemoveAll(f => string.Equals(f, oldRelativePath, StringComparison.OrdinalIgnoreCase)) > 0;

            var stillCompilable = SolutionExplorerTree.CompilableExtensions.Contains(Path.GetExtension(newPath), StringComparer.OrdinalIgnoreCase);
            if (wasTracked && stillCompilable)
                project.SourceFiles.Add(RelativeSourcePath(project, newPath));

            if (wasTracked)
                project.Save();
        }

        _solutionExplorer.Rebuild(_workspace);
        if (isOpen)
        {
            // Not OpenFile(newPath) - it would re-run ConfirmReplaceCurrentFile, prompting a
            // second time about the (already-handled, now nonexistent) old path if the user chose
            // Discard above rather than Save.
            _editorPane.Open(newPath);
            _editorFrame.Title = newFileName;
            UpdateLanguageIndicator();
        }
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
            var relativePath = RelativeSourcePath(project, path);
            if (project.SourceFiles.RemoveAll(f => string.Equals(f, relativePath, StringComparison.OrdinalIgnoreCase)) > 0)
                project.Save();
        }

        _solutionExplorer.Rebuild(_workspace);
    }

    /// <summary>
    /// A file's path relative to <paramref name="project"/>'s own directory, in the same "/"
    /// (never "\") form <see cref="TedideProject.SourceFiles"/> entries are written in - matching
    /// how the bundled sample .tproj files (and <see cref="Workspace.NewProject"/>'s scaffolded
    /// one) are authored, since <see cref="Path.GetRelativePath(string, string)"/> alone returns
    /// "\"-separated paths on Windows, which would silently fail to match/dedupe against those.
    /// </summary>
    private static string RelativeSourcePath(TedideProject project, string path) =>
        Path.GetRelativePath(project.Directory, path).Replace('\\', '/');

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
    /// <param name="initialSearchText">Pre-populates (and immediately searches for) this text -
    /// e.g. the editor's current selection, via <see cref="EditorPane.FindInFilesRequested"/>.
    /// Empty for the Edit menu/Ctrl+Shift+F path, which starts with a blank search field.</param>
    private void ShowFindInFiles(string initialSearchText = "")
    {
        if (_workspace.Projects.Count == 0)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        var dialog = new FindInFilesDialog(_workspace, initialSearchText);
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
        _outputView.Clear();
        _errorListView.SetDiagnostics([]);
        AppendOutputLine($"------ Build started: {project.Name} ({project.Target.ToCl65Id()}) ------");

        var result = await _toolchain.BuildAsync(project, onOutputLine: line =>
            Application.Invoke(() => AppendOutputLine(line)));

        _errorListView.SetDiagnostics(result.Diagnostics);
        AppendOutputLine(result.Succeeded
            ? $"------ Build succeeded in {result.Duration.TotalSeconds:0.0}s ------"
            : $"------ Build FAILED ({result.Errors.Count()} error(s)) in {result.Duration.TotalSeconds:0.0}s ------");

        if (result.Succeeded && new FileInfo(project.ResolvedOutputFile) is { Exists: true } outputFile)
            AppendOutputLine($"{Path.GetFileName(project.ResolvedOutputFile)}: {outputFile.Length} bytes");

        // Refreshes the Solution Explorer's "Generated Files" node - e.g. newly-written
        // assembler listings (see SolutionExplorerTree.AddGeneratedFilesNode) only appear once
        // the tree is rebuilt after this build actually wrote them.
        _solutionExplorer.Rebuild(_workspace);

        return result;
    }

    /// <summary>Builds the active project, then launches it in the VICE emulator matching its target if the build succeeded.</summary>
    private async Task RunActiveProjectAsync()
    {
        ShowOutputTab();

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

        // Drops (or shrinks) the Solution Explorer's "Generated Files" node if the assembler
        // listings it was showing are among the files just deleted.
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

    private void AppendOutputLine(string line) => _outputView.AppendLine(line);

    /// <summary>
    /// Switches the Output/Error List pane to its "Output" tab and gives the output view itself
    /// input focus - used when running a project, so its build/launch output is immediately
    /// visible rather than left behind whatever tab (e.g. Error List) the user had last selected.
    /// </summary>
    private void ShowOutputTab()
    {
        _outputTabs.Value = _outputTab;
        _outputView.SetFocus();
    }
}
