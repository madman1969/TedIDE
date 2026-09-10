using Tedide.Theming;
using Tedide.App.Views;
using Tedide.Build;
using Tedide.Core;
using Tedide.Core.Debugging;
using Tedide.Debug;
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
    // Not inline-initialized (unlike its siblings below) - its BinDirectory depends on
    // ToolchainSettings, applied in the constructor via ApplyToolchainSettings (also reapplied
    // after every ProjectSettingsDialog save - see ShowProjectSettings).
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
    private readonly SymbolPanelView _symbolPanel = new();
    private readonly DebugPanelView _debugPanel = new();
    private readonly CurrentDebugLineTransformer _debugLineTransformer = new();
    private readonly BreakpointLineTransformer _breakpointLineTransformer = new();
    private BreakpointsFile _breakpoints = new();
    private ViceMonitorClient? _debugClient;
    private DbgFile? _dbgFile;
    private bool _isDebugging;
    private bool _isStopped;
    private Tabs _outputTabs = null!;
    private View _outputTab = null!;
    private View _debugTab = null!;
    private readonly EditorMenuBar _menuBar;
    private readonly EditorStatusBar _statusBar;

    public AppShell()
    {
        Title = "Tedide - CC65 IDE";
        Width = Dim.Fill();
        Height = Dim.Fill();

        // At startup, an unset Cc65Home must not clobber a CC65_HOME the user has set some other
        // way (shell profile, system env) before launching Tedide - only an explicit edit via the
        // CC65 tab's Save (see ShowProjectSettings) is allowed to unset it, hence allowUnsettingCc65Home: false here.
        ApplyToolchainSettings(ToolchainSettings.Load(), allowUnsettingCc65Home: false);

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
        _solutionExplorer.AddExistingItemRequested += AddExistingItem;
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
        // Every tab title in the app gets a leading/trailing space (" _Output " rather than
        // "_Output") - a standing style convention, not specific to this pane.
        _outputTab = new View { Title = " _Output ", Width = Dim.Fill(), Height = Dim.Fill() };
        _outputView.Width = Dim.Fill();
        _outputView.Height = Dim.Fill();
        _outputTab.Add(_outputView);

        var errorListTab = new View { Title = " _Error List ", Width = Dim.Fill(), Height = Dim.Fill() };
        _errorListView.Width = Dim.Fill();
        _errorListView.Height = Dim.Fill();
        _errorListView.DiagnosticActivated += OpenDiagnostic;
        errorListTab.Add(_errorListView);

        var symbolsTab = new View { Title = " _Symbols ", Width = Dim.Fill(), Height = Dim.Fill() };
        _symbolPanel.Width = Dim.Fill();
        _symbolPanel.Height = Dim.Fill();
        _symbolPanel.LineActivated += OpenSymbol;
        symbolsTab.Add(_symbolPanel);

        _debugTab = new View { Title = " _Debug ", Width = Dim.Fill(), Height = Dim.Fill() };
        _debugPanel.Width = Dim.Fill();
        _debugPanel.Height = Dim.Fill();
        _debugTab.Add(_debugPanel);

        _outputTabs.Add(_outputTab);
        _outputTabs.Add(errorListTab);
        _outputTabs.Add(symbolsTab);
        _outputTabs.Add(_debugTab);

        // Breakpoint highlighting registered before the current-debug-line one, so the latter's
        // Accent color wins on a line that's both a breakpoint and the paused line - see
        // BreakpointLineTransformer's own doc comment for why order matters here.
        _editorPane.Editor.LineTransformers.Add(_breakpointLineTransformer);
        // Highlights the source line execution is stopped at during a debug session - see its own
        // doc comment for why LineTransformers (not BackgroundRenderers) is the right extension
        // point for this.
        _editorPane.Editor.LineTransformers.Add(_debugLineTransformer);

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
        // BindKeyToApplication = true is what actually makes a MenuItem's Key fire while its
        // dropdown is closed - a Shortcut/MenuItem's Key only auto-registers a *HotKeyBinding*
        // (confirmed in Terminal.Gui's own Shortcut.cs), which only fires while the view holding
        // it is mounted in the tree; a menu's dropdown Menu only exists while open, unlike the
        // always-present status bar (why Build/Breakpoint/Run/Save/Go To Line/Quit above work via
        // a *duplicate* status-bar Shortcut instead). Applied here rather than duplicating these
        // less-frequently-used actions into the status bar too and cluttering it further.
        var fileMenu = new MenuBarItem("_File", new List<View>
        {
            new MenuItem("_New Project...", "", NewProject, Key.N.WithCtrl) { BindKeyToApplication = true },
            new MenuItem("_Open Project...", "", OpenProject, Key.O.WithCtrl) { BindKeyToApplication = true },
            _recentProjectsMenuItem,
            new MenuItem("Close _Project", "", CloseSolution, Key.Empty),
            new Line(),
            new MenuItem("_Save", "", SaveAll, Key.S.WithCtrl),
            new MenuItem("_Close File", "", CloseActiveFile, Key.W.WithCtrl) { BindKeyToApplication = true },
            new Line(),
            new MenuItem("_Quit", "", () => Application.RequestStop(this), Key.Q.WithCtrl),
        });

        var buildMenu = new MenuBarItem("_Build", new List<MenuItem>
        {
            new("_Build Project", "", () => _ = BuildActiveProjectAsync(), Key.F5),
            new("_Clean Project", "", CleanActiveProject, Key.Empty),
            new("_Run Project", "", () => _ = RunActiveProjectAsync(), Key.F6),
        });

        // F5/F6 above are already Build/Run - Start Debugging/Continue/Step use keys of their own.
        var debugMenu = new MenuBarItem("_Debug", new List<View>
        {
            new MenuItem("_Start Debugging", "", () => _ = StartDebuggingAsync(), Key.F5.WithShift) { BindKeyToApplication = true },
            new MenuItem("_Continue", "", () => _ = ContinueDebuggingAsync(), Key.F5.WithCtrl) { BindKeyToApplication = true },
            new MenuItem("S_tep", "", () => _ = StepDebuggingAsync(), Key.F10) { BindKeyToApplication = true },
            new MenuItem("Sto_p Debugging", "", () => _ = StopDebuggingAsync(), Key.Empty),
            new Line(),
            new MenuItem("_Toggle Breakpoint", "", ToggleBreakpointAtCursor, Key.F9),
            new MenuItem("_Breakpoints...", "", ShowBreakpointsDialog, Key.Empty),
        });

        var projectMenu = new MenuBarItem("_Project", new List<MenuItem>
        {
            new("_Settings...", "", ShowProjectSettings, Key.Empty),
        });

        // Shared with Tedide.DocViewer (ThemeMenuBuilder, in Tedide.Theming) - same nine entries,
        // and the currently active one is marked with a leading checkmark, kept live via
        // ThemeSwitcher.Changed.
        var themeMenu = ThemeMenuBuilder.Build();

        var helpMenu = new MenuBarItem("_Help", new List<MenuItem>
        {
            new("_About Tedide...", "", ShowAbout, Key.Empty),
        });

        // Replace the library's default single-file File menu (New/Open/Save/Save As/Quit) with
        // our own project-aware one, but keep its auto-generated EditMenu/ViewMenu - already wired
        // directly to the editor (Find/Replace/Undo/Redo/Cut/Copy/Paste/Select All; Line Numbers/
        // Fold Indicators/Word Wrap/Show Tabs/Scrollbars) - for free. Insert our own Find in Files
        // at the very top of that same Edit menu (above Find, as the first item of its search
        // group) rather than giving it a top-level menu of its own - EditMenu.PopoverMenu.Root is
        // the live Menu backing the dropdown (EditMenu itself only holds the items it was
        // constructed with), so AddAt(0, ...) inserts it before Find the same way the library adds
        // its own items via Add().
        var editMenuItems = menuBar.EditMenu.PopoverMenu!.Root!;
        editMenuItems.AddAt(0, new MenuItem("_Find in Files...", "", () => ShowFindInFiles(), Key.F.WithCtrl.WithShift) { BindKeyToApplication = true });
        editMenuItems.AddAt(1, new MenuItem("_Go To Line...", "", ShowGoToLine, Key.G.WithCtrl));
        menuBar.Menus = [fileMenu, menuBar.EditMenu, menuBar.ViewMenu, buildMenu, debugMenu, projectMenu, themeMenu, helpMenu];
        menuBar.X = 0;
        menuBar.Y = 0;
        menuBar.Width = Dim.Fill();
        return menuBar;
    }

    private EditorStatusBar BuildStatusBar()
    {
        var statusBar = new EditorStatusBar(_editorPane.Editor);
        // ThemeDropDown drives Terminal.Gui's own ConfigurationManager-based ThemeManager, separate
        // from our own SchemeManager-based ThemeSwitcher (see Tedide.Theming/ThemeSwitcher.cs) - leaving
        // both active would let this dropdown silently overwrite our custom Schemes. Hide it; the
        // Theme menu above is our one theme switcher.
        statusBar.ThemeDropDown.Visible = false;
        statusBar.Add(new Shortcut(Key.F5, "~F5~ Build", () => _ = BuildActiveProjectAsync()));
        // A plain MenuItem's Key only acts as a hotkey while its menu is already open - a Shortcut
        // is what actually makes a key global, the same reason Build's F5 above needs one too. F6
        // Run and Ctrl+S Save had the same gap (menu-only, never worked while the editor had focus)
        // until it was reported and fixed here alongside F9/Ctrl+G.
        statusBar.Add(new Shortcut(Key.F6, "~F6~ Run", () => _ = RunActiveProjectAsync()));
        statusBar.Add(new Shortcut(Key.F9, "~F9~ Breakpoint", ToggleBreakpointAtCursor));
        statusBar.Add(new Shortcut(Key.S.WithCtrl, "~^S~ Save", SaveAll));
        statusBar.Add(new Shortcut(Key.G.WithCtrl, "~^G~ Go To Line", ShowGoToLine));
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
            _symbolPanel.Refresh(_workspace.ActiveProject);
            LoadBreakpointsForActiveProject();
            // NewProject always creates a wrapping .tsln alongside the .tproj (see its own doc
            // comment) - remember that, not the bare project, matching how opening one of the
            // bundled samples remembers its .tsln rather than the .tproj inside it.
            RememberRecentProject(_workspace.Solution!.FilePath!);
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
            TedideMessageBox.ErrorQuery("File Not Found", $"'{path}' no longer exists.", ["OK"]);
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
            TedideMessageBox.ErrorQuery("Unsupported file",
                $"Expected a {TedideProject.FileExtension} or {TedideSolution.FileExtension} file.", ["OK"]);
            return;
        }

        _solutionExplorer.Rebuild(_workspace);
        _symbolPanel.Refresh(_workspace.ActiveProject);
        LoadBreakpointsForActiveProject();
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
        // Silently drops any entry that no longer exists on disk before building the list, so a
        // moved/deleted project just quietly disappears from the menu rather than sitting there
        // until the user clicks it and gets the "File Not Found" error in OpenProjectOrSolution.
        _recentProjects.PruneMissing();

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
            TedideMessageBox.ErrorQuery("File Exists", $"'{fileName}' already exists.", ["OK"]);
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
    /// Copies one or more existing files, picked from anywhere on disk via a multi-select file
    /// dialog, into <paramref name="directory"/> - the same target folder "New File..." would use
    /// (see <see cref="NewFile"/>). The dialog's own file-type filter matches the folder's
    /// convention the same way <see cref="NewFile"/>'s default filename extension does ("include"
    /// -> headers, "src" -> compilable sources, elsewhere -> anything the Solution Explorer
    /// displays). A file already sitting at the destination path (e.g. one picked from inside this
    /// same folder) is left alone rather than copied onto itself; one that would collide with a
    /// different file already there is skipped, and every skip is reported together in one
    /// message once the whole batch is done rather than interrupting it file by file. Compilable
    /// copies not already in the project are added to its SourceFiles, same as <see cref="NewFile"/>.
    /// </summary>
    private void AddExistingItem(string directory)
    {
        var project = _workspace.Projects.FirstOrDefault(p =>
            directory.StartsWith(p.Directory, StringComparison.OrdinalIgnoreCase));
        if (project is null)
            return;

        var allowedType = Path.GetFileName(directory).ToLowerInvariant() switch
        {
            "include" => new AllowedType("Header Files", ".h", ".inc"),
            "src" => new AllowedType("Source Files", ".c", ".s", ".asm"),
            _ => new AllowedType("Source/Header Files", SolutionExplorerTree.DisplayedExtensions),
        };
        var dialog = new OpenDialog
        {
            Title = "Add Existing Item",
            OpenMode = OpenMode.File,
            AllowsMultipleSelection = true,
            AllowedTypes = [allowedType],
        };
        Application.Run(dialog);
        if (dialog.FilePaths.Count == 0)
            return;

        var skipped = new List<string>();
        var addedAny = false;
        foreach (var sourcePath in dialog.FilePaths)
        {
            var destinationPath = Path.Combine(directory, Path.GetFileName(sourcePath));
            var alreadyInPlace = string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase);

            if (!alreadyInPlace)
            {
                if (File.Exists(destinationPath))
                {
                    skipped.Add(Path.GetFileName(destinationPath));
                    continue;
                }
                File.Copy(sourcePath, destinationPath);
            }

            if (SolutionExplorerTree.CompilableExtensions.Contains(Path.GetExtension(destinationPath), StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = RelativeSourcePath(project, destinationPath);
                if (!project.SourceFiles.Any(f => string.Equals(f, relativePath, StringComparison.OrdinalIgnoreCase)))
                    project.SourceFiles.Add(relativePath);
            }

            addedAny = true;
        }

        if (addedAny)
            project.Save();

        if (skipped.Count > 0)
        {
            TedideMessageBox.ErrorQuery("Some Files Skipped",
                $"Already exists in this folder, skipped:\n{string.Join('\n', skipped)}", ["OK"]);
        }

        _solutionExplorer.Rebuild(_workspace);
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
            TedideMessageBox.ErrorQuery("File Exists", $"'{newFileName}' already exists.", ["OK"]);
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
            RefreshBreakpointHighlights();
        }
    }

    /// <summary>
    /// Deletes the given file from disk after confirming with the user, closing it first if it's
    /// the currently open file, and removing it from any project's SourceFiles that references it.
    /// </summary>
    private void DeleteFile(string path)
    {
        var choice = TedideMessageBox.Query("Delete File",
            $"Delete '{Path.GetFileName(path)}'? This cannot be undone.", ["Delete", "Cancel"]);
        if (choice != 0)
            return;

        if (string.Equals(_editorPane.OpenPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _editorPane.Close();
            _editorFrame.Title = NoFileOpenTitle;
            UpdateLanguageIndicator();
            RefreshBreakpointHighlights();
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
        RefreshBreakpointHighlights();
        // EditorPane.Open always makes the freshly-opened file editable - reassert read-only if
        // a debug session is in progress (e.g. a breakpoint in a different file, or the user
        // browsing via Solution Explorer while paused), same as StartDebuggingAsync sets initially.
        if (_isDebugging)
            _editorPane.Editor.ReadOnly = true;
    }

    /// <summary>Opens the Help > About dialog. Read-only - see <see cref="AboutDialog"/>.</summary>
    private void ShowAbout()
    {
        Application.Run(new AboutDialog());
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
    /// Prompts for a line number (pre-filled with the caret's current line) and moves the caret
    /// to the start of that line, scrolling it into view - same CaretOffset-assignment mechanism
    /// as <see cref="OpenMatch"/>, just without a column.
    /// </summary>
    private void ShowGoToLine()
    {
        var document = _editorPane.Editor.Document;
        if (_editorPane.OpenPath is null || document is null)
            return;

        var currentLineNumber = document.GetLineByOffset(_editorPane.Editor.CaretOffset).LineNumber;
        var dialog = new GoToLineDialog(currentLineNumber, document.LineCount);
        Application.Run(dialog);
        if (dialog.LineNumber is { } lineNumber)
        {
            _editorPane.Editor.CaretOffset = document.GetLineByNumber(lineNumber).Offset;
            _editorPane.Editor.SetFocus();
        }
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

    /// <summary>
    /// Opens a symbol panel entry's source file (lnk.map or .lbl - both plain text, unaffected by
    /// this being a structured panel over them, see <see cref="SymbolPanelView"/>) and moves the
    /// caret to its line, the same way <see cref="OpenMatch"/> does for a Find in Files result.
    /// </summary>
    private void OpenSymbol((string FilePath, int LineNumber) entry)
    {
        if (!File.Exists(entry.FilePath))
            return;

        if (!string.Equals(_editorPane.OpenPath, entry.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            OpenFile(entry.FilePath);
            if (!string.Equals(_editorPane.OpenPath, entry.FilePath, StringComparison.OrdinalIgnoreCase))
                return; // User cancelled replacing the currently open (modified) file.
        }

        var document = _editorPane.Editor.Document;
        if (document is null || entry.LineNumber < 1 || entry.LineNumber > document.LineCount)
            return;

        var line = document.GetLineByNumber(entry.LineNumber);
        _editorPane.Editor.CaretOffset = line.Offset;
        _editorPane.Editor.SetFocus();
    }

    /// <summary>Reloads <see cref="_breakpoints"/> from the active project's breakpoints sidecar
    /// file (see <see cref="TedideProject.ResolvedBreakpointsFile"/>), or resets to an empty set if
    /// no project is loaded. Called everywhere the active project itself changes (open/new/close,
    /// Project Settings save) - not on every build, since breakpoints don't change from a build.</summary>
    private void LoadBreakpointsForActiveProject()
    {
        _breakpoints = _workspace.ActiveProject is { } project
            ? BreakpointsFile.Load(project.ResolvedBreakpointsFile)
            : new BreakpointsFile();
        RefreshBreakpointHighlights();
    }

    /// <summary>
    /// Recomputes <see cref="_breakpointLineTransformer"/>'s highlighted line set from
    /// <see cref="_breakpoints"/>, scoped to whichever file is currently open (a breakpoint in any
    /// other file is irrelevant since only one file is ever open at once - see EditorPane's class
    /// summary). Called whenever either the open file or the breakpoint set itself changes.
    /// </summary>
    private void RefreshBreakpointHighlights()
    {
        _breakpointLineTransformer.BreakpointLines.Clear();
        if (_workspace.ActiveProject is { } project && _editorPane.OpenPath is { } openPath)
        {
            var relativePath = Path.GetRelativePath(project.Directory, openPath).Replace('\\', '/');
            foreach (var breakpoint in _breakpoints.Breakpoints)
                if (breakpoint.Enabled && string.Equals(breakpoint.SourceFile, relativePath, StringComparison.OrdinalIgnoreCase))
                    _breakpointLineTransformer.BreakpointLines.Add(breakpoint.Line);
        }
        _editorPane.Editor.SetNeedsDraw();
    }

    /// <summary>
    /// Toggles a breakpoint on the currently open file's cursor line (F9) - the fallback for
    /// setting breakpoints since Terminal.Gui.Editor's Editor has no clickable gutter (see
    /// <see cref="CurrentDebugLineTransformer"/>'s own doc comment). Saves immediately so it
    /// survives even if the debug session (or Tedide itself) is closed without an explicit save.
    /// </summary>
    private void ToggleBreakpointAtCursor()
    {
        var project = _workspace.ActiveProject;
        if (project is null || _editorPane.OpenPath is not { } openPath || _editorPane.Editor.Document is not { } document)
            return;

        var line = document.GetLineByOffset(_editorPane.Editor.CaretOffset).LineNumber;
        // .dbg file paths are forward-slashed (cl65 was invoked with e.g. "src/main.c") regardless
        // of this being Windows - normalize so breakpoints resolve against DbgFile.FindAddressForSourceLine.
        var relativePath = Path.GetRelativePath(project.Directory, openPath).Replace('\\', '/');

        var existingIndex = _breakpoints.Breakpoints.FindIndex(b =>
            b.Line == line && string.Equals(b.SourceFile, relativePath, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            _breakpoints.Breakpoints.RemoveAt(existingIndex);
            AppendOutputLine($"Breakpoint removed: {relativePath}:{line}");
        }
        else
        {
            _breakpoints.Breakpoints.Add(new BreakpointEntry(relativePath, line));
            AppendOutputLine($"Breakpoint set: {relativePath}:{line}");
        }

        _breakpoints.Save(project.ResolvedBreakpointsFile);
        RefreshBreakpointHighlights();
    }

    private void ShowBreakpointsDialog()
    {
        var project = _workspace.ActiveProject;
        if (project is null)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        var dialog = new BreakpointsDialog(_breakpoints, project.ResolvedBreakpointsFile);
        dialog.BreakpointSelected += breakpoint =>
            OpenSymbol((Path.Combine(project.Directory, breakpoint.SourceFile), breakpoint.Line));
        Application.Run(dialog);
        // The dialog mutates the same _breakpoints instance in place (toggle/delete) - refresh in
        // case it changed anything for the currently open file.
        RefreshBreakpointHighlights();
    }

    /// <summary>
    /// Builds the active project (if needed), launches it in VICE with the binary monitor enabled,
    /// connects <see cref="_debugClient"/>, sets every enabled breakpoint (resolved to an address
    /// via <see cref="_dbgFile"/>), and starts it running. Requires <see cref="TedideProject.GenerateDebugInfo"/>
    /// to be on - without it there's no .dbg file to resolve breakpoints/addresses against.
    /// </summary>
    private async Task StartDebuggingAsync()
    {
        var project = _workspace.ActiveProject;
        if (project is null)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }
        if (!project.GenerateDebugInfo)
        {
            TedideMessageBox.ErrorQuery("Debug Info Required",
                "\"Generate debug info\" is off for this project.\n" +
                "Enable it on the Linker tab of Project Settings, then rebuild before starting a debug session.",
                ["OK"]);
            return;
        }
        if (_isDebugging)
        {
            AppendOutputLine("Already debugging - use Debug > Stop Debugging first.");
            return;
        }

        // Switch to the Debug tab immediately so its status/register panel is what the user sees
        // as the session comes up, rather than whatever tab (Output/Error List/Symbols) happened
        // to be selected before.
        ShowDebugTab();

        var buildResult = await BuildActiveProjectAsync();
        if (buildResult is not { Succeeded: true })
            return;

        if (!File.Exists(project.ResolvedDebugInfoFile))
        {
            AppendOutputLine("Build succeeded but no debug info file was produced.");
            return;
        }
        _dbgFile = DbgFile.Parse(File.ReadAllText(project.ResolvedDebugInfoFile));

        _vice.Launch(project, AppendOutputLine, enableBinaryMonitor: true);

        _debugClient = new ViceMonitorClient();
        _debugClient.CheckpointHit += OnCheckpointHit;
        _debugClient.Resumed += _ => Application.Invoke(() =>
        {
            _isStopped = false;
            _debugLineTransformer.CurrentLineNumber = null;
            _debugPanel.SetStatus("Running...");
            _editorPane.Editor.SetNeedsDraw();
        });

        _debugPanel.SetStatus("Connecting to VICE...");
        var connected = false;
        // VICE needs a moment to start listening on its binary monitor port after the process
        // starts - retry rather than failing on the first attempt.
        for (var attempt = 0; attempt < 20 && !connected; attempt++)
        {
            try
            {
                await _debugClient.ConnectAsync();
                connected = true;
            }
            catch
            {
                await Task.Delay(250);
            }
        }
        if (!connected)
        {
            AppendOutputLine("Could not connect to VICE's binary monitor - is VICE installed and did it launch correctly?");
            await _debugClient.DisposeAsync();
            _debugClient = null;
            _debugPanel.SetStatus("Not debugging.");
            return;
        }

        _isDebugging = true;
        // Read-only for the whole session - editing source while the compiled binary it no longer
        // matches is running would be misleading, and this also guarantees the unsaved-changes
        // prompt in ConfirmReplaceCurrentFile (via OpenFile) can never fire/get cancelled while a
        // breakpoint/step tries to jump to a different file, since no further edits are possible
        // once this is set (the BuildActiveProjectAsync call above already saved everything).
        _editorPane.Editor.ReadOnly = true;

        foreach (var breakpoint in _breakpoints.Breakpoints.Where(b => b.Enabled))
        {
            var address = _dbgFile.FindAddressForSourceLine(breakpoint.SourceFile, breakpoint.Line);
            if (address is { } addr)
                await _debugClient.SetCheckpointAsync((ushort)addr);
            else
                AppendOutputLine($"Could not resolve breakpoint {breakpoint.SourceFile}:{breakpoint.Line} to an address - it may be on a line with no compiled code.");
        }

        _debugPanel.SetStatus("Running...");
        await _debugClient.ContinueAsync();
    }

    /// <summary>
    /// Fires whenever VICE stops at a checkpoint - reads registers, resolves the PC back to a
    /// source location (<see cref="_dbgFile"/>), and jumps the editor there. Runs on
    /// <see cref="ViceMonitorClient"/>'s own background read-loop thread, so every UI touch (and
    /// the nested GetRegistersAsync request/response, which needs that same read loop free to
    /// process it) is marshaled onto the UI thread via Application.Invoke - which posts and returns
    /// immediately rather than blocking the calling thread, so this doesn't deadlock against the
    /// read loop it was raised from.
    /// </summary>
    private void OnCheckpointHit(CheckpointHitEventArgs args)
    {
        Application.Invoke(async () =>
        {
            try
            {
                _isStopped = true;
                if (_debugClient is null)
                    return;

                var registers = await _debugClient.GetRegistersAsync();
                _debugPanel.SetRegisters(registers);

                // "PC" is VICE's register name for the 6502 program counter on the main memspace -
                // confirmed for real against a live VICE 3.9 instance during implementation (its
                // ids are assigned dynamically per the binary monitor protocol docs, but this name
                // was stable), not just inferred from community tooling.
                var project = _workspace.ActiveProject;
                var pc = registers["PC"];
                if (project is not null && pc is { } pcValue && _dbgFile?.FindSourceLocationForAddress(pcValue) is { } location)
                {
                    OpenSymbol((Path.Combine(project.Directory, location.FilePath), location.Line));
                    _debugLineTransformer.CurrentLineNumber = location.Line;
                    _debugPanel.SetStatus($"Stopped at {location.FilePath}:{location.Line} (checkpoint #{args.Checkpoint.Number})");
                }
                else
                {
                    _debugLineTransformer.CurrentLineNumber = null;
                    _debugPanel.SetStatus(pc is { } pcv ? $"Stopped at ${pcv:X4} (checkpoint #{args.Checkpoint.Number})" : "Stopped.");
                }
                _editorPane.Editor.SetNeedsDraw();
            }
            catch (Exception ex)
            {
                AppendOutputLine($"Error handling checkpoint hit: {ex.Message}");
            }
        });
    }

    private async Task ContinueDebuggingAsync()
    {
        if (_debugClient is null || !_isDebugging)
            return;

        _isStopped = false;
        _debugLineTransformer.CurrentLineNumber = null;
        _debugPanel.SetStatus("Running...");
        _editorPane.Editor.SetNeedsDraw();
        await _debugClient.ContinueAsync();
    }

    /// <summary>
    /// Steps by source line, not raw instruction: single-steps repeatedly until the resolved
    /// location changes from where it started (or the step count safety cap is hit - an address
    /// with no line mapping, e.g. inside a library routine with no debug info, would otherwise
    /// single-step forever).
    /// </summary>
    private async Task StepDebuggingAsync()
    {
        if (_debugClient is null || _dbgFile is null || !_isDebugging || !_isStopped)
            return;

        var startRegisters = await _debugClient.GetRegistersAsync();
        var startLocation = startRegisters["PC"] is { } startPc ? _dbgFile.FindSourceLocationForAddress(startPc) : null;

        for (var i = 0; i < 500; i++)
        {
            await _debugClient.StepAsync();
            var registers = await _debugClient.GetRegistersAsync();
            if (registers["PC"] is not { } pc)
                break;

            var location = _dbgFile.FindSourceLocationForAddress(pc);
            if (location is null || location != startLocation)
            {
                _debugPanel.SetRegisters(registers);
                var project = _workspace.ActiveProject;
                if (project is not null && location is { } loc)
                {
                    OpenSymbol((Path.Combine(project.Directory, loc.FilePath), loc.Line));
                    _debugLineTransformer.CurrentLineNumber = loc.Line;
                    _debugPanel.SetStatus($"Stopped at {loc.FilePath}:{loc.Line}");
                }
                else
                {
                    _debugLineTransformer.CurrentLineNumber = null;
                    _debugPanel.SetStatus($"Stopped at ${pc:X4}");
                }
                _editorPane.Editor.SetNeedsDraw();
                return;
            }
        }
    }

    private async Task StopDebuggingAsync()
    {
        if (_debugClient is null)
            return;

        try { await _debugClient.ContinueAsync(); }
        catch { /* VICE may already be gone - fine, we're tearing down the connection either way. */ }

        await _debugClient.DisposeAsync();
        _debugClient = null;
        _dbgFile = null;
        _isDebugging = false;
        _isStopped = false;
        _debugLineTransformer.CurrentLineNumber = null;
        _debugPanel.SetStatus("Not debugging.");
        _debugPanel.SetRegisters(null);
        // Only if a file is actually open - EditorPane itself keeps ReadOnly true with nothing
        // open (see its constructor), and this shouldn't override that.
        if (_editorPane.OpenPath is not null)
            _editorPane.Editor.ReadOnly = false;
        _editorPane.Editor.SetNeedsDraw();
    }

    private void CloseActiveFile()
    {
        if (_editorPane.OpenPath is null || !ConfirmReplaceCurrentFile())
            return;

        _editorPane.Close();
        _editorFrame.Title = NoFileOpenTitle;
        UpdateLanguageIndicator();
        RefreshBreakpointHighlights();
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
        _symbolPanel.Refresh(_workspace.ActiveProject);
        LoadBreakpointsForActiveProject();
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

        var choice = TedideMessageBox.Query("Unsaved Changes",
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
        _symbolPanel.Refresh(project);

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
    /// which the dialog may have just changed. If the name changed, also renames the project's
    /// folder (and its .tproj file) on disk to match - see <see cref="RenameProjectFolder"/>.
    /// </summary>
    private void ShowProjectSettings()
    {
        var project = _workspace.ActiveProject;
        if (project is null)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        var oldName = project.Name;
        var oldFilePath = project.FilePath;
        var oldDirectory = project.Directory;

        var dialog = new ProjectSettingsDialog(project);
        Application.Run(dialog);
        if (!dialog.Saved)
            return;

        // The dialog's "CC65"/"VICE" tabs already persisted to ToolchainSettings on Save (they're
        // not project state - see ProjectSettingsDialog) - reload and apply immediately so a
        // changed value takes effect without restarting Tedide. Unlike the startup call in the
        // constructor, an explicit Save is allowed to unset CC65_HOME.
        ApplyToolchainSettings(ToolchainSettings.Load(), allowUnsettingCc65Home: true);

        if (oldFilePath is not null && !string.Equals(project.Name, oldName, StringComparison.Ordinal))
            RenameProjectFolder(project, oldFilePath, oldDirectory);

        _solutionExplorer.Rebuild(_workspace);
        _symbolPanel.Refresh(_workspace.ActiveProject);
        LoadBreakpointsForActiveProject();
    }

    /// <summary>
    /// Applies <see cref="ToolchainSettings"/> to this running instance: <see cref="_vice"/>'s
    /// BinDirectory always follows the setting (falling back to <see cref="ViceEmulator.DefaultBinDirectory"/>
    /// when unset - it has no ambient equivalent to preserve), while CC65_HOME is only ever set, never
    /// cleared, unless <paramref name="allowUnsettingCc65Home"/> is true - see call sites for why.
    /// </summary>
    private void ApplyToolchainSettings(ToolchainSettings settings, bool allowUnsettingCc65Home)
    {
        if (!string.IsNullOrWhiteSpace(settings.Cc65Home))
            Environment.SetEnvironmentVariable("CC65_HOME", settings.Cc65Home);
        else if (allowUnsettingCc65Home)
            Environment.SetEnvironmentVariable("CC65_HOME", null);

        _vice.BinDirectory = string.IsNullOrWhiteSpace(settings.ViceBinDirectory)
            ? ViceEmulator.DefaultBinDirectory
            : settings.ViceBinDirectory;
    }

    /// <summary>
    /// Renames a project's own directory (and its .tproj file) to match a name change just saved
    /// by <see cref="ProjectSettingsDialog"/> - e.g. renaming "HelloGame" to "SuperGame" moves
    /// .../HelloGame/ to .../SuperGame/ and HelloGame.tproj to SuperGame.tproj within it, following
    /// the same "folder named after the project" convention <see cref="Workspace.NewProject"/>
    /// scaffolds. The dialog has already written the new name into the .tproj at its old location
    /// by the time this runs, so the move carries the up-to-date content with it - no re-save
    /// needed. Also repoints the owning solution's ProjectPaths entry, and the editor's open file
    /// if it was inside this project, since neither follows a directory move on its own.
    /// </summary>
    private void RenameProjectFolder(TedideProject project, string oldFilePath, string oldDirectory)
    {
        var parentDirectory = Path.GetDirectoryName(oldDirectory);
        if (parentDirectory is null)
            return;

        var newDirectory = Path.Combine(parentDirectory, project.Name);
        if (Directory.Exists(newDirectory))
        {
            TedideMessageBox.ErrorQuery("Directory Exists",
                $"Cannot rename the project folder to '{project.Name}' - '{newDirectory}' already exists.\n" +
                "The project's name was saved, but its folder was left as-is.", ["OK"]);
            return;
        }

        // The currently open file won't follow a Directory.Move on its own - EditorPane.OpenPath
        // is just a string. If it's inside this project, confirm/save unsaved changes now (while
        // the old path is still valid) and remember it relative to the project root so it can be
        // reopened from the new location afterward.
        string? relativeOpenPath = null;
        if (_editorPane.OpenPath is { } openPath &&
            openPath.StartsWith(oldDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            if (!ConfirmReplaceCurrentFile())
            {
                TedideMessageBox.ErrorQuery("Rename Cancelled",
                    "The project's name was saved, but its folder was not renamed because the open file has unsaved changes.", ["OK"]);
                return;
            }

            relativeOpenPath = Path.GetRelativePath(oldDirectory, openPath);
        }

        try
        {
            Directory.Move(oldDirectory, newDirectory);

            var movedFilePath = Path.Combine(newDirectory, Path.GetFileName(oldFilePath));
            var newFilePath = Path.Combine(newDirectory, project.Name + TedideProject.FileExtension);
            if (!string.Equals(movedFilePath, newFilePath, StringComparison.OrdinalIgnoreCase))
                File.Move(movedFilePath, newFilePath);

            project.FilePath = newFilePath;
        }
        catch (IOException ex)
        {
            TedideMessageBox.ErrorQuery("Rename Failed",
                $"The project's name was saved, but its folder could not be renamed: {ex.Message}", ["OK"]);
            return;
        }

        if (_workspace.Solution is { } solution)
        {
            var oldRelative = Path.GetRelativePath(solution.Directory, oldFilePath).Replace('\\', '/');
            var index = solution.ProjectPaths.FindIndex(p => string.Equals(p, oldRelative, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                solution.ProjectPaths[index] = Path.GetRelativePath(solution.Directory, project.FilePath).Replace('\\', '/');
                solution.Save();
            }
        }

        if (relativeOpenPath is not null)
        {
            var newOpenPath = Path.Combine(newDirectory, relativeOpenPath);
            _editorPane.Open(newOpenPath);
            _editorFrame.Title = Path.GetFileName(newOpenPath);
            UpdateLanguageIndicator();
            RefreshBreakpointHighlights();
        }
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

    /// <summary>
    /// Switches the Output/Error List pane to its "Debug" tab and gives the debug panel input
    /// focus - used when a debug session starts, so its status/register panel is immediately
    /// visible rather than left behind whatever tab the user had last selected. Focus moves back
    /// to the editor as soon as execution actually stops somewhere (see <see cref="OpenSymbol"/>,
    /// called from <see cref="OnCheckpointHit"/>/<see cref="StepDebuggingAsync"/>), so this is only
    /// the very first thing the user sees while the session is coming up.
    /// </summary>
    private void ShowDebugTab()
    {
        _outputTabs.Value = _debugTab;
        _debugPanel.SetFocus();
    }
}
