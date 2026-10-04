using System.Runtime.CompilerServices;
using System.Text.Json;
using Serilog;
using Tedide.Theming;
using Tedide.App.Views;
using Tedide.Build;
using Tedide.Core;
using Tedide.Core.Navigation;
using Tedide.Git;
using Terminal.Gui.App;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
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
public sealed class AppShell : Window, IDebugSessionHost, IShell
{
    private readonly Workspace _workspace = new();

    /// <summary>Git status for the Solution Explorer, the status bar and the Git tab - see <see cref="GitTracker"/>.</summary>
    private readonly GitTracker _git;
    private readonly GitChangesView _gitView = new();
    private View _gitTab = null!;



    // Not inline-initialized (unlike its siblings below) - its BinDirectory depends on
    // ToolchainSettings, applied in the constructor via ApplyToolchainSettings (also reapplied
    // after every ProjectSettingsDialog save - see ShowProjectSettings).
    private readonly ViceEmulator _vice = new();
    private readonly RecentProjectsSettings _recentProjects = RecentProjectsSettings.Load();

    /// <summary>Navigate Backward/Forward (Alt+Left/Alt+Right) - see <see cref="NavigationCommands.RecordJump"/>.</summary>
    private readonly NavigationHistory _navigationHistory = new();
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
    private readonly ReferencesView _referencesView = new();
    private readonly MemoryView _memoryView = new();
    private readonly DisassemblyView _disassemblyView = new();
    private readonly DebugPanelView _debugPanel = new();
    private readonly CurrentDebugLineTransformer _debugLineTransformer = new();
    private readonly BreakpointLineTransformer _breakpointLineTransformer = new();
    private SessionStateFile _sessionState = new();
    /// <summary>Breakpoints and the VICE debug session - see <see cref="DebugSession"/>.</summary>
    private readonly DebugSession _debug;
    /// <summary>Find, Go To, references, rename and Navigate Backward/Forward - see <see cref="NavigationCommands"/>.</summary>
    private readonly NavigationCommands _navigation;
    /// <summary>Build, Run and Clean - see <see cref="BuildCommands"/>.</summary>
    private readonly BuildCommands _build;
    /// <summary>The Git tab, blame, change bars, Compare and Blame - see <see cref="GitIntegration"/>.</summary>
    private readonly GitIntegration _gitIntegration;
    private Tabs _outputTabs = null!;
    private View _outputTab = null!;
    private View _debugTab = null!;
    private View _referencesTab = null!;
    private readonly EditorMenuBar _menuBar;
    private readonly EditorStatusBar _statusBar;

    public AppShell()
    {
        _git = new GitTracker(
            () => (_workspace.Projects.Select(p => p.Directory).ToList(), _workspace.Solution?.Directory ?? _workspace.ActiveProject?.Directory),
            OnUiThread);
        // The title carries the debug state ("Stopped in detect_system at ..."): Terminal.Gui reads
        // its "_" as a hotkey marker, dropping it, and crashed in BorderView.TryUpdateTerminalTitle
        // when the next, shorter title came in with the old hotkey's position.
        HotKeySpecifier = TerminalGuiWorkarounds.NoHotKey;
        Title = AppTitle;
        _debug = new DebugSession(this, _workspace, _editorPane, _debugPanel, _disassemblyView, _memoryView, _breakpointLineTransformer);
        _navigation = new NavigationCommands(this, _workspace, _editorPane, _navigationHistory, _referencesView);
        _build = new BuildCommands(this, _workspace, _navigation, _vice, _outputView, _errorListView, _solutionExplorer, _symbolPanel);
        _gitIntegration = new GitIntegration(this, _workspace, _editorPane, _solutionExplorer, _navigation, _git, _gitView);
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
        _solutionExplorer.AddNewProjectRequested += AddNewProject;
        _solutionExplorer.AddExistingProjectRequested += AddExistingProject;
        _solutionExplorer.BuildSolutionRequested += () => Fire(_build.BuildSolutionAsync());
        _solutionExplorer.CleanSolutionRequested += _build.CleanSolution;
        _solutionExplorer.SetStartupProjectRequested += SetStartupProject;
        _solutionExplorer.BuildProjectRequested += project => Fire(_build.BuildProjectsAsync([project]));
        _solutionExplorer.CleanProjectRequested += project => _build.CleanProjects([project]);
        _solutionExplorer.ProjectSettingsRequested += ShowProjectSettings;
        _solutionExplorer.RemoveProjectRequested += RemoveProject;
        _solutionExplorer.DeleteProjectRequested += DeleteProject;
        explorerFrame.Add(_solutionExplorer);

        _editorFrame = new FrameView
        {
            Title = NoFileOpenTitle,
            // The title is the open file's name, and a title reads its first "_" as a hotkey
            // marker - "sound_fx.c" would show as "soundfx.c".
            HotKeySpecifier = TerminalGuiWorkarounds.NoHotKey,
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
        _editorPane.FindInFilesRequested += _navigation.ShowFindInFiles;
        _editorPane.GoToDefinitionRequested += _navigation.GoToDefinition;
        _editorPane.FindReferencesRequested += _navigation.FindAllReferences;
        _editorPane.RenameSymbolRequested += _navigation.RenameSymbol;
        _editorPane.CompareWithHeadRequested += _gitIntegration.CompareActiveWithHead;
        _editorPane.FileHistoryRequested += () =>
        {
            if (_editorPane.OpenPath is { } path)
                Fire(_gitIntegration.ShowHistoryAsync(path));
        };
        _editorPane.BlameRequested += () =>
        {
            if (_editorPane.OpenPath is { } path)
                Fire(_gitIntegration.ShowBlameAsync(path));
        };
        _editorPane.ActiveDocumentChanged += OnActiveDocumentChanged;
        _editorPane.CloseRequested += path => CloseFile(path);
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
        _errorListView.DiagnosticActivated += _navigation.OpenDiagnostic;
        errorListTab.Add(_errorListView);

        var symbolsTab = new View { Title = " _Symbols ", Width = Dim.Fill(), Height = Dim.Fill() };
        _symbolPanel.Width = Dim.Fill();
        _symbolPanel.Height = Dim.Fill();
        _symbolPanel.LineActivated += entry =>
        {
            _navigation.RecordJump();
            _navigation.OpenSymbol(entry);
        };
        symbolsTab.Add(_symbolPanel);

        _referencesTab = new View { Title = " _References ", Width = Dim.Fill(), Height = Dim.Fill() };
        _referencesView.Width = Dim.Fill();
        _referencesView.Height = Dim.Fill();
        _referencesView.ReferenceActivated += reference => _navigation.NavigateTo(reference.FilePath, reference.Line, reference.Column, reference.Length);
        _referencesTab.Add(_referencesView);

        _debugTab = new View { Title = " _Debug ", Width = Dim.Fill(), Height = Dim.Fill() };
        _debugPanel.Width = Dim.Fill();
        _debugPanel.Height = Dim.Fill();
        _debugTab.Add(_debugPanel);

        _outputTabs.Add(_outputTab);
        _outputTabs.Add(errorListTab);
        _outputTabs.Add(symbolsTab);
        _outputTabs.Add(_referencesTab);
        _outputTabs.Add(_debugTab);

        var memoryTab = new View { Title = " _Memory ", Width = Dim.Fill(), Height = Dim.Fill() };
        _memoryView.Width = Dim.Fill();
        _memoryView.Height = Dim.Fill();
        _memoryView.AddressRequested += _debug.ShowMemoryAt;
        _memoryView.PageRequested += delta =>
        {
            if (_debug.MemoryAddress is { } address)
                _debug.ShowMemoryAt($"${Math.Clamp(address + delta, 0, 0x10000 - MemoryView.ByteCount):X4}");
        };
        memoryTab.Add(_memoryView);
        _outputTabs.Add(memoryTab);

        var disassemblyTab = new View { Title = " Dis_assembly ", Width = Dim.Fill(), Height = Dim.Fill() };
        _disassemblyView.Width = Dim.Fill();
        _disassemblyView.Height = Dim.Fill();
        _disassemblyView.SourceRequested += _debug.OpenSourceForAddress;
        disassemblyTab.Add(_disassemblyView);
        _outputTabs.Add(disassemblyTab);

        _gitTab = new View { Title = " _Git ", Width = Dim.Fill(), Height = Dim.Fill() };
        _gitView.Width = Dim.Fill();
        _gitView.Height = Dim.Fill();
        _gitTab.Add(_gitView);
        _outputTabs.Add(_gitTab);

        _debugPanel.FrameActivated += frame => _navigation.OpenSymbol((frame.FilePath!, frame.Line));
        _debugPanel.BreakpointActivated += breakpoint =>
        {
            if (_workspace.ActiveProject is { } project)
                _navigation.OpenSymbol((Path.Combine(project.Directory, breakpoint.SourceFile), breakpoint.Line));
        };

        // Breakpoint highlighting registered before the current-debug-line one, so the latter's
        // Accent color wins on a line that's both a breakpoint and the paused line - see
        // BreakpointLineTransformer's own doc comment for why order matters here.
        _editorPane.Editor.LineTransformers.Add(_breakpointLineTransformer);
        // Highlights the source line execution is stopped at during a debug session - see its own
        // doc comment for why LineTransformers (not BackgroundRenderers) is the right extension
        // point for this.
        _editorPane.Editor.LineTransformers.Add(_debugLineTransformer);

        Add([_menuBar, explorerFrame, _editorFrame, _outputTabs, _statusBar]);

        _workspace.Changed += () => _git.ProjectsChanged();
        _solutionExplorer.Rebuilt += () => _git.RequestRefresh();
        _git.Start();
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
        LogOnlyOnExit("Saving layout settings", _layoutSettings.Save);
    }

    /// <summary>Records the active project's currently open file (see
    /// <see cref="SaveLastOpenFileForActiveProject"/>) so it's reopened next time this same
    /// project loads (see <see cref="LoadLastOpenFileForActiveProject"/>). Called once, from
    /// Program.cs, right after <c>Application.Run(shell)</c> returns - i.e. when the user quits -
    /// alongside <see cref="SaveLayoutSettings"/>.</summary>
    public void SaveSessionState() => LogOnlyOnExit("Saving the session", SaveLastOpenFileForActiveProjectCore);

    /// <summary>The on-exit counterpart to <see cref="Guard"/>: the main window has already closed,
    /// so there's nowhere sensible to show an error box - a failure is only logged, rather than
    /// crashing Tedide on its way out.</summary>
    private static void LogOnlyOnExit(string action, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            Log.Error(ex, "{Action} failed while exiting", action);
        }
    }

    private EditorMenuBar BuildMenuBar()
    {
        var menuBar = new EditorMenuBar(_editorPane.Editor);
        // MenuBar's own default "activate the menu bar" key is F10 (confirmed in Terminal.Gui's
        // own source, MenuBar.cs), which silently wins over Debug > Step's F10 - this isn't a
        // hotkey-binding-priority conflict, it's the MenuBar's constructor directly registering
        // `HotKeyBindings.Add(Key, Command.HotKey)` for whatever DefaultKey was at construction
        // time. Setting the instance Key property afterward doesn't re-sync that binding (tried
        // it - no effect, the setter only raises an event), and setting the *static* DefaultKey to
        // Key.Empty beforehand instead makes the constructor itself throw ("Invalid newEventArgs" -
        // HotKeyBindings.Add rejects an empty key outright, confirmed via a real crash caught in
        // the log). Removing the already-registered binding directly is the only approach that
        // actually works. Every menu is already reachable via Alt+<mnemonic> (confirmed throughout
        // this app's own testing), so freeing F10 here costs no discoverability.
        menuBar.HotKeyBindings.Remove(Key.F10);

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
            new MenuItem("_Save", "", () => SaveActive(), Key.S.WithCtrl),
            // No key: Ctrl+Shift+S arrives as Ctrl+S in Windows Terminal (see FindInFilesKey).
            new MenuItem("Save A_ll", "", () => SaveAll(), Key.Empty),
            // Ctrl+W as in VS Code; Visual Studio's own Ctrl+F4 works too (OnKeyDown).
            new MenuItem("_Close File", "", CloseActiveFile, Key.W.WithCtrl) { BindKeyToApplication = true },
            new MenuItem("Close A_ll Files", "", CloseAllFiles, Key.Empty),
            // The keys are labels here - the editor and OnKeyDown handle them (see EditorPane).
            new MenuItem("_Next File", "", () => _editorPane.CycleDocument(1), Key.PageDown.WithCtrl),
            new MenuItem("Pre_vious File", "", () => _editorPane.CycleDocument(-1), Key.PageUp.WithCtrl),
            new Line(),
            // The key is a label: OnKeyDown handles it, not a status-bar Shortcut, which left no
            // room for the rest (and BindKeyToApplication didn't fire for it - confirmed live).
            new MenuItem("_Quit", "", Quit, Key.Q.WithCtrl),
        });

        var buildMenu = new MenuBarItem("_Build", new List<MenuItem>
        {
            new("Build _Solution", "", () => Fire(_build.BuildSolutionAsync()), BuildSolutionKey),
            new("_Build Project", "", () => Fire(_build.BuildActiveProjectAsync()), Key.Empty),
            new("C_ancel Build", "", _build.CancelBuild, Key.Empty),
            new("_Clean Project", "", _build.CleanActiveProject, Key.Empty),
            new("Clea_n Solution", "", _build.CleanSolution, Key.Empty),
        });

        // Visual Studio's keys, except Step Into (below). The keys shown here are only labels:
        // F5/Ctrl+F5/F10/F7/F9 fire via their status-bar Shortcuts and the rest via OnKeyDown,
        // because BindKeyToApplication never fires for this menu's items (confirmed live - see
        // OnKeyDown). Adding it back would risk double-firing if it ever starts working.
        var debugMenu = new MenuBarItem("_Debug", new List<View>
        {
            new MenuItem("_Windows", "", new Menu(BuildDebugWindowsMenuItems())),
            new Line(),
            new MenuItem("_Start Debugging", "", () => Fire(_debug.StartDebuggingAsync()), Key.F5),
            new MenuItem("Start Wit_hout Debugging", "", () => Fire(_build.RunActiveProjectAsync()), Key.F5.WithCtrl),
            new MenuItem("_Continue", "", () => Fire(_debug.ContinueDebuggingAsync()), Key.F5),
            new MenuItem("Step _Over", "", () => Fire(_debug.StepDebuggingAsync(stepInto: false)), Key.F10),
            // F7, not Visual Studio's F11 - Windows Terminal claims F11 for its own full-screen
            // toggle before the app ever sees it. F7/F8 is also the Turbo Pascal/Borland pairing.
            new MenuItem("Step _Into", "", () => Fire(_debug.StepDebuggingAsync(stepInto: true)), Key.F7),
            new MenuItem("Sto_p Debugging", "", () => Fire(_debug.StopDebuggingAsync()), Key.F5.WithShift),
            new Line(),
            new MenuItem("_Toggle Breakpoint", "", _debug.ToggleBreakpointAtCursor, Key.F9),
            new MenuItem("_Enable/Disable Breakpoint", "", _debug.EnableBreakpointAtCursor, Key.F9.WithCtrl),
            new MenuItem("Breakpoint Co_ndition...", "", _debug.EditBreakpointConditionAtCursor, Key.Empty),
            new MenuItem("_Breakpoints...", "", _debug.ShowBreakpointsDialog, Key.Empty),
            new Line(),
            new MenuItem("Add W_atch...", "", _debug.ShowAddWatchDialog, Key.Empty),
            new MenuItem("C_lear Watches", "", _debug.ClearWatches, Key.Empty),
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
            new("_Context Help", "", _navigation.ShowContextHelp, ContextHelpKey),
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
        editMenuItems.AddAt(0, new MenuItem("_Find in Files...", "", () => _navigation.ShowFindInFiles(), FindInFilesKey));
        editMenuItems.AddAt(1, new MenuItem("_Go To Line...", "", _navigation.ShowGoToLine, Key.G.WithCtrl));
        editMenuItems.AddAt(2, new MenuItem("Go To _Definition", "", _navigation.GoToDefinition, GoToDefinitionKey));
        editMenuItems.AddAt(3, new MenuItem("Find All _References", "", _navigation.FindAllReferences, FindReferencesKey));
        editMenuItems.AddAt(4, new MenuItem("Re_name Symbol...", "", _navigation.RenameSymbol, RenameSymbolKey));
        editMenuItems.AddAt(5, new MenuItem("Navigate _Backward", "", _navigation.NavigateBackward, NavigateBackwardKey).WithKeyText("Alt+Left"));
        editMenuItems.AddAt(6, new MenuItem("Navigate For_ward", "", _navigation.NavigateForward, NavigateForwardKey).WithKeyText("Alt+Right"));
        var viewMenuItems = menuBar.ViewMenu.PopoverMenu!.Root!;
        viewMenuItems.AddAt(0, new MenuItem("_Solution Explorer", "", ShowSolutionExplorer, SolutionExplorerKey));
        viewMenuItems.AddAt(1, new MenuItem("_Output", "", ShowOutputTab, Key.Empty));
        viewMenuItems.AddAt(2, new MenuItem("_Error List", "", () => ShowPane(_errorListView), Key.Empty));
        viewMenuItems.AddAt(3, new MenuItem("_Git Changes", "", () => ShowPane(_gitView), Key.Empty));
        viewMenuItems.AddAt(4, new Line());
        menuBar.Menus = [fileMenu, menuBar.EditMenu, menuBar.ViewMenu, buildMenu, debugMenu, projectMenu, themeMenu, helpMenu];
        menuBar.X = 0;
        menuBar.Y = 0;
        menuBar.Width = Dim.Fill();
        return menuBar;
    }

    /// <summary>
    /// Find in Files' key. Not Visual Studio's Ctrl+Shift+F: Windows Terminal claims that for its
    /// own Find bar before the app sees it, and more generally a Ctrl+Shift+letter arrives with
    /// the Shift dropped (Ctrl+Shift+G reads as Ctrl+G, Ctrl+Shift+H as Ctrl+Backspace) - both
    /// confirmed live. Alt+Shift+F arrives intact.
    /// </summary>
    private static readonly Key FindInFilesKey = Key.F.WithAlt.WithShift;

    /// <summary>Visual Studio's own keys for Go To Definition and Find All References.</summary>
    private static readonly Key GoToDefinitionKey = Key.F12;
    private static readonly Key FindReferencesKey = Key.F12.WithShift;

    /// <summary>F2, as in VS Code - Visual Studio's own Ctrl+R, Ctrl+R is a two-key chord.</summary>
    private static readonly Key RenameSymbolKey = Key.F2;

    /// <summary>Not Visual Studio's Ctrl+- and Ctrl+Shift+-: Windows Terminal takes those for its
    /// font size. Alt+Left/Alt+Right are VS Code's, and the Doc Viewer's own Back/Forward.</summary>
    private static readonly Key NavigateBackwardKey = Key.CursorLeft.WithAlt;
    private static readonly Key NavigateForwardKey = Key.CursorRight.WithAlt;

    /// <summary>F1, as in Visual Studio - see <see cref="NavigationCommands.ShowContextHelp"/>.</summary>
    private static readonly Key ContextHelpKey = Key.F1;

    /// <summary>Build Solution: Visual Studio's Ctrl+Shift+B arrives as Ctrl+B in Windows Terminal
    /// (see <see cref="FindInFilesKey"/>), so Ctrl+B it is - and pressing Ctrl+Shift+B still works.</summary>
    private static readonly Key BuildSolutionKey = Key.B.WithCtrl;

    /// <summary>Visual Studio's keys for its tool windows. Where VS uses a two-key chord (Locals is
    /// Ctrl+Alt+V, L; Watch is Ctrl+Alt+W, 1; Memory is Ctrl+Alt+M, 1) the first key alone opens it.
    /// Not VS's Ctrl+Alt+O for Output: Ctrl+Alt is AltGr, and on a UK keyboard AltGr+O types "ó"
    /// (confirmed live) - the same goes for the other vowels, which none of these use.</summary>
    private static readonly Key SolutionExplorerKey = Key.L.WithCtrl.WithAlt;
    private static readonly Key CallStackKey = Key.C.WithCtrl.WithAlt;
    private static readonly Key BreakpointsWindowKey = Key.B.WithCtrl.WithAlt;
    private static readonly Key RegistersKey = Key.G.WithCtrl.WithAlt;
    private static readonly Key LocalsKey = Key.V.WithCtrl.WithAlt;
    private static readonly Key WatchKey = Key.W.WithCtrl.WithAlt;
    private static readonly Key MemoryKey = Key.M.WithCtrl.WithAlt;
    private static readonly Key DisassemblyKey = Key.D.WithCtrl.WithAlt;

    private List<MenuItem> BuildDebugWindowsMenuItems() =>
    [
        new("_Locals", "", () => ShowDebugWindow(DebugWindow.Locals), LocalsKey),
        new("_Watch", "", () => ShowDebugWindow(DebugWindow.Watch), WatchKey),
        new("_Call Stack", "", () => ShowDebugWindow(DebugWindow.CallStack), CallStackKey),
        new("_Breakpoints", "", () => ShowDebugWindow(DebugWindow.Breakpoints), BreakpointsWindowKey),
        new("_Registers", "", () => ShowDebugWindow(DebugWindow.Registers), RegistersKey),
        new("_Memory", "", () => ShowPane(_memoryView), MemoryKey),
        new("_Disassembly", "", () => ShowPane(_disassemblyView), DisassemblyKey),
    ];

    /// <summary>
    /// App-wide keys that have no status-bar Shortcut to carry them. A key reaches this only after
    /// the focused view declines it, and never while a dialog is open (a dialog is its own
    /// top-level runnable), so these can't fire behind a modal. They're handled here rather than
    /// via MenuItem.BindKeyToApplication, which - confirmed live against 2.4.17 - never fires for
    /// the Debug menu's items even though the keys do arrive (Shift+F5 and Ctrl+F5 did nothing,
    /// while File menu's Ctrl+N worked). Its app-level registration goes through a null-conditional
    /// App?.Keyboard, so it's silently skipped for a menu item that has no App yet when it runs.
    /// </summary>
    protected override bool OnKeyDown(Key key)
    {
        Action? action = null;
        if (key == Key.F5.WithShift)
            action = () => Fire(_debug.StopDebuggingAsync());
        else if (key == Key.F9.WithCtrl)
            action = _debug.EnableBreakpointAtCursor;
        else if (key == Key.F4.WithCtrl)
            action = CloseActiveFile;
        else if (key == Key.Q.WithCtrl)
            action = Quit;
        else if (key == SolutionExplorerKey)
            action = ShowSolutionExplorer;
        else if (key == LocalsKey)
            action = () => ShowDebugWindow(DebugWindow.Locals);
        else if (key == WatchKey)
            action = () => ShowDebugWindow(DebugWindow.Watch);
        else if (key == CallStackKey)
            action = () => ShowDebugWindow(DebugWindow.CallStack);
        else if (key == BreakpointsWindowKey)
            action = () => ShowDebugWindow(DebugWindow.Breakpoints);
        else if (key == RegistersKey)
            action = () => ShowDebugWindow(DebugWindow.Registers);
        else if (key == MemoryKey)
            action = () => ShowPane(_memoryView);
        else if (key == DisassemblyKey)
            action = () => ShowPane(_disassemblyView);
        else if (key == FindInFilesKey)
            action = () => _navigation.ShowFindInFiles();
        else if (key == GoToDefinitionKey)
            action = _navigation.GoToDefinition;
        else if (key == FindReferencesKey)
            action = _navigation.FindAllReferences;
        else if (key == RenameSymbolKey)
            action = _navigation.RenameSymbol;
        else if (key == NavigateBackwardKey)
            action = _navigation.NavigateBackward;
        else if (key == NavigateForwardKey)
            action = _navigation.NavigateForward;
        else if (key == ContextHelpKey)
            action = _navigation.ShowContextHelp;
        else if (key == Key.PageDown.WithCtrl)
            action = () => _editorPane.CycleDocument(1);
        else if (key == Key.PageUp.WithCtrl)
            action = () => _editorPane.CycleDocument(-1);

        if (action is null)
            return base.OnKeyDown(key);

        // Run on the next main-loop pass, not inside this key event: opening a modal from within
        // the keypress that's still being dispatched left Find in Files' search field without
        // focus - typing went nowhere (confirmed live; the same dialog opened from the Edit menu
        // was fine). Start Debugging can open a message box too, so all three are deferred.
        Application.AddTimeout(TimeSpan.Zero, () =>
        {
            action();
            return false;
        });
        return true;
    }

    private EditorStatusBar BuildStatusBar()
    {
        var statusBar = new EditorStatusBar(_editorPane.Editor);
        // ThemeDropDown drives Terminal.Gui's own ConfigurationManager-based ThemeManager, separate
        // from our own SchemeManager-based ThemeSwitcher (see Tedide.Theming/ThemeSwitcher.cs) - leaving
        // both active would let this dropdown silently overwrite our custom Schemes. Hide it; the
        // Theme menu above is our one theme switcher.
        statusBar.ThemeDropDown.Visible = false;
        // Visual Studio's keys: F5 starts debugging, or continues once stopped; Ctrl+F5 runs
        // without the debugger; Ctrl+B (VS's Ctrl+Shift+B - see BuildSolutionKey) builds.
        statusBar.Add(new Shortcut(Key.F5, "Debug", () => Fire(_debug.StartOrContinueDebuggingAsync())));
        statusBar.Add(new Shortcut(Key.F5.WithCtrl, "Run", () => Fire(_build.RunActiveProjectAsync())));
        statusBar.Add(new Shortcut(BuildSolutionKey, "Build", () => Fire(_build.BuildSolutionAsync())));
        // A plain MenuItem's Key only acts as a hotkey while its menu is already open - a Shortcut
        // is what actually makes a key global. Ctrl+S Save had that gap (menu-only, never worked
        // while the editor had focus) until it was reported and fixed here alongside F9/Ctrl+G.
        statusBar.Add(new Shortcut(Key.F9, "Breakpoint", _debug.ToggleBreakpointAtCursor));
        // These status-bar Shortcuts are what make F10/F7 work at all: the Debug menu's own items
        // show the keys but don't bind them, because BindKeyToApplication never fires for that
        // menu's items (see OnKeyDown for the confirmed cause).
        statusBar.Add(new Shortcut(Key.F10, "Step", () => Fire(_debug.StepDebuggingAsync(stepInto: false))));
        statusBar.Add(new Shortcut(Key.F7, "Into", () => Fire(_debug.StepDebuggingAsync(stepInto: true))));
        statusBar.Add(new Shortcut(Key.S.WithCtrl, "Save", () => SaveActive()));
        statusBar.Add(new Shortcut(Key.G.WithCtrl, "Go To", _navigation.ShowGoToLine));
        statusBar.X = 0;
        statusBar.Y = Pos.AnchorEnd(1);
        statusBar.Width = Dim.Fill();
        return statusBar;
    }

    private void NewProject() => Guard("Creating the project", () => NewProjectCore());

    private void NewProjectCore()
    {
        var dialog = new NewProjectDialog();
        Application.Run(dialog);
        if (dialog.Target is { } target && !string.IsNullOrWhiteSpace(dialog.ProjectName))
        {
            if (!ConfirmCloseFiles(_editorPane.OpenPaths))
                return;
            SaveLastOpenFileForActiveProject();
            _editorPane.CloseAll();
            _workspace.NewProject(dialog.Directory, dialog.ProjectName, target, dialog.OutputType);
            _solutionExplorer.Rebuild(_workspace);
            _symbolPanel.Refresh(_workspace.ActiveProject);
            _debug.LoadBreakpointsForActiveProject();
            LoadLastOpenFileForActiveProject();
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
    private void OpenProjectOrSolution(string path) => Guard("Opening the project", () => OpenProjectOrSolutionCore(path));

    private void OpenProjectOrSolutionCore(string path)
    {
        if (!File.Exists(path))
        {
            TedideMessageBox.ErrorQuery("File Not Found", $"'{path}' no longer exists.", ["OK"]);
            _recentProjects.Remove(path);
            RefreshRecentProjectsMenu();
            return;
        }

        var isSolution = path.EndsWith(TedideSolution.FileExtension, StringComparison.OrdinalIgnoreCase);
        if (!isSolution && !path.EndsWith(TedideProject.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            TedideMessageBox.ErrorQuery("Unsupported file",
                $"Expected a {TedideProject.FileExtension} or {TedideSolution.FileExtension} file.", ["OK"]);
            return;
        }

        // The current project's tabs close with it - settle their unsaved changes first.
        if (!ConfirmCloseFiles(_editorPane.OpenPaths))
            return;
        SaveLastOpenFileForActiveProject();
        _editorPane.CloseAll();

        if (isSolution)
            _workspace.OpenSolution(path);
        else
            _workspace.OpenProject(path);

        _solutionExplorer.Rebuild(_workspace);
        _symbolPanel.Refresh(_workspace.ActiveProject);
        _debug.LoadBreakpointsForActiveProject();
        LoadLastOpenFileForActiveProject();
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
            Key.Empty).WithLiteralHelpText()).ToList();
    }

    /// <summary>
    /// Creates a new source/header file directly in <paramref name="directory"/> - the folder the
    /// user right-clicked in the Solution Explorer to get here (New File isn't offered on the
    /// project root itself - see <see cref="SolutionExplorerTree.TryShowContextMenu"/>).
    /// Compilable files (.c/.s/.asm) are added to the owning project's SourceFiles and the
    /// project is saved; headers are not, since cl65 never compiles them directly. The new file
    /// is opened in the editor once created.
    /// </summary>
    private void NewFile(string directory) => Guard("Creating the file", () => NewFileCore(directory));

    private void NewFileCore(string directory)
    {
        var project = _workspace.Projects.FirstOrDefault(p =>
            IsSameOrInsideDirectory(directory, p.Directory));
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

        var initialContent = string.Equals(Path.GetExtension(filePath), ".h", StringComparison.OrdinalIgnoreCase)
            ? BuildHeaderGuardContent(fileName)
            : string.Empty;
        File.WriteAllText(filePath, initialContent);

        if (SolutionExplorerTree.CompilableExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase))
        {
            project.SourceFiles.Add(RelativeSourcePath(project, filePath));
            project.Save();
        }

        _solutionExplorer.Rebuild(_workspace);
        OpenFile(filePath);
    }

    /// <summary>Builds a freshly-created header's starting content: the standard
    /// <c>#ifndef</c>/<c>#define</c>/<c>#endif</c> include guard, named after the file itself (e.g.
    /// "screen.h" -> "SCREEN_H") - the same convention every bundled sample's own headers already
    /// follow (see e.g. samples/bounce/include/main.h) and <see cref="Workspace.NewProject"/>'s own
    /// generated main.h uses, just applied here to every new header, not only that one.</summary>
    internal static string BuildHeaderGuardContent(string fileName)
    {
        var guard = BuildIncludeGuardMacro(fileName);
        return $"#ifndef {guard}\n#define {guard}\n\n#endif\n";
    }

    /// <summary>Turns a header's base file name (extension stripped) into a valid, all-uppercase
    /// C preprocessor macro name suffixed with "_H" - e.g. "screen.h" -> "SCREEN_H" - by uppercasing
    /// every letter/digit and replacing anything else (spaces, hyphens, ...) with an underscore, so
    /// even an unusual file name still produces a syntactically valid guard.</summary>
    internal static string BuildIncludeGuardMacro(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var macroChars = baseName.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray();
        return new string(macroChars) + "_H";
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
    private void AddExistingItem(string directory) => Guard("Adding the files", () => AddExistingItemCore(directory));

    private void AddExistingItemCore(string directory)
    {
        var project = _workspace.Projects.FirstOrDefault(p =>
            IsSameOrInsideDirectory(directory, p.Directory));
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
    /// Renames a file in place (same folder) after confirming a new name with the user. If it's
    /// open in a tab, the tab follows it to the new name, keeping any unsaved edits - they're
    /// in memory, and get saved to the new name like any other. Updates every project's SourceFiles that
    /// referenced the old path: removed if the new name's extension isn't one cl65 compiles,
    /// added under the new path if it is (covering a rename that changes the extension, e.g.
    /// .c -> .h, not just the base name), same as <see cref="NewFile"/>/<see cref="DeleteFile"/>'s
    /// own compilable-extension check.
    /// </summary>
    private void RenameFile(string path) => Guard("Renaming the file", () => RenameFileCore(path));

    private void RenameFileCore(string path)
    {
        var dialog = new RenameFileDialog(Path.GetFileName(path));
        Application.Run(dialog);
        if (dialog.NewFileName is not { } newFileName)
            return;

        var newPath = Path.Combine(Path.GetDirectoryName(path)!, newFileName);
        // A case-only rename (main.c -> Main.c) names the same file on Windows, so File.Exists is
        // true for it - that's not a collision, and File.Move handles it fine.
        var isCaseOnlyRename = string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        if (File.Exists(newPath) && !isCaseOnlyRename)
        {
            TedideMessageBox.ErrorQuery("File Exists", $"'{newFileName}' already exists.", ["OK"]);
            return;
        }

        File.Move(path, newPath);
        // An open tab follows the file to its new name, unsaved edits and all.
        _editorPane.Rename(path, newPath);
        _navigationHistory.MovePath(path, newPath);

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
        MoveBreakpoints(path, newPath);

        _solutionExplorer.Rebuild(_workspace);
    }

    /// <summary>
    /// Deletes the given file from disk after confirming with the user, closing it first if it's
    /// the currently open file, and removing it from any project's SourceFiles that references it.
    /// </summary>
    private void DeleteFile(string path) => Guard("Deleting the file", () => DeleteFileCore(path));

    private void DeleteFileCore(string path)
    {
        var choice = TedideMessageBox.Query("Delete File",
            $"Delete '{Path.GetFileName(path)}'? This cannot be undone.", ["Delete", "Cancel"]);
        if (choice != 0)
            return;

        // Its tab goes too, unsaved edits included - the file is being deleted either way.
        _editorPane.Close(path);

        File.Delete(path);
        _navigationHistory.RemoveFile(path);

        foreach (var project in _workspace.Projects)
        {
            var relativePath = RelativeSourcePath(project, path);
            if (project.SourceFiles.RemoveAll(f => string.Equals(f, relativePath, StringComparison.OrdinalIgnoreCase)) > 0)
                project.Save();
        }
        MoveBreakpoints(path, newPath: null);

        _solutionExplorer.Rebuild(_workspace);
    }

    /// <summary>
    /// Keeps the active project's breakpoints attached to a file that was just renamed to
    /// <paramref name="newPath"/>, or drops them if it was deleted (<paramref name="newPath"/> null).
    /// Breakpoints are stored by relative path, so before this a rename silently orphaned them -
    /// they vanished from the file, and a debug session then reported them as unresolvable.
    /// </summary>
    private void MoveBreakpoints(string oldPath, string? newPath)
    {
        if (_workspace.ActiveProject is not { } project)
            return;

        var newRelative = newPath is null ? null : RelativeSourcePath(project, newPath);
        if (!_debug.Breakpoints.RenameSourceFile(RelativeSourcePath(project, oldPath), newRelative))
            return;

        _debug.Breakpoints.Save(project.ResolvedBreakpointsFile);
        _debug.BreakpointsChanged();
    }

    /// <summary>
    /// A file's path relative to <paramref name="project"/>'s own directory, in the same "/"
    /// (never "\") form <see cref="TedideProject.SourceFiles"/> entries are written in - matching
    /// how the bundled sample .tproj files (and <see cref="Workspace.NewProject"/>'s scaffolded
    /// one) are authored, since <see cref="Path.GetRelativePath(string, string)"/> alone returns
    /// "\"-separated paths on Windows, which would silently fail to match/dedupe against those.
    /// </summary>
    /// <summary>Whether <paramref name="path"/> is <paramref name="directory"/> itself or somewhere
    /// inside it. Not a bare StartsWith, which also matched a sibling that merely shares the name
    /// as a prefix - "Game" would claim files in "GameTools".</summary>
    internal static bool IsSameOrInsideDirectory(string path, string directory)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string RelativeSourcePath(TedideProject project, string path) =>
        Path.GetRelativePath(project.Directory, path).Replace('\\', '/');

    /// <summary>
    /// Shows the given file in the editor: its tab if it's already open, otherwise a new tab.
    /// Everything that follows the shown file (title, highlighting, breakpoints) is refreshed by
    /// <see cref="OnActiveDocumentChanged"/>.
    /// </summary>
    private void OpenFile(string path) => Guard("Opening the file", () => _editorPane.Open(path));

    /// <summary>
    /// Brings everything tied to "the file in the editor" up to date after a tab switch, open,
    /// close or rename: the frame title, the status bar's language, the breakpoint highlights,
    /// and the current-debug-line highlight, which only belongs on the file execution stopped in.
    /// </summary>
    private void OnActiveDocumentChanged()
    {
        _gitIntegration.ActiveDocumentChanged();
        _editorFrame.Title = _editorPane.OpenPath is { } path ? Path.GetFileName(path) : NoFileOpenTitle;
        UpdateLanguageIndicator();
        _debug.RefreshBreakpointHighlights();
        _debugLineTransformer.CurrentLineNumber = _debugLine is { } stop
            && _editorPane.IsShown(stop.FilePath)
                ? stop.Line
                : null;
        _editorPane.Editor.SetNeedsDraw();
    }

    /// <summary>Where execution is stopped, while it is - so the current-line highlight goes with
    /// that file's tab, not whichever tab is shown. See <see cref="SetDebugLine"/>.</summary>
    private (string FilePath, int Line)? _debugLine;

    /// <summary>Records (or clears) where execution stopped and highlights that line if its file
    /// is the one shown.</summary>
    private void SetDebugLine((string FilePath, int Line)? location)
    {
        _debugLine = location;
        OnActiveDocumentChanged();
    }

    /// <summary>Opens the Help > About dialog. Read-only - see <see cref="AboutDialog"/>.</summary>
    private void ShowAbout()
    {
        Application.Run(new AboutDialog());
    }

    /// <summary>Records whichever file is currently open (if any) as the active project's "last
    /// open file", so <see cref="LoadLastOpenFileForActiveProject"/> can reopen it automatically
    /// next time this same project becomes active again. Called right before switching away from
    /// the active project - to a different project/solution (<see cref="OpenProjectOrSolution"/>),
    /// a freshly created one (<see cref="NewProject"/>), or none at all (<see cref="CloseSolution"/>) -
    /// and once more from Program.cs on quit (see <see cref="SaveSessionState"/>), the same two
    /// moments <see cref="SaveLayoutSettings"/> is saved at. A no-op if no project is loaded (there's
    /// nowhere to save the sidecar file).</summary>
    private void SaveLastOpenFileForActiveProject() => Guard("Saving the session", () => SaveLastOpenFileForActiveProjectCore());

    private void SaveLastOpenFileForActiveProjectCore()
    {
        if (_workspace.ActiveProject is not { } project)
            return;

        _sessionState.LastOpenFile = _editorPane.OpenPath is { } openPath
            ? Path.GetRelativePath(project.Directory, openPath)
            : null;
        // Only files in the solution's projects - a tab from elsewhere (a cc65 header opened by Go
        // To Definition, say) is left out rather than stored as a "..\..\" path.
        _sessionState.OpenFiles = _editorPane.OpenPaths
            .Where(p => _workspace.ProjectFor(p) is not null)
            .Select(p => Path.GetRelativePath(project.Directory, p))
            .ToList();
        _sessionState.Save(project.ResolvedSessionFile);
    }

    /// <summary>Reloads <see cref="_sessionState"/> from the active project's session sidecar file
    /// (see <see cref="TedideProject.ResolvedSessionFile"/>), closes whatever tabs are open, and
    /// reopens the tabs it recorded (any that still exist on disk), finishing on the one that was
    /// showing. Callers settle unsaved changes in the old tabs first (<see cref="ConfirmCloseFiles"/>).
    /// Called alongside <see cref="DebugSession.LoadBreakpointsForActiveProject"/> at
    /// every point the active project itself changes (open/new/close) - deliberately not also at
    /// Project Settings save, since that keeps the same project active and already has its own
    /// reopen-after-rename handling (see <see cref="RenameProject"/>).</summary>
    private void LoadLastOpenFileForActiveProject()
    {
        string? problem = null;
        _sessionState = _workspace.ActiveProject is { } project
            ? SessionStateFile.LoadOrRecover(project.ResolvedSessionFile, out problem)
            : new SessionStateFile();
        if (problem is not null)
            AppendOutputLine(problem);

        // Whatever was open belonged to the previous project, and callers have already settled
        // its unsaved changes (see ConfirmCloseFiles). Its jump history goes with it.
        _editorPane.CloseAll();
        _navigationHistory.Clear();
        if (_workspace.ActiveProject is not { } activeProject)
            return;

        // A session saved before tabs existed has only LastOpenFile.
        var relativePaths = _sessionState.OpenFiles ?? (_sessionState.LastOpenFile is { } single ? [single] : []);
        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(activeProject.Directory, relativePath);
            if (File.Exists(fullPath))
                OpenFile(fullPath);
        }

        // Finish on the tab that was showing.
        if (_sessionState.LastOpenFile is { } last && Path.Combine(activeProject.Directory, last) is var lastPath && _editorPane.IsOpen(lastPath))
            _editorPane.Open(lastPath);
    }

    /// <summary>File > Close File (Ctrl+W): closes the tab being shown, asking about unsaved changes first.</summary>
    private void CloseActiveFile()
    {
        if (_editorPane.OpenPath is { } path)
            CloseFile(path);
    }

    /// <summary>Closes one tab, asking about its unsaved changes first. False if the user cancelled.</summary>
    private bool CloseFile(string path)
    {
        if (!ConfirmCloseFiles([path]))
            return false;
        _editorPane.Close(path);
        return true;
    }

    /// <summary>File > Close All Files: closes every tab, asking about unsaved changes once for all of them.</summary>
    private void CloseAllFiles()
    {
        if (ConfirmCloseFiles(_editorPane.OpenPaths))
            _editorPane.CloseAll();
    }

    /// <summary>File > Quit (Ctrl+Q): asks about any unsaved changes, then exits.</summary>
    private void Quit()
    {
        if (ConfirmCloseFiles(_editorPane.OpenPaths))
            Application.RequestStop(this);
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

        if (!ConfirmCloseFiles(_editorPane.OpenPaths))
            return;

        SaveLastOpenFileForActiveProject();
        _editorPane.CloseAll();

        _workspace.Close();
        _solutionExplorer.Rebuild(_workspace);
        _symbolPanel.Refresh(_workspace.ActiveProject);
        _debug.LoadBreakpointsForActiveProject();
        LoadLastOpenFileForActiveProject();
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
    /// Before closing the given open files: if any have unsaved changes, asks once whether to save
    /// them, discard them, or cancel - naming the file when there's one, listing them when there
    /// are several. True if it's fine to go ahead (nothing modified, or Save/Discard chosen and any
    /// save worked), false if the user cancelled or a save failed.
    /// </summary>
    private bool ConfirmCloseFiles(IReadOnlyCollection<string> paths)
    {
        var modified = paths.Where(_editorPane.IsModifiedFile).ToList();
        if (modified.Count == 0)
            return true;

        var message = modified.Count == 1
            ? $"Save changes to {Path.GetFileName(modified[0])}?"
            : $"Save changes to these {modified.Count} files?\n\n{string.Join('\n', modified.Take(8).Select(Path.GetFileName))}"
              + (modified.Count > 8 ? $"\n...and {modified.Count - 8} more" : "");
        var choice = TedideMessageBox.Query("Unsaved Changes", message, [modified.Count == 1 ? "Save" : "Save All", "Discard", "Cancel"]);
        return choice switch
        {
            0 => modified.All(SaveFile), // a failed save must not go on to lose the buffer
            1 => true,
            _ => false, // Cancel, or Esc
        };
    }

    /// <summary>Saves one open file (see <see cref="EditorPane.Save(string)"/>), reporting any
    /// encoding change in Output. False, having already told the user why, if it couldn't be written.</summary>
    private bool SaveFile(string path) => Guard($"Saving {Path.GetFileName(path)}", () =>
    {
        if (_editorPane.Save(path) is { } notice)
            AppendOutputLine(notice);
        _git.RequestRefresh();
    });

    /// <summary>File > Save (Ctrl+S): the file being shown, plus the loaded project/solution files.</summary>
    private bool SaveActive() =>
        (_editorPane.OpenPath is not { } path || SaveFile(path)) && Guard("Saving the project", _workspace.SaveAll);

    /// <summary>File > Save All, and every build: each modified open file plus the project/solution
    /// files. False, having already told the user why, if anything couldn't be written.</summary>
    private bool SaveAll() =>
        _editorPane.ModifiedPaths.All(SaveFile) && Guard("Saving the project", _workspace.SaveAll);

    /// <summary>
    /// Runs <paramref name="body"/>, turning a file-system or file-format failure into an error
    /// box instead of a crash. Needed on anything reachable from a menu, key or Solution Explorer
    /// action: an exception escaping one of those is unhandled by Terminal.Gui and terminates the
    /// whole process with no visible message (confirmed: Ctrl+S on a read-only file, and renaming
    /// a project whose .tsln lived in the renamed folder, both did exactly that). Returns false if
    /// it failed. Deliberately narrow: only exceptions that describe the outside world (files,
    /// permissions, malformed JSON) - a programming error should still surface loudly.
    /// </summary>
    private bool Guard(string action, Action body)
    {
        try
        {
            body();
            return true;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            Log.Error(ex, "{Action} failed", action);
            OnUiThread(() => TedideMessageBox.ErrorQuery("Error", $"{action} failed:\n{ex.Message}", ["OK"]));
            return false;
        }
    }

    internal static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or NotSupportedException or System.Security.SecurityException
        // Invalid path characters surface as a plain ArgumentException; its null/out-of-range
        // subclasses would be bugs, not bad input.
        || (ex is ArgumentException && ex is not ArgumentNullException and not ArgumentOutOfRangeException);

    /// <summary>
    /// Solution Explorer > Add New Project: scaffolds a project (an application or a library) in
    /// its own folder beside the solution file, and adds it to the solution.
    /// </summary>
    private void AddNewProject() => Guard("Adding the project", () =>
    {
        if (_workspace.Solution is not { } solution)
            return;
        var dialog = new NewProjectDialog(_workspace.DefaultNewProjectParent(), "Add New Project");
        Application.Run(dialog);
        if (dialog.Target is not { } target || string.IsNullOrWhiteSpace(dialog.ProjectName))
            return;
        var project = _workspace.AddNewProject(dialog.Directory, dialog.ProjectName.Trim(), target, dialog.OutputType);
        _solutionExplorer.Rebuild(_workspace);
        AppendOutputLine($"Added {project.Name} ({(project.IsLibrary ? "library" : "application")}) to the solution.");
    });

    /// <summary>Solution Explorer > Add Existing Project: adds a .tproj from disk to the solution.</summary>
    private void AddExistingProject() => Guard("Adding the project", () =>
    {
        if (_workspace.Solution is null)
            return;
        var dialog = new OpenDialog { Title = "Add Existing Project" };
        Application.Run(dialog);
        if (dialog.FilePaths.FirstOrDefault() is not { } path)
            return;
        if (!path.EndsWith(TedideProject.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            TedideMessageBox.ErrorQuery("Unsupported file", $"Expected a {TedideProject.FileExtension} file.", ["OK"]);
            return;
        }
        var project = _workspace.AddExistingProject(path);
        _solutionExplorer.Rebuild(_workspace);
        AppendOutputLine($"Added {project.Name} to the solution.");
    });

    /// <summary>
    /// Solution Explorer > Remove from Solution: drops the project (its files stay on disk) and
    /// other projects' references to it, after closing its tabs.
    /// </summary>
    private void RemoveProject(TedideProject project) => Guard("Removing the project", () =>
    {
        var choice = TedideMessageBox.Query("Remove Project",
            $"Remove {project.Name} from the solution?\n\nIts files stay on disk; other projects stop referencing it.", ["Remove", "Cancel"]);
        if (choice != 0)
            return;

        var openInProject = _editorPane.OpenPaths.Where(p => _workspace.ProjectFor(p) == project).ToList();
        if (!ConfirmCloseFiles(openInProject))
            return;
        var wasStartup = project == _workspace.ActiveProject;
        if (wasStartup)
            SaveLastOpenFileForActiveProject();
        foreach (var path in openInProject)
            _editorPane.Close(path);

        _workspace.RemoveProject(project);
        _solutionExplorer.Rebuild(_workspace);
        if (wasStartup)
            OnStartupProjectChanged();
        AppendOutputLine($"Removed {project.Name} from the solution.");
    });

    /// <summary>
    /// Solution Explorer > Delete Project: removes the project from the solution (as Remove from
    /// Solution does) and sends its whole folder to the Recycle Bin, after confirming. Refused when
    /// the folder also holds the solution file or another project - see <see cref="ProjectDeletion"/>.
    /// Its tabs close without asking about unsaved changes, since the files are going anyway.
    /// </summary>
    private void DeleteProject(TedideProject project) => Guard("Deleting the project", () =>
    {
        if (ProjectDeletion.WhyNot(_workspace, project) is { } reason)
        {
            TedideMessageBox.ErrorQuery("Can't Delete Project", reason, ["OK"]);
            return;
        }

        var choice = TedideMessageBox.Query("Delete Project",
            $"Delete {project.Name} and everything in its folder?\n\n{project.Directory}\n\n"
            + "The folder goes to the Recycle Bin. Other projects stop referencing it.", ["Delete", "Cancel"]);
        if (choice != 0)
            return;

        var wasStartup = project == _workspace.ActiveProject;
        if (wasStartup)
            SaveLastOpenFileForActiveProject();
        foreach (var path in _editorPane.OpenPaths.Where(p => IsSameOrInsideDirectory(p, project.Directory)).ToList())
            _editorPane.Close(path);

        // The folder first: if Windows can't recycle it (a file in use), the solution is untouched.
        ProjectDeletion.RecycleDirectory(project.Directory);
        _workspace.RemoveProject(project);

        // The startup project's breakpoints in the deleted files go with them.
        if (!wasStartup && _workspace.ActiveProject is { } startup
            && _debug.Breakpoints.Breakpoints.RemoveAll(b => IsSameOrInsideDirectory(Path.GetFullPath(Path.Combine(startup.Directory, b.SourceFile)), project.Directory)) > 0)
        {
            _debug.Breakpoints.Save(startup.ResolvedBreakpointsFile);
            _debug.RefreshBreakpointHighlights();
        }

        _solutionExplorer.Rebuild(_workspace);
        if (wasStartup)
            OnStartupProjectChanged();
        AppendOutputLine($"Deleted {project.Name}: {project.Directory} is in the Recycle Bin.");
    });

    /// <summary>Solution Explorer > Set as Startup Project: the project Run, Start Debugging and
    /// Build Project act on, remembered in the solution file.</summary>
    private void SetStartupProject(TedideProject project) => Guard("Setting the startup project", () =>
    {
        SaveLastOpenFileForActiveProject();
        _workspace.SetStartupProject(project);
        _solutionExplorer.Rebuild(_workspace);
        OnStartupProjectChanged();
        AppendOutputLine($"{project.Name} is now the startup project.");
    });

    /// <summary>Breakpoints and the Symbols tab belong to the startup project - reload them for
    /// the new one. Open tabs are left as they are.</summary>
    private void OnStartupProjectChanged()
    {
        _debug.LoadBreakpointsForActiveProject();
        _symbolPanel.Refresh(_workspace.ActiveProject);
    }

    /// <summary>
    /// Opens the settings dialog for the active project - its Settings tab (name, target, output
    /// file, extra cl65 arguments) and Optimizer tab (cc65 optimization preset). Rebuilds the
    /// Solution Explorer afterward since its project node label includes the name and target,
    /// which the dialog may have just changed. If the name changed, also renames the project's
    /// folder (and its .tproj file) on disk to match - see <see cref="RenameProject"/>.
    /// </summary>
    private void ShowProjectSettings() => ShowProjectSettings(_workspace.ActiveProject);

    /// <summary>The same, for any project - the Solution Explorer's project Settings... item.</summary>
    private void ShowProjectSettings(TedideProject? project) => Guard("Saving project settings", () => ShowProjectSettingsCore(project));

    private void ShowProjectSettingsCore(TedideProject? project)
    {
        if (project is null)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        var oldName = project.Name;
        var oldFilePath = project.FilePath;
        var oldDirectory = project.Directory;

        var dialog = new ProjectSettingsDialog(project, _workspace.Projects);
        Application.Run(dialog);
        if (!dialog.Saved)
            return;

        // The dialog's "CC65"/"VICE" tabs already persisted to ToolchainSettings on Save (they're
        // not project state - see ProjectSettingsDialog) - reload and apply immediately so a
        // changed value takes effect without restarting Tedide. Unlike the startup call in the
        // constructor, an explicit Save is allowed to unset CC65_HOME.
        ApplyToolchainSettings(ToolchainSettings.Load(), allowUnsettingCc65Home: true);

        if (oldFilePath is not null && !string.Equals(project.Name, oldName, StringComparison.Ordinal))
            RenameProject(project, oldName, oldFilePath, oldDirectory);

        _solutionExplorer.Rebuild(_workspace);
        _symbolPanel.Refresh(_workspace.ActiveProject);
        _debug.LoadBreakpointsForActiveProject();
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
    /// Follows a project name change just saved by <see cref="ProjectSettingsDialog"/> through to
    /// disk - its folder, .tproj, sidecar files and solution (see <see cref="ProjectRename"/>) - then
    /// brings the UI along: the open file if it moved, and the Recent list, whose old entry would
    /// otherwise point at a path that no longer exists and silently drop off the menu.
    /// </summary>
    private void RenameProject(TedideProject project, string oldName, string oldFilePath, string oldDirectory)
    {
        // Open tabs hold paths, which won't follow a folder move by themselves - remember where
        // each one inside the project sits, and point it at the new folder afterwards. Their
        // buffers (unsaved edits included) stay as they are.
        var openInProject = _editorPane.OpenPaths
            .Where(p => p.StartsWith(oldDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(p => (OldPath: p, Relative: Path.GetRelativePath(oldDirectory, p)))
            .ToList();

        var oldRecentPath = _workspace.Solution?.FilePath ?? oldFilePath;
        var result = ProjectRename.Apply(project, _workspace.Solution, oldName, oldFilePath, renameFolder: true);
        // Other projects' references follow it to its new .tproj path.
        _workspace.RetargetReferences(oldFilePath, result.NewProjectFile);

        _recentProjects.Remove(oldRecentPath);
        RememberRecentProject(result.NewSolutionFile ?? result.NewProjectFile);

        _navigationHistory.MovePath(oldDirectory, project.Directory);
        foreach (var (oldPath, relative) in openInProject)
        {
            var newPath = Path.Combine(project.Directory, relative);
            if (!string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase))
                _editorPane.Rename(oldPath, newPath);
        }

        if (result.FolderNotRenamedReason is { } reason)
            TedideMessageBox.ErrorQuery("Folder Not Renamed",
                $"The project was renamed to '{project.Name}', but its folder was left as-is: {reason}.", ["OK"]);
    }

    /// <summary>Safe to call from any thread - see <see cref="OnUiThread"/>.</summary>
    private void AppendOutputLine(string line) => OnUiThread(() => _outputView.AppendLine(line));

    void IShell.AppendOutputLine(string line) => AppendOutputLine(line);
    void IShell.Fire(Task task, string what) => Fire(task, what);
    bool IShell.Guard(string action, Action body) => Guard(action, body);
    void IShell.OpenFile(string path) => OpenFile(path);
    bool IShell.SaveFile(string path) => SaveFile(path);
    bool IShell.SaveAll() => SaveAll();
    void IShell.OpenProjectOrSolution(string path) => OpenProjectOrSolution(path);
    void IShell.ShowOutputTab() => ShowOutputTab();
    void IShell.BringOutputForward() => _outputTabs.Value = _outputTab;
    void IShell.ShowPane(View content) => ShowPane(content);

    ViceEmulator IDebugSessionHost.Vice => _vice;
    void IDebugSessionHost.AppendOutputLine(string line) => AppendOutputLine(line);
    void IDebugSessionHost.OnUiThread(Action action) => OnUiThread(action);
    void IDebugSessionHost.Fire(Task task, string what) => Fire(task, what);
    bool IDebugSessionHost.Guard(string action, Action body) => Guard(action, body);
    void IDebugSessionHost.OpenSymbol((string FilePath, int LineNumber) entry) => _navigation.OpenSymbol(entry);
    void IDebugSessionHost.CenterEditorOnLine(string filePath, int lineNumber) => _navigation.CenterEditorOnLine(filePath, lineNumber);
    void IDebugSessionHost.SetDebugLine((string FilePath, int Line)? location) => SetDebugLine(location);
    void IDebugSessionHost.SetDebugStatus(string? status) => SetDebugStatus(status);
    void IDebugSessionHost.ShowDebugTab() => ShowDebugTab();
    Task<BuildResult?> IDebugSessionHost.BuildActiveProjectAsync() => _build.BuildActiveProjectAsync();
    bool IDebugSessionHost.CheckStartupProjectRuns(string action) => _build.CheckStartupProjectRuns(action);

    /// <summary>
    /// Starts <paramref name="task"/> without waiting for it, as <c>_ = task</c> would, but logs a
    /// failure and says so in Output instead of losing it - see <see cref="BackgroundTask"/>.
    /// </summary>
    private void Fire(Task task, [CallerArgumentExpression(nameof(task))] string what = "") =>
        BackgroundTask.Watch(task, exception =>
        {
            Log.Error(exception, "Background task failed: {What}", what);
            OnUiThread(() => AppendOutputLine($"Something went wrong: {exception.Message} (details in the log)"));
        });

    /// <summary>
    /// Runs <paramref name="action"/> right away if already on the UI thread, otherwise posts it
    /// there via Application.Invoke. Awaits started on the UI thread come back to it by themselves
    /// (see <see cref="UiSynchronizationContext"/>); this is for callbacks that arrive on other
    /// threads - a process's output, a failed background task. Posted work runs in order, after
    /// anything already queued.
    /// </summary>
    private static void OnUiThread(Action action)
    {
        if (Environment.CurrentManagedThreadId == Application.MainThreadId)
            action();
        else
            Application.Invoke(action);
    }

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

    private const string AppTitle = "Tedide - CC65 IDE";

    /// <summary>The debug state ("Running...", "Stopped in main at main.c:24"), or null when not
    /// debugging.</summary>
    private string? _debugStatus;

    /// <summary>Shows the debug state in the window title, as Visual Studio does, rather than in
    /// the Debug tab; null puts the plain title back.</summary>
    private void SetDebugStatus(string? status)
    {
        _debugStatus = status;
        Title = DebugTitle(status);
    }

    internal static string DebugTitle(string? status) => status is null ? AppTitle : $"{AppTitle} - {status}";

    /// <summary>Brings the bottom-pane tab holding <paramref name="content"/> to the front and focuses it.</summary>
    private void ShowPane(View content)
    {
        if (content.SuperView is { } tab)
            _outputTabs.Value = tab;
        content.SetFocus();
    }

    /// <summary>Debug > Windows: the Debug tab, with <paramref name="window"/> in front and focused.</summary>
    private void ShowDebugWindow(DebugWindow window)
    {
        _outputTabs.Value = _debugTab;
        _debugPanel.ShowWindow(window);
    }

    private void ShowSolutionExplorer() => _solutionExplorer.SetFocus();

    /// <summary>
    /// Switches the Output/Error List pane to its "Debug" tab and gives the debug panel input
    /// focus - used when a debug session starts, so its debugger windows are immediately
    /// visible rather than left behind whatever tab the user had last selected. Focus moves back
    /// to the editor as soon as execution actually stops somewhere (see <see cref="NavigationCommands.OpenSymbol"/>,
    /// called when a debug session stops - see <see cref="DebugSession"/>), so this is only
    /// the very first thing the user sees while the session is coming up.
    /// </summary>
    private void ShowDebugTab()
    {
        _outputTabs.Value = _debugTab;
        _debugPanel.SetFocus();
    }
}
