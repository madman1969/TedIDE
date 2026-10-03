using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Serilog;
using Tedide.Theming;
using Tedide.App.Views;
using Tedide.Build;
using Tedide.Core;
using Tedide.Core.Debugging;
using Tedide.Core.Navigation;
using Tedide.Debug;
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
public sealed class AppShell : Window
{
    private readonly Workspace _workspace = new();
    private readonly Cc65Toolchain _toolchain = new();
    /// <summary>Non-null only while a build is running - see <see cref="BuildActiveProjectAsync"/>
    /// and <see cref="CancelBuild"/>.</summary>
    private volatile CancellationTokenSource? _buildCancellation;
    // Not inline-initialized (unlike its siblings below) - its BinDirectory depends on
    // ToolchainSettings, applied in the constructor via ApplyToolchainSettings (also reapplied
    // after every ProjectSettingsDialog save - see ShowProjectSettings).
    private readonly ViceEmulator _vice = new();
    private readonly RecentProjectsSettings _recentProjects = RecentProjectsSettings.Load();

    /// <summary>Navigate Backward/Forward (Alt+Left/Alt+Right) - see <see cref="RecordJump"/>.</summary>
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
    /// <summary>Where the Memory tab reads from, once the user has entered an address, and what it
    /// read there at the last stop - so the next stop can highlight what changed.</summary>
    private ushort? _memoryAddress;
    private byte[]? _memorySnapshot;
    private readonly DebugPanelView _debugPanel = new();
    private readonly CurrentDebugLineTransformer _debugLineTransformer = new();
    private readonly BreakpointLineTransformer _breakpointLineTransformer = new();
    private BreakpointsFile _breakpoints = new();
    private SessionStateFile _sessionState = new();
    private ViceMonitorClient? _debugClient;
    private DbgFile? _dbgFile;
    private bool _isDebugging;
    private bool _isStopped;
    private bool _isStepping;
    /// <summary>Maps each currently-armed breakpoint to the VICE-assigned checkpoint number
    /// <see cref="ViceMonitorClient.SetCheckpointAsync"/> returned for it, so it can be deleted
    /// again later - see <see cref="SyncCheckpointsWithViceAsync"/>.</summary>
    private readonly Dictionary<BreakpointEntry, uint> _checkpointNumbers = new();
    /// <summary>Serializes every set/delete pass over VICE's checkpoints (and so every access to
    /// <see cref="_checkpointNumbers"/>) - toggling two breakpoints in quick succession otherwise
    /// runs two fire-and-forget <see cref="SyncCheckpointsWithViceAsync"/> passes interleaved across
    /// their awaits, one clearing the dictionary while the other is still enumerating it.</summary>
    private readonly SemaphoreSlim _checkpointLock = new(1, 1);
    /// <summary>User-added memory watches for the active debug session - see <see cref="WatchEntry"/>'s
    /// own doc comment for why these aren't persisted the way <see cref="_breakpoints"/> is.</summary>
    private readonly List<WatchEntry> _watches = new();
    private Tabs _outputTabs = null!;
    private View _outputTab = null!;
    private View _debugTab = null!;
    private View _referencesTab = null!;
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
        _solutionExplorer.AddNewProjectRequested += AddNewProject;
        _solutionExplorer.AddExistingProjectRequested += AddExistingProject;
        _solutionExplorer.BuildSolutionRequested += () => _ = BuildSolutionAsync();
        _solutionExplorer.CleanSolutionRequested += CleanSolution;
        _solutionExplorer.SetStartupProjectRequested += SetStartupProject;
        _solutionExplorer.BuildProjectRequested += project => _ = BuildProjectsAsync([project]);
        _solutionExplorer.CleanProjectRequested += project => CleanProjects([project]);
        _solutionExplorer.ProjectSettingsRequested += ShowProjectSettings;
        _solutionExplorer.RemoveProjectRequested += RemoveProject;
        _solutionExplorer.DeleteProjectRequested += DeleteProject;
        explorerFrame.Add(_solutionExplorer);

        _editorFrame = new FrameView
        {
            Title = NoFileOpenTitle,
            // The title is the open file's name, and a title reads its first "_" as a hotkey
            // marker - "sound_fx.c" would show as "soundfx.c".
            HotKeySpecifier = new System.Text.Rune(0xFFFF),
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
        _editorPane.GoToDefinitionRequested += GoToDefinition;
        _editorPane.FindReferencesRequested += FindAllReferences;
        _editorPane.RenameSymbolRequested += RenameSymbol;
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
        _errorListView.DiagnosticActivated += OpenDiagnostic;
        errorListTab.Add(_errorListView);

        var symbolsTab = new View { Title = " _Symbols ", Width = Dim.Fill(), Height = Dim.Fill() };
        _symbolPanel.Width = Dim.Fill();
        _symbolPanel.Height = Dim.Fill();
        _symbolPanel.LineActivated += entry =>
        {
            RecordJump();
            OpenSymbol(entry);
        };
        symbolsTab.Add(_symbolPanel);

        _referencesTab = new View { Title = " _References ", Width = Dim.Fill(), Height = Dim.Fill() };
        _referencesView.Width = Dim.Fill();
        _referencesView.Height = Dim.Fill();
        _referencesView.ReferenceActivated += reference => NavigateTo(reference.FilePath, reference.Line, reference.Column, reference.Length);
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
        _memoryView.AddressRequested += ShowMemoryAt;
        _memoryView.PageRequested += delta =>
        {
            if (_memoryAddress is { } address)
                ShowMemoryAt($"${Math.Clamp(address + delta, 0, 0x10000 - MemoryView.ByteCount):X4}");
        };
        memoryTab.Add(_memoryView);
        _outputTabs.Add(memoryTab);

        var disassemblyTab = new View { Title = " Dis_assembly ", Width = Dim.Fill(), Height = Dim.Fill() };
        _disassemblyView.Width = Dim.Fill();
        _disassemblyView.Height = Dim.Fill();
        _disassemblyView.SourceRequested += OpenSourceForAddress;
        disassemblyTab.Add(_disassemblyView);
        _outputTabs.Add(disassemblyTab);

        _debugPanel.FrameActivated += frame => OpenSymbol((frame.FilePath!, frame.Line));

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
            new MenuItem("_Close File", "", CloseActiveFile, Key.W.WithCtrl) { BindKeyToApplication = true },
            new MenuItem("Close A_ll Files", "", CloseAllFiles, Key.Empty),
            // The keys are labels here - the editor and OnKeyDown handle them (see EditorPane).
            new MenuItem("_Next File", "", () => _editorPane.CycleDocument(1), Key.PageDown.WithCtrl),
            new MenuItem("Pre_vious File", "", () => _editorPane.CycleDocument(-1), Key.PageUp.WithCtrl),
            new Line(),
            new MenuItem("_Quit", "", Quit, Key.Q.WithCtrl),
        });

        var buildMenu = new MenuBarItem("_Build", new List<MenuItem>
        {
            new("_Build Project", "", () => _ = BuildActiveProjectAsync(), Key.F5),
            new("Build _Solution", "", () => _ = BuildSolutionAsync(), Key.Empty),
            new("C_ancel Build", "", CancelBuild, Key.Empty),
            new("_Clean Project", "", CleanActiveProject, Key.Empty),
            new("Clea_n Solution", "", CleanSolution, Key.Empty),
            new("_Run Project", "", () => _ = RunActiveProjectAsync(), Key.F6),
        });

        // F5/F6 above are already Build/Run - Start Debugging/Continue/Step use keys of their own.
        // The keys shown here are only labels: F10/F7 fire via their status-bar Shortcuts and
        // Shift+F5/Ctrl+F5 via OnKeyDown, because BindKeyToApplication never fires for this menu's
        // items (confirmed live - see OnKeyDown). Adding it back would risk double-firing if it
        // ever starts working.
        var debugMenu = new MenuBarItem("_Debug", new List<View>
        {
            new MenuItem("_Start Debugging", "", () => _ = StartDebuggingAsync(), Key.F5.WithShift),
            new MenuItem("_Continue", "", () => _ = ContinueDebuggingAsync(), Key.F5.WithCtrl),
            new MenuItem("Step _Over", "", () => _ = StepDebuggingAsync(stepInto: false), Key.F10),
            // F7, not Visual Studio's F11 - Windows Terminal claims F11 for its own full-screen
            // toggle before the app ever sees it. F7/F8 is also the Turbo Pascal/Borland pairing.
            new MenuItem("Step _Into", "", () => _ = StepDebuggingAsync(stepInto: true), Key.F7),
            new MenuItem("Sto_p Debugging", "", () => _ = StopDebuggingAsync(), Key.Empty),
            new Line(),
            new MenuItem("_Toggle Breakpoint", "", ToggleBreakpointAtCursor, Key.F9),
            new MenuItem("Breakpoint Co_ndition...", "", EditBreakpointConditionAtCursor, Key.Empty),
            new MenuItem("_Breakpoints...", "", ShowBreakpointsDialog, Key.Empty),
            new Line(),
            new MenuItem("Add _Watch...", "", ShowAddWatchDialog, Key.Empty),
            new MenuItem("C_lear Watches", "", ClearWatches, Key.Empty),
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
            new("_Context Help", "", ShowContextHelp, ContextHelpKey),
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
        editMenuItems.AddAt(0, new MenuItem("_Find in Files...", "", () => ShowFindInFiles(), FindInFilesKey));
        editMenuItems.AddAt(1, new MenuItem("_Go To Line...", "", ShowGoToLine, Key.G.WithCtrl));
        editMenuItems.AddAt(2, new MenuItem("Go To _Definition", "", GoToDefinition, GoToDefinitionKey));
        editMenuItems.AddAt(3, new MenuItem("Find All _References", "", FindAllReferences, FindReferencesKey));
        editMenuItems.AddAt(4, new MenuItem("Re_name Symbol...", "", RenameSymbol, RenameSymbolKey));
        editMenuItems.AddAt(5, new MenuItem("Navigate _Backward", "", NavigateBackward, NavigateBackwardKey));
        editMenuItems.AddAt(6, new MenuItem("Navigate For_ward", "", NavigateForward, NavigateForwardKey));
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

    /// <summary>F1, as in Visual Studio - see <see cref="ShowContextHelp"/>.</summary>
    private static readonly Key ContextHelpKey = Key.F1;

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
            action = () => _ = StartDebuggingAsync();
        else if (key == Key.F5.WithCtrl)
            action = () => _ = ContinueDebuggingAsync();
        else if (key == FindInFilesKey)
            action = () => ShowFindInFiles();
        else if (key == GoToDefinitionKey)
            action = GoToDefinition;
        else if (key == FindReferencesKey)
            action = FindAllReferences;
        else if (key == RenameSymbolKey)
            action = RenameSymbol;
        else if (key == NavigateBackwardKey)
            action = NavigateBackward;
        else if (key == NavigateForwardKey)
            action = NavigateForward;
        else if (key == ContextHelpKey)
            action = ShowContextHelp;
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
        statusBar.Add(new Shortcut(Key.F5, "Build", () => _ = BuildActiveProjectAsync()));
        // A plain MenuItem's Key only acts as a hotkey while its menu is already open - a Shortcut
        // is what actually makes a key global, the same reason Build's F5 above needs one too. F6
        // Run and Ctrl+S Save had the same gap (menu-only, never worked while the editor had focus)
        // until it was reported and fixed here alongside F9/Ctrl+G.
        statusBar.Add(new Shortcut(Key.F6, "Run", () => _ = RunActiveProjectAsync()));
        statusBar.Add(new Shortcut(Key.F9, "Breakpoint", ToggleBreakpointAtCursor));
        // These status-bar Shortcuts are what make F10/F7 work at all: the Debug menu's own items
        // show the keys but don't bind them, because BindKeyToApplication never fires for that
        // menu's items (see OnKeyDown for the confirmed cause).
        statusBar.Add(new Shortcut(Key.F10, "Step", () => _ = StepDebuggingAsync(stepInto: false)));
        statusBar.Add(new Shortcut(Key.F7, "Into", () => _ = StepDebuggingAsync(stepInto: true)));
        statusBar.Add(new Shortcut(Key.S.WithCtrl, "Save", () => SaveActive()));
        statusBar.Add(new Shortcut(Key.G.WithCtrl, "Go To Line", ShowGoToLine));
        statusBar.Add(new Shortcut(Key.Q.WithCtrl, "Quit", Quit));
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
            LoadBreakpointsForActiveProject();
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
        LoadBreakpointsForActiveProject();
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
        if (!_breakpoints.RenameSourceFile(RelativeSourcePath(project, oldPath), newRelative))
            return;

        _breakpoints.Save(project.ResolvedBreakpointsFile);
        RefreshBreakpointHighlights();
        _ = SyncCheckpointsWithViceAsync();
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
        _editorFrame.Title = _editorPane.OpenPath is { } path ? Path.GetFileName(path) : NoFileOpenTitle;
        UpdateLanguageIndicator();
        RefreshBreakpointHighlights();
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

    /// <summary>
    /// Opens the Find in Files dialog, searching every loaded project's directory tree. If the
    /// user activates a result, opens its file in a tab (or switches to it) and moves the caret to
    /// the matched line.
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
    /// current one first, same as <see cref="OpenFile"/>) and moves the caret to the 1-based line and
    /// column, scrolling it into view. Shared by Find in Files, Go To Definition and the References tab.
    /// </summary>
    /// <param name="highlightLength">When non-zero, that many characters from the column are
    /// selected, so the symbol a reference or definition points at stands out - the same
    /// SelectRange highlighting <see cref="OpenDiagnostic"/> gives a whole line.</param>
    private void NavigateTo(string filePath, int lineNumber, int columnNumber, int highlightLength = 0, bool recordJump = true)
    {
        if (recordJump)
            RecordJump();
        if (!_editorPane.IsShown(filePath))
        {
            OpenFile(filePath);
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
            .Where(p => Directory.Exists(p.Directory))
            .SelectMany(p => SolutionExplorerTree.EnumerateFiles(p.Directory))
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
    private string DisplayPath(string path)
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
    private void GoToDefinition() => Guard("Going to the definition", GoToDefinitionCore);

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
            Application.Run(dialog);
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
    private void RecordJump()
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
    private void NavigateBackward() => Guard("Navigating backward", () =>
    {
        if (_navigationHistory.GoBack(CurrentCaretLocation()) is { } target)
            GoToHistoryLocation(target);
        else
            ReportNavigation("Nothing to navigate back to.");
    });

    /// <summary>Edit > Navigate Forward (Alt+Right): undoes a Navigate Backward.</summary>
    private void NavigateForward() => Guard("Navigating forward", () =>
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
    private void ShowContextHelp() => Guard("Opening the Doc Viewer", () =>
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
            TedideMessageBox.ErrorQuery("Doc Viewer not found",
                $"{ContextHelp.DocViewerFileName} wasn't found. Looked in:\n\n{string.Join('\n', candidates)}\n\n"
                + $"Publish it beside Tedide, or set {ContextHelp.DocViewerPathVariable} to its full path.", ["OK"]);
            return;
        }

        var insideWindowsTerminal = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"));
        using var _ = System.Diagnostics.Process.Start(ContextHelp.CreateStartInfo(docViewer, topic, insideWindowsTerminal));
        AppendOutputLine(topic is null ? "Opened the Doc Viewer." : $"Opened the Doc Viewer at '{topic}'.");
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
    private void FindAllReferences() => Guard("Finding references", FindAllReferencesCore);

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
        AppendOutputLine($"Find All References: '{result.Symbol}' - {result.References.Count} reference(s) in {fileCount} file(s).");
        _outputTabs.Value = _referencesTab;
        _referencesView.SetFocus();
    }

    /// <summary>
    /// Edit > Rename Symbol (F2): renames the symbol at the caret everywhere Find All References
    /// finds it, after <see cref="RenameSymbolDialog"/> has checked the new name. Each file open in
    /// a tab is changed in the editor as one undoable step and left unsaved; every other file is
    /// rewritten on disk in its own encoding. Everything is worked out and checked before anything
    /// is changed, so a file that's changed underneath stops the rename before anything is touched.
    /// </summary>
    private void RenameSymbol() => Guard("Renaming the symbol", RenameSymbolCore);

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
            TedideMessageBox.ErrorQuery("Can't Rename", RenameSymbolDialog.Wrap(blocker), ["OK"]);
            return;
        }

        var fileCount = references.References.Select(r => r.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var dialog = new RenameSymbolDialog(symbol, references.References.Count, fileCount, name => navigator.PlanRename(path, line, column, name));
        Application.Run(dialog);
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
        AppendOutputLine($"Renamed '{symbol}' to '{newName}': {plan.Edits.Count} change(s) in {plan.FileCount} file(s)"
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
        AppendOutputLine(message);
        _outputTabs.Value = _outputTab;
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

        RecordJump();
        if (!_editorPane.IsShown(filePath))
        {
            OpenFile(filePath);
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
    private void OpenSymbol((string FilePath, int LineNumber) entry)
    {
        if (!File.Exists(entry.FilePath))
            return;

        if (!_editorPane.IsShown(entry.FilePath))
        {
            OpenFile(entry.FilePath);
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
    private void CenterEditorOnLine(string filePath, int lineNumber)
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

    /// <summary>Reloads <see cref="_breakpoints"/> from the active project's breakpoints sidecar
    /// file (see <see cref="TedideProject.ResolvedBreakpointsFile"/>), or resets to an empty set if
    /// no project is loaded. Called everywhere the active project itself changes (open/new/close,
    /// Project Settings save) - not on every build, since breakpoints don't change from a build.</summary>
    private void LoadBreakpointsForActiveProject()
    {
        string? problem = null;
        _breakpoints = _workspace.ActiveProject is { } project
            ? BreakpointsFile.LoadOrRecover(project.ResolvedBreakpointsFile, out problem)
            : new BreakpointsFile();
        if (problem is not null)
            AppendOutputLine(problem);
        RefreshBreakpointHighlights();
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
    /// Called alongside <see cref="LoadBreakpointsForActiveProject"/> at
    /// every point the active project itself changes (open/new/close) - deliberately not also at
    /// Project Settings save, since that keeps the same project active and already has its own
    /// reopen-after-rename handling (see <see cref="RenameProjectFolder"/>).</summary>
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

    /// <summary>
    /// Recomputes <see cref="_breakpointLineTransformer"/>'s highlighted line set from
    /// <see cref="_breakpoints"/>, scoped to the file being shown (the editor shows one tab at a
    /// time, and this runs again on every tab switch - see <see cref="OnActiveDocumentChanged"/>),
    /// and refreshes the Debug tab's breakpoints strip (unscoped - every breakpoint in
    /// the project, not just the open file). Called whenever either the open file or the
    /// breakpoint set itself changes.
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
        _debugPanel.SetBreakpoints(_breakpoints.Breakpoints);
    }

    /// <summary>
    /// Toggles a breakpoint on the currently open file's cursor line (F9) - the fallback for
    /// setting breakpoints since Terminal.Gui.Editor's Editor has no clickable gutter (see
    /// <see cref="CurrentDebugLineTransformer"/>'s own doc comment). Saves immediately so it
    /// survives even if the debug session (or Tedide itself) is closed without an explicit save.
    /// </summary>
    private void ToggleBreakpointAtCursor() => Guard("Saving breakpoints", () => ToggleBreakpointAtCursorCore());

    private void ToggleBreakpointAtCursorCore()
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
        // Otherwise a breakpoint added/removed mid-session has no effect on the already-running
        // VICE instance - checkpoints are only ever set once, at StartDebuggingAsync's own setup.
        _ = SyncCheckpointsWithViceAsync();
    }

    /// <summary>
    /// Debug > Breakpoint Condition...: sets or clears the condition (VICE monitor syntax) of the
    /// breakpoint on the caret's line, creating the breakpoint first if there isn't one - so a
    /// conditional breakpoint takes one step, not F9 and then this.
    /// </summary>
    private void EditBreakpointConditionAtCursor() => Guard("Saving breakpoints", EditBreakpointConditionAtCursorCore);

    private void EditBreakpointConditionAtCursorCore()
    {
        var project = _workspace.ActiveProject;
        if (project is null || _editorPane.OpenPath is not { } openPath || _editorPane.Editor.Document is null)
            return;

        var line = _editorPane.CaretPosition.Line;
        // Forward slashes, matching the .dbg file - see ToggleBreakpointAtCursorCore.
        var relativePath = Path.GetRelativePath(project.Directory, openPath).Replace('\\', '/');
        var index = _breakpoints.Breakpoints.FindIndex(b =>
            b.Line == line && string.Equals(b.SourceFile, relativePath, StringComparison.OrdinalIgnoreCase));
        var current = index >= 0 ? _breakpoints.Breakpoints[index] : new BreakpointEntry(relativePath, line);

        var dialog = new BreakpointConditionDialog($"{relativePath}:{line}", current.Condition);
        Application.Run(dialog);
        if (dialog.Condition is not { } condition)
            return;

        var updated = current with { Condition = condition.Length == 0 ? null : condition, Enabled = true };
        if (index >= 0)
            _breakpoints.Breakpoints[index] = updated;
        else
            _breakpoints.Breakpoints.Add(updated);
        AppendOutputLine($"Breakpoint set: {updated.Describe()}");

        _breakpoints.Save(project.ResolvedBreakpointsFile);
        RefreshBreakpointHighlights();
        _ = SyncCheckpointsWithViceAsync();
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
        // Toggling a breakpoint's Enabled flag (or deleting it) here has the exact same "VICE
        // never finds out" gap as ToggleBreakpointAtCursor - see SyncCheckpointsWithViceAsync's
        // own doc comment. The dialog can toggle/delete several entries in one visit, but the
        // resync itself is a full re-sync (delete everything tracked, re-set every still-enabled
        // breakpoint), not incremental, so one call after it closes covers all of them.
        _ = SyncCheckpointsWithViceAsync();
    }

    /// <summary>
    /// Prompts for a watch expression, resolves it to an address (a matching <see cref="_dbgFile"/>
    /// symbol name if one's loaded, otherwise a raw <c>$hex</c>/decimal address), and adds it to
    /// <see cref="_watches"/>. Shows a placeholder value immediately and, if a session is currently
    /// stopped, kicks off a real read right away rather than waiting for the next step/checkpoint.
    /// </summary>
    private void ShowAddWatchDialog()
    {
        var dialog = new AddWatchDialog();
        Application.Run(dialog);
        if (dialog.Expression is not { } expression)
            return;

        if (!TryResolveWatchAddress(expression, out var address, out var error))
        {
            AppendOutputLine(error);
            return;
        }

        _watches.Add(new WatchEntry(expression, address, dialog.Size));
        _debugPanel.SetWatches(_watches.Select(w => $"{w.Label} (${w.Address:X4}) = ?").ToList());
        _ = RefreshWatchesIfStoppedAsync();
    }

    private void ClearWatches()
    {
        _watches.Clear();
        _debugPanel.SetWatches([]);
    }

    /// <summary>
    /// Resolves a watch expression to an address: first as a <see cref="_dbgFile"/> symbol name
    /// (matched with or without cc65's leading underscore - the same convention
    /// <see cref="DbgFile.FindEnclosingFunctionName"/> uses), falling back to a literal address
    /// (<c>$hex</c>, <c>0xhex</c>, or plain decimal) so hardware registers (e.g. <c>$D012</c> for
    /// the VIC-II raster line) work even without debug info loaded.
    /// </summary>
    private bool TryResolveWatchAddress(string expression, out ushort address, out string error)
    {
        expression = expression.Trim();

        var symbol = _dbgFile?.Symbols.FirstOrDefault(s =>
            s.Value is not null &&
            (string.Equals(s.Name, expression, StringComparison.Ordinal) ||
             string.Equals(s.Name.TrimStart('_'), expression, StringComparison.Ordinal)));
        if (symbol is not null)
        {
            address = (ushort)symbol.Value!.Value;
            error = "";
            return true;
        }

        var hexText = expression.StartsWith('$') ? expression[1..]
            : expression.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? expression[2..]
            : null;
        if (hexText is not null && ushort.TryParse(hexText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
        {
            address = hexValue;
            error = "";
            return true;
        }

        if (ushort.TryParse(expression, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalValue))
        {
            address = decimalValue;
            error = "";
            return true;
        }

        address = 0;
        error = $"Could not resolve watch \"{expression}\" - enter a known symbol name, or an address as $hex, 0xhex, or decimal.";
        return false;
    }

    /// <summary>Reads every watch's current value from VICE and formats it for display - pure data
    /// fetching (no UI touched), so unlike the callers around it this needs no re-marshaling onto
    /// the UI thread; see <see cref="RefreshWatchesIfStoppedAsync"/> and the nested-Invoke call
    /// sites in <see cref="OnCheckpointHit"/>/<see cref="StepDebuggingAsync"/> for where the result
    /// actually reaches <see cref="_debugPanel"/>.</summary>
    private async Task<List<string>> FormatWatchesAsync(ViceMonitorClient debugClient)
    {
        // A snapshot, not _watches itself - the awaits below give the UI thread a chance to add
        // or clear watches mid-loop, which would otherwise throw "Collection was modified".
        var watches = _watches.ToList();
        var formatted = new List<string>(watches.Count);
        foreach (var watch in watches)
        {
            try
            {
                var bytes = await debugClient.GetMemoryAsync(watch.Address, (ushort)(watch.Address + watch.Size - 1));
                var text = watch.Size == 2 && bytes.Length >= 2
                    ? $"${(ushort)(bytes[0] | (bytes[1] << 8)):X4}"
                    : bytes.Length >= 1 ? $"${bytes[0]:X2}" : "?";
                formatted.Add($"{watch.Label} (${watch.Address:X4}) = {text}");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read memory for watch {Label} (${Address:X4})", watch.Label, watch.Address);
                formatted.Add($"{watch.Label} (${watch.Address:X4}) = ?");
            }
        }
        return formatted;
    }

    /// <summary>Each generated .s file's parsed stack frames, keyed by path - parsed on first stop
    /// in it and kept for the session (cleared when a new session's build replaces them).</summary>
    private readonly Dictionary<string, IReadOnlyList<FunctionFrame>> _assemblyFrames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The Locals table for a stop: the parameters and function-level locals of the C function
    /// the PC is in, read from cc65's software stack. Where each one lives comes from the frame
    /// depth at the stopped instruction in cl65's generated .s (see <see cref="GeneratedAssemblyFrames"/>),
    /// its type from its declaration in the C source (see <see cref="CDeclarations"/>) - cc65's
    /// debug info has neither. Empty outside a project's C code (assembly, the runtime library)
    /// or if anything needed is missing; never throws - locals are a convenience, not worth
    /// failing a stop over.
    /// </summary>
    private async Task<IReadOnlyList<LocalRow>> ReadLocalsAsync(ViceMonitorClient debugClient, RegisterSnapshot registers)
    {
        try
        {
            if (_dbgFile is not { } dbgFile || _workspace.ActiveProject is not { } project || registers["PC"] is not { } pc)
                return [];

            var cLocation = dbgFile.FindProjectSourceLocationForAddress(pc, project.Directory);
            var assemblyLine = dbgFile.FindAssemblyLineForAddress(pc);
            if (cLocation is not { } c || assemblyLine is not { } asm)
                return [];

            var asmPath = Path.Combine(project.Directory, asm.FilePath);
            if (!_assemblyFrames.TryGetValue(asmPath, out var frames))
            {
                if (!File.Exists(asmPath))
                    return [];
                frames = GeneratedAssemblyFrames.Parse(await File.ReadAllTextAsync(asmPath));
                _assemblyFrames[asmPath] = frames;
            }

            var frame = frames.FirstOrDefault(f => asm.Line >= f.FirstLine && asm.Line <= f.LastLine);
            if (frame is null || frame.Symbols.Count == 0)
                return [];
            if (!frame.Reliable || frame.DepthAt(asm.Line) is not { } depth)
                return [new LocalRow("(locals unavailable)", "", $"{frame.Name} uses stack code Tedide can't follow")];

            // cc65's software stack pointer: "sp" in cc65 2.19, renamed "c_sp" in later versions.
            var spSymbol = dbgFile.Symbols.FirstOrDefault(s => s.Name is "sp" or "c_sp" && s.Type == "lab" && s.Value is not null);
            if (spSymbol is null)
                return [];
            var spBytes = await debugClient.GetMemoryAsync((ushort)spSymbol.Value!.Value, (ushort)(spSymbol.Value.Value + 1));
            var sp = (ushort)(spBytes[0] | spBytes[1] << 8);

            var source = await File.ReadAllTextAsync(Path.Combine(project.Directory, c.FilePath));
            var types = CDeclarations.FindTypes(source, frame.Name, frame.Symbols.Select(s => s.Name), c.Line);
            var slots = frame.SlotsAt(depth, sp, types);

            var rows = new List<LocalRow>(slots.Count);
            foreach (var slot in slots)
            {
                var typeText = slot.Type?.Text ?? "?";
                string value;
                if (slot.Address is { } address)
                {
                    var bytes = await debugClient.GetMemoryAsync(address, (ushort)(address + slot.Size - 1));
                    value = LocalValueFormatter.Format(bytes, slot.Type);
                }
                else if (slot.Symbol is { IsParameter: true, Offset: 0 } && registers["A"] is { } a)
                {
                    // cc65's fastcall: the last parameter arrives in A (low) / X (high) and is only
                    // pushed by the function's first instruction.
                    byte[] bytes = slot.Size == 1 ? [(byte)a] : [(byte)a, (byte)(registers["X"] ?? 0)];
                    value = LocalValueFormatter.Format(bytes, slot.Type) + " [in A/X]";
                }
                else
                {
                    value = "(not yet on the stack)";
                }
                rows.Add(new LocalRow(slot.Symbol.IsParameter ? $"{slot.Symbol.Name} (param)" : slot.Symbol.Name, typeText, value));
            }
            return rows;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read locals");
            return [];
        }
    }

    /// <summary>Fire-and-forget refresh for when a watch is added/cleared outside the normal
    /// stop/step flow - a no-op unless a session is both connected and currently stopped (reading
    /// memory while running would race the emulator).</summary>
    private async Task RefreshWatchesIfStoppedAsync()
    {
        if (_debugClient is not { } debugClient || !_isDebugging || !_isStopped)
            return;

        var watchLines = await FormatWatchesAsync(debugClient);
        Application.Invoke(() => _debugPanel.SetWatches(watchLines));
    }

    /// <summary>What the Memory and Disassembly tabs and the call stack show for one stop - read
    /// from VICE off the UI thread, then shown by <see cref="ShowDebugViews"/> on it.</summary>
    private sealed record DebugViews(
        IReadOnlyList<CallFrame> CallStack,
        IReadOnlyList<DisassemblyRow> Disassembly,
        string DisassemblyStatus,
        ushort? MemoryAddress,
        byte[] Memory,
        HashSet<int> MemoryChanged);

    /// <summary>Reads everything <see cref="DebugViews"/> holds for the current stop. Never throws:
    /// like the locals, these are extras, not worth failing a stop over.</summary>
    private async Task<DebugViews> ReadDebugViewsAsync(ViceMonitorClient debugClient, RegisterSnapshot registers)
    {
        IReadOnlyList<CallFrame> callStack = [];
        IReadOnlyList<DisassemblyRow> disassembly = [];
        var disassemblyStatus = "No PC to show.";
        try
        {
            callStack = await ReadCallStackAsync(debugClient, registers);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the call stack");
        }
        try
        {
            if (registers["PC"] is { } pc)
                (disassembly, disassemblyStatus) = await ReadDisassemblyAsync(debugClient, pc);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not disassemble");
            disassemblyStatus = $"Could not read memory around the PC: {ex.Message}";
        }

        var memoryAddress = _memoryAddress;
        byte[] memory = [];
        HashSet<int> changed = [];
        if (memoryAddress is { } address)
        {
            try
            {
                memory = await debugClient.GetMemoryAsync(address, (ushort)Math.Min(0xFFFF, address + MemoryView.ByteCount - 1));
                if (_memorySnapshot is { } previous && previous.Length == memory.Length)
                    changed = MemoryDump.ChangedOffsets(previous, memory);
                _memorySnapshot = memory;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read memory at ${Address:X4}", address);
            }
        }
        return new DebugViews(callStack, disassembly, disassemblyStatus, memoryAddress, memory, changed);
    }

    /// <summary>Shows a stop's <see cref="DebugViews"/>. Must run on the UI thread.</summary>
    private void ShowDebugViews(DebugViews views)
    {
        _debugPanel.SetCallStack(views.CallStack);
        _disassemblyView.Show(views.Disassembly, views.DisassemblyStatus);
        if (views.MemoryAddress is { } address && views.Memory.Length > 0)
        {
            var changedText = views.MemoryChanged.Count == 0 ? "" : $" - {views.MemoryChanged.Count} byte(s) changed since the last stop";
            _memoryView.Show(address, views.Memory, views.MemoryChanged, $"As of this stop{changedText}.");
        }
    }

    /// <summary>
    /// The call stack for a stop, innermost first: where the PC is, then each call site found on
    /// the hardware stack by <see cref="CallStackWalker"/>. Only return addresses into the
    /// program's own read-only segments are considered, and each one's JSR is confirmed by reading
    /// the opcode - one small read per candidate, so a stack full of data doesn't mean 255 reads.
    /// </summary>
    private async Task<IReadOnlyList<CallFrame>> ReadCallStackAsync(ViceMonitorClient debugClient, RegisterSnapshot registers)
    {
        if (_dbgFile is not { } dbgFile || _workspace.ActiveProject is not { } project
            || registers["PC"] is not { } pc || registers["SP"] is not { } sp)
            return [];

        var stack = await debugClient.GetMemoryAsync(0x0100, 0x01FF);
        var isJsr = new Dictionary<ushort, bool>();
        foreach (var site in CallStackWalker.CandidateCallSites(stack, (byte)sp).Where(a => dbgFile.IsInReadOnlySegment(a)).Distinct())
            isJsr[site] = (await debugClient.GetMemoryAsync(site, site))[0] == CallStackWalker.JsrOpcode;

        var frames = new List<CallFrame> { DescribeFrame(dbgFile, project, pc) };
        frames.AddRange(CallStackWalker.Walk(stack, (byte)sp, site => isJsr.GetValueOrDefault(site)).Select(site => DescribeFrame(dbgFile, project, site)));
        return frames;
    }

    /// <summary>A call stack line for <paramref name="address"/>: the function (or, in cc65's
    /// runtime, the nearest label) and the project source line, if it has one.</summary>
    private static CallFrame DescribeFrame(DbgFile dbgFile, TedideProject project, ushort address)
    {
        var name = dbgFile.FindEnclosingFunctionName(address) ?? dbgFile.FindNearestLabel(address) ?? "?";
        return dbgFile.FindProjectSourceLocationForAddress(address, project.Directory) is { } location
            ? new CallFrame($"{name}  {DebugPath(project, location.FilePath)}:{location.Line}", Path.Combine(project.Directory, location.FilePath), location.Line)
            : new CallFrame($"{name}  (${address:X4})", null, 0);
    }

    /// <summary>How many bytes before and after the PC the Disassembly tab reads.</summary>
    private const int DisassemblyBytesBefore = 48, DisassemblyBytesAfter = 96;

    /// <summary>
    /// The instructions around <paramref name="pc"/>, decoded for the project's CPU, each noted
    /// with the label at its address, the label its operand refers to, and the source line where
    /// a new one starts.
    /// </summary>
    private async Task<(IReadOnlyList<DisassemblyRow> Rows, string Status)> ReadDisassemblyAsync(ViceMonitorClient debugClient, ushort pc)
    {
        var start = (ushort)Math.Max(0, pc - DisassemblyBytesBefore);
        var end = (ushort)Math.Min(0xFFFF, pc + DisassemblyBytesAfter);
        var bytes = await debugClient.GetMemoryAsync(start, end);
        var cpu = _workspace.ActiveProject?.ResolvedCc65Cpu ?? "6502";
        var cmos = Disassembler6502.IsCmos(cpu);
        var from = Disassembler6502.FindStartBefore(bytes, pc - start, DisassemblyBytesBefore, cmos);

        var dbgFile = _dbgFile;
        var project = _workspace.ActiveProject;
        (string FilePath, int Line)? lastLocation = null;
        var rows = new List<DisassemblyRow>();
        foreach (var instruction in Disassembler6502.Disassemble(bytes, start, from, maxCount: 48, cmos))
        {
            var notes = new List<string>();
            if (dbgFile?.FindLabelAt(instruction.Address) is { } label)
                notes.Add($"{label}:");
            if (instruction.Target is { } target && instruction.Mnemonic != "bra" && dbgFile?.FindLabelAt(target) is { } targetLabel)
                notes.Add($"-> {targetLabel}");
            if (dbgFile is not null && project is not null
                && dbgFile.FindProjectSourceLocationForAddress(instruction.Address, project.Directory) is { } location
                && location != lastLocation)
            {
                notes.Add($"{DebugPath(project, location.FilePath)}:{location.Line}");
                lastLocation = location;
            }
            rows.Add(new DisassemblyRow(instruction, instruction.Address == pc, string.Join("  ", notes)));
        }
        return (rows, $"PC ${pc:X4} - {cpu} instructions; cycles: * +1 on a page crossing, ** branch +1 taken, +1 more crossing a page.");
    }

    /// <summary>
    /// Memory tab: resolves an address the way Add Watch does (symbol, $hex or decimal) and shows
    /// the memory there - read now if execution is stopped, otherwise at the next stop.
    /// </summary>
    private void ShowMemoryAt(string expression)
    {
        if (!TryResolveWatchAddress(expression, out var address, out var error))
        {
            _memoryView.SetStatus(error);
            return;
        }

        _memoryAddress = address;
        _memorySnapshot = null;
        if (_debugClient is not { } debugClient || !_isDebugging || !_isStopped)
        {
            _memoryView.SetStatus($"${address:X4} will be shown when execution next stops.");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await debugClient.GetMemoryAsync(address, (ushort)Math.Min(0xFFFF, address + MemoryView.ByteCount - 1));
                _memorySnapshot = bytes;
                OnUiThread(() => _memoryView.Show(address, bytes, [], "As of this stop."));
            }
            catch (Exception ex)
            {
                OnUiThread(() => _memoryView.SetStatus($"Could not read memory: {ex.Message}"));
            }
        });
    }

    /// <summary>Opens the project source line an address belongs to - for a Disassembly row.</summary>
    private void OpenSourceForAddress(ushort address)
    {
        if (_dbgFile is not { } dbgFile || _workspace.ActiveProject is not { } project)
            return;
        if (dbgFile.FindProjectSourceLocationForAddress(address, project.Directory) is { } location)
            OpenSymbol((Path.Combine(project.Directory, location.FilePath), location.Line));
        else
            AppendOutputLine($"${address:X4} has no source line in this project.");
    }

    /// <summary>
    /// Builds the active project (if needed), launches it in VICE with the binary monitor enabled,
    /// connects <see cref="_debugClient"/>, sets every enabled breakpoint (resolved to an address
    /// via <see cref="_dbgFile"/>), and starts it running. Requires <see cref="TedideProject.GenerateDebugInfo"/>
    /// to be on - without it there's no .dbg file to resolve breakpoints/addresses against.
    /// Every caller fires this and forgets it, so any exception is caught and reported here and
    /// whatever was already set up is torn down - otherwise e.g. VICE not being installed would
    /// leave the Debug tab showing a half-started session with no error at all.
    /// </summary>
    private async Task StartDebuggingAsync()
    {
        try
        {
            await StartDebuggingCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error starting debug session");
            Application.Invoke(() => AppendOutputLine($"Could not start debugging: {ex.Message}"));
            await EndDebugSessionAsync();
        }
    }

    private async Task StartDebuggingCoreAsync()
    {
        var project = _workspace.ActiveProject;
        if (project is null)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }
        if (!CheckStartupProjectRuns("debug"))
            return;
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
        _debugPanel.ClearHistory();

        var buildResult = await BuildActiveProjectAsync();
        if (buildResult is not { Succeeded: true })
            return;

        if (!File.Exists(project.ResolvedDebugInfoFile))
        {
            AppendOutputLine("Build succeeded but no debug info file was produced.");
            return;
        }
        _dbgFile = DbgFile.Parse(File.ReadAllText(project.ResolvedDebugInfoFile));
        _assemblyFrames.Clear(); // this build's generated .s files replace the last session's

        // Application.Invoke, not AppendOutputLine directly: Process.OutputDataReceived/
        // ErrorDataReceived (what ViceEmulator.Launch's onOutputLine ultimately wraps) fire on a
        // thread-pool thread, not the UI thread - confirmed via a real crash this line caused
        // ("Collection was modified; enumeration operation may not execute" inside TextView's own
        // draw, racing OutputView._lines against the UI thread's concurrent draw-time enumeration
        // of it). RunActiveProjectAsync's own _vice.Launch call already gets this right.
        var viceProcess = _vice.Launch(project, line => Application.Invoke(() => AppendOutputLine(line)), enableBinaryMonitor: true);

        OnUiThread(() => _debugPanel.SetStatus("Connecting to VICE..."));
        // VICE needs a moment to start listening on its binary monitor port after the process
        // starts - retry rather than failing on the first attempt. A fresh client per attempt: a
        // TcpClient whose connect has failed isn't reliably reusable. And give up straight away
        // if VICE has already exited (e.g. it rejected a ROM or command-line option) rather than
        // spending the whole retry budget knocking on a port nothing will ever open.
        for (var attempt = 0; attempt < 20 && _debugClient is null && !viceProcess.HasExited; attempt++)
        {
            var client = CreateDebugClient();
            try
            {
                await client.ConnectAsync();
                _debugClient = client;
                Log.Debug("Debug start: connected to VICE on attempt {Attempt}", attempt + 1);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                Log.Debug("Debug start: connect attempt {Attempt} failed: {Reason}", attempt + 1, ex.Message);
                await client.DisposeAsync();
                await Task.Delay(250);
            }
        }
        if (_debugClient is null)
        {
            var reason = viceProcess.HasExited
                ? $"VICE exited during startup (exit code {viceProcess.ExitCode}) - see its output above."
                : "Could not connect to VICE's binary monitor - is VICE installed and did it launch correctly?";
            Application.Invoke(() =>
            {
                AppendOutputLine(reason);
                _debugPanel.SetStatus("Not debugging.");
            });
            return;
        }

        _isDebugging = true;
        // Read-only for the whole session, in every tab - editing source while the compiled binary
        // it no longer matches is running would be misleading (BuildActiveProjectAsync above has
        // already saved everything).
        // Invoked: this continuation is past several awaits, so it isn't on the UI thread.
        Application.Invoke(() => _editorPane.ReadOnly = true);

        // Show the C source containing main() as the session comes up, before anything actually
        // runs - the same _main label every C program has, resolved back to its source location
        // the same way a checkpoint hit resolves the PC (see DbgFile.FindSourceLocationForAddress).
        // Application.Invoke, not a direct call: by this point StartDebuggingAsync has been through
        // several awaits (connecting to VICE), and unlike everything else in this method, OpenSymbol
        // touches Editor.Document - TextDocument enforces single-thread ownership (VerifyAccess),
        // and nothing guarantees this continuation resumed on the UI thread. A real crash, caught
        // via Windows Event Log after this exact code path took the whole process down with
        // "Call from invalid thread" during the next draw.
        var mainSymbol = _dbgFile.Symbols.FirstOrDefault(s => s.Name == "_main" && s.Type == "lab");
        Log.Debug("Debug start: _main at {Address}", mainSymbol?.Value);
        if (mainSymbol is { Value: { } mainAddress }
            && _dbgFile.FindSourceLocationForAddress(mainAddress) is { } mainLocation)
        {
            Log.Debug("Debug start: main() is at {File}:{Line}", mainLocation.FilePath, mainLocation.Line);
            Application.Invoke(() =>
            {
                var mainPath = Path.Combine(project.Directory, mainLocation.FilePath);
                OpenSymbol((mainPath, mainLocation.Line));
                CenterEditorOnLine(mainPath, mainLocation.Line);
            });
        }

        await _checkpointLock.WaitAsync();
        try
        {
            Log.Debug("Debug start: setting checkpoints");
            await SetAllEnabledCheckpointsAsync(_dbgFile, _debugClient);
        }
        finally
        {
            _checkpointLock.Release();
        }

        Log.Debug("Debug start: checkpoints set, continuing");
        Application.Invoke(() => _debugPanel.SetStatus("Running..."));
        await _debugClient.ContinueAsync();
    }

    /// <summary>A new, not-yet-connected monitor client with this shell's event handlers attached.</summary>
    private ViceMonitorClient CreateDebugClient()
    {
        var client = new ViceMonitorClient();
        client.CheckpointHit += OnCheckpointHit;
        client.EventHandlerFailed += ex => Log.Error(ex, "Debug event handler failed");
        // VICE closed (or crashed) under a live session: end it, so the editor becomes editable
        // again and the Debug panel stops claiming a session exists. Checked on the UI thread
        // against the *current* client, so a stale client from an earlier session does nothing.
        client.Disconnected += () => Application.Invoke(() =>
        {
            if (_debugClient != client || !_isDebugging)
                return;
            AppendOutputLine("VICE closed the debugging connection - debug session ended.");
            _ = EndDebugSessionAsync();
        });
        client.Resumed += pc => Application.Invoke(() =>
        {
            Log.Debug("Resumed event: PC={PC:X4}, _isStepping={IsStepping}", pc, _isStepping);
            // Stepping resumes and re-halts the CPU just like Continue does, so it raises this same
            // unsolicited Resumed event - without this guard, its queued "Running..." update could
            // land after StepDebuggingAsync's own "Stopped at ..." one, clobbering the status and
            // un-highlighting the line it had just moved to. (ShowStoppedAt also re-asserts
            // _isStopped, for a Resumed that slips in just after _isStepping is cleared.)
            if (_isStepping)
                return;

            _isStopped = false;
            SetDebugLine(null);
            _debugPanel.SetStatus("Running...");
            _editorPane.Editor.SetNeedsDraw();
        });
        return client;
    }

    /// <summary>Sets a VICE checkpoint for every currently-enabled breakpoint, recording each one's
    /// VICE-assigned checkpoint number in <see cref="_checkpointNumbers"/> so it can later be
    /// deleted again (see <see cref="SyncCheckpointsWithViceAsync"/>). Shared between the initial
    /// setup in <see cref="StartDebuggingAsync"/> and re-syncing after a breakpoint changes mid-session.
    /// Callers must hold <see cref="_checkpointLock"/>.</summary>
    private async Task SetAllEnabledCheckpointsAsync(DbgFile dbgFile, ViceMonitorClient debugClient)
    {
        // Snapshotted (ToList) before the first await - the UI thread can toggle a breakpoint
        // while this loop is waiting on VICE, which would otherwise throw "Collection was modified".
        foreach (var breakpoint in _breakpoints.Breakpoints.Where(b => b.Enabled).ToList())
        {
            var address = dbgFile.FindAddressForSourceLine(breakpoint.SourceFile, breakpoint.Line);
            if (address is { } addr)
            {
                var info = await debugClient.SetCheckpointAsync((ushort)addr);
                _checkpointNumbers[breakpoint] = info.Number;
                if (breakpoint.HasCondition)
                {
                    try
                    {
                        await debugClient.SetConditionAsync(info.Number, breakpoint.Condition!.Trim());
                    }
                    catch (Exception ex) when (ex is ViceMonitorException or ArgumentException)
                    {
                        // An unconditional stop where a conditional one was asked for would be a
                        // surprise mid-run - so the breakpoint sits this session out instead.
                        await debugClient.DeleteCheckpointAsync(info.Number);
                        _checkpointNumbers.Remove(breakpoint);
                        Application.Invoke(() => AppendOutputLine(
                            $"VICE rejected the condition \"{breakpoint.Condition}\" on {breakpoint.SourceFile}:{breakpoint.Line}, so that breakpoint is off for this session. "
                            + "Use VICE monitor syntax, e.g. A == $05 or @cpu:$d020 == $0e."));
                    }
                }
            }
            else
            {
                Application.Invoke(() => AppendOutputLine(
                    $"Could not resolve breakpoint {breakpoint.SourceFile}:{breakpoint.Line} to an address - it may be on a line with no compiled code."));
            }
        }
    }

    /// <summary>
    /// Re-synchronizes VICE's actual checkpoints with the current breakpoint set - called whenever
    /// a breakpoint is toggled/added/removed while a debug session is already running. Without
    /// this, checkpoints are only ever set once (at <see cref="StartDebuggingAsync"/>'s own setup)
    /// - VICE has no idea Tedide's breakpoint file changed afterward, so deleting or disabling a
    /// breakpoint mid-session left execution still stopping there (the bug this fixes). Deletes
    /// every checkpoint this app previously set and re-sets one for every currently-enabled
    /// breakpoint, rather than diffing precisely - simpler, and the cost is negligible for the
    /// handful of breakpoints a real debugging session has.
    /// </summary>
    private async Task SyncCheckpointsWithViceAsync()
    {
        if (_debugClient is not { } debugClient || _dbgFile is not { } dbgFile || !_isDebugging)
            return;

        await _checkpointLock.WaitAsync();
        try
        {
            // Re-checked under the lock: a Stop Debugging that ran while this call was waiting
            // has already deleted every checkpoint and disposed this client.
            if (!_isDebugging || _debugClient != debugClient)
                return;

            await DeleteAllCheckpointsAsync(debugClient);
            await SetAllEnabledCheckpointsAsync(dbgFile, debugClient);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error re-syncing breakpoints with VICE");
            Application.Invoke(() => AppendOutputLine($"Error updating breakpoints in the running debug session: {ex.Message}"));
        }
        finally
        {
            _checkpointLock.Release();
        }
    }

    /// <summary>Deletes every checkpoint this app has set in VICE (<see cref="_checkpointNumbers"/>)
    /// and forgets them. Individual failures are logged and skipped - VICE may already be gone, or
    /// may have dropped a checkpoint on its own (e.g. a "temporary" one). Callers must hold
    /// <see cref="_checkpointLock"/>.</summary>
    private async Task DeleteAllCheckpointsAsync(ViceMonitorClient debugClient)
    {
        foreach (var number in _checkpointNumbers.Values.ToList())
        {
            try
            {
                await debugClient.DeleteCheckpointAsync(number);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not delete checkpoint #{Number}", number);
            }
        }
        _checkpointNumbers.Clear();
    }

    /// <summary>Builds a "Stopped [in {function}] at {where}" status string, prepending the
    /// enclosing function name (via <see cref="_dbgFile"/>) when it resolves - e.g. code with no
    /// debug info at all (cc65's own runtime library) has no scope info, so this falls back to
    /// just "Stopped at {where}".</summary>
    /// <summary>
    /// A source path from the debug info, for display: as it is when relative (the startup project's
    /// own files), else relative to the startup project - a library project's sources are compiled by
    /// full path (see Cc65Toolchain.BuildCompileSteps), which made every stop in one a whole line of
    /// C:\Users\... . "../Gfx/src/gfx.c" matches how the breakpoint list writes it.
    /// </summary>
    private static string DebugPath(TedideProject project, string path) =>
        Path.IsPathRooted(path) ? Path.GetRelativePath(project.Directory, path).Replace('\\', '/') : path;

    private string FunctionAwareStoppedAt(ushort? pc, string where)
    {
        var function = pc is { } pcValue ? _dbgFile?.FindEnclosingFunctionName(pcValue) : null;
        return function is { } name ? $"Stopped in {name} at {where}" : $"Stopped at {where}";
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
        Log.Debug("CheckpointHit event: checkpoint #{Number}", args.Checkpoint.Number);
        Application.Invoke(async () =>
        {
            try
            {
                _isStopped = true;
                if (_debugClient is null)
                    return;

                var registers = await _debugClient.GetRegistersAsync();
                // Fetched here (still in the outer async continuation) rather than inside the
                // nested Invoke below - it's pure network I/O against VICE, same as GetRegistersAsync
                // just above, with none of the UI-thread affinity concerns that block touching
                // Editor/TextDocument state after an await (see the nested-Invoke comment below).
                var watchLines = await FormatWatchesAsync(_debugClient);
                var locals = await ReadLocalsAsync(_debugClient, registers);
                var views = await ReadDebugViewsAsync(_debugClient, registers);

                // Everything below touches Editor/TextDocument state, which enforces single-thread
                // ownership (TextDocument.VerifyAccess). Application.Invoke only guarantees the UI
                // thread for this lambda's synchronous prefix - the await just above means this
                // continuation is NOT guaranteed to still be on the UI thread, and Terminal.Gui
                // doesn't restore it. Confirmed via a real crash here ("Call from invalid thread"
                // inside OpenSymbol) - re-marshal explicitly with a nested Invoke rather than
                // assuming the outer one's thread-affinity survives an internal await.
                // "PC" is VICE's register name for the 6502 program counter on the main memspace -
                // confirmed against a live VICE 3.9 instance (its ids are assigned dynamically per
                // the binary monitor protocol docs, but this name was stable).
                Application.Invoke(() =>
                {
                    ShowStoppedAt(registers, watchLines, locals, registers["PC"], $" (checkpoint #{args.Checkpoint.Number})");
                    ShowDebugViews(views);
                    Log.Debug("CheckpointHit done: status is now {Status}", _debugPanel.StatusText);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling checkpoint hit");
                Application.Invoke(() => AppendOutputLine($"Error handling checkpoint hit: {ex.Message}"));
            }
        });
    }

    private async Task ContinueDebuggingAsync()
    {
        if (_debugClient is null || !_isDebugging)
            return;

        _isStopped = false;
        SetDebugLine(null);
        _debugPanel.SetStatus("Running...");
        _editorPane.Editor.SetNeedsDraw();
        await _debugClient.ContinueAsync();
    }

    /// <summary>
    /// Updates the Debug panel and editor to show where execution has stopped: registers, watches,
    /// the source line <paramref name="pc"/> resolves to (opened, centered and highlighted), and a
    /// "Stopped [in function] at file:line" status plus history entry, with
    /// <paramref name="statusSuffix"/> appended (e.g. which checkpoint fired). Falls back to a bare
    /// address when the PC has no source line. Must run on the UI thread - it touches
    /// Editor/TextDocument state, which enforces single-thread ownership.
    /// </summary>
    private void ShowStoppedAt(RegisterSnapshot registers, List<string> watchLines, IReadOnlyList<LocalRow> locals, ushort? pc, string statusSuffix)
    {
        _isStopped = true;
        _debugPanel.SetRegisters(registers);
        _debugPanel.SetWatches(watchLines);
        _debugPanel.SetLocals(locals);

        var project = _workspace.ActiveProject;
        // Project files only: a line record from cc65's runtime library can't be opened, and used
        // to move the current-line highlight to its line number in whatever file was open.
        var location = pc is { } pcForLookup && project is not null
            ? _dbgFile?.FindProjectSourceLocationForAddress(pcForLookup, project.Directory)
            : null;
        Log.Debug("Stopped: PC={PC:X4}, location={Location}",
            pc, location is { } loc ? $"{loc.FilePath}:{loc.Line}" : "(unresolved)");

        string status;
        if (project is not null && location is { } resolved)
        {
            var resolvedPath = Path.Combine(project.Directory, resolved.FilePath);
            OpenSymbol((resolvedPath, resolved.Line));
            CenterEditorOnLine(resolvedPath, resolved.Line);
            SetDebugLine((resolvedPath, resolved.Line));
            status = FunctionAwareStoppedAt(pc, $"{DebugPath(project, resolved.FilePath)}:{resolved.Line}") + statusSuffix;
        }
        else
        {
            SetDebugLine(null);
            status = (pc is { } pcv ? $"Stopped at ${pcv:X4}" : "Stopped.") + statusSuffix;
        }
        _debugPanel.SetStatus(status);
        _debugPanel.AddHistoryEntry(status);
        _editorPane.Editor.SetNeedsDraw();
    }

    /// <summary>The most instructions one Step will execute looking for the next source line
    /// before giving up and showing wherever it got to - an address with no line mapping reached by
    /// a path Step doesn't recognize would otherwise single-step forever.</summary>
    private const int MaxStepInstructions = 500;

    /// <summary>
    /// Steps by source line, not raw instruction: steps repeatedly until the resolved location
    /// changes from where it started. <paramref name="stepInto"/> false (Step Over) executes each
    /// subroutine call as a single instruction, so calls on the line run to completion. true (Step
    /// Into) follows calls into functions that have source; a call into code with no source at
    /// all - almost always cc65's runtime library, which the line itself calls constantly for
    /// things like argument pushing - is run to its return rather than stopped in, so stepping
    /// into a line with no user function calls on it behaves just like stepping over it.
    /// </summary>
    private async Task StepDebuggingAsync(bool stepInto)
    {
        if (_debugClient is not { } debugClient || _dbgFile is not { } dbgFile || !_isDebugging || !_isStopped
            || _workspace.ActiveProject is not { } project)
            return;

        // "Has source" means source in this project - cc65's runtime library has line records too
        // (its own .s files, and macro files at paths from the machine that built it), and Step
        // Into must run through that code, not stop in it. See FindProjectSourceLocationForAddress.
        (string FilePath, int Line)? SourceLocation(long address) =>
            dbgFile.FindProjectSourceLocationForAddress(address, project.Directory);

        // See the Resumed handler's own comment in StartDebuggingAsync for why this guard exists -
        // each single-step's own Resumed event must not touch UI state that this method (still
        // mid-loop) owns for the whole duration of the step.
        _isStepping = true;
        try
        {
            var startPc = (await debugClient.GetRegistersAsync())["PC"];
            var startLocation = startPc is { } s ? SourceLocation(s) : null;
            // main.c has 6 line records in HelloCBM.dbg against main.s's 22 for the same code -
            // cl65's generated .s intermediate is tracked at far finer granularity than the
            // original C source. Single-stepping from a C line legitimately passes through
            // addresses that only resolve to that intermediate (no surviving .c line record there -
            // see DbgFile.FindSourceLocationForAddress's own doc comment) before reaching the next
            // real C statement - those must be skipped, not reported as "the next line", or Step
            // stops one instruction early and shows the generated .s file instead of the .c one.
            var startedInAssembly = startLocation is null || IsAssemblySourceFile(startLocation.Value.FilePath);

            var reachedNewLine = false;
            for (var i = 0; i < MaxStepInstructions && !reachedNewLine; i++)
            {
                // StepAsync waits for VICE to actually stop again and hands back the PC from that
                // Stopped event - one round trip per instruction, no separate register read.
                var pc = await debugClient.StepAsync(stepOverSubroutines: !stepInto);
                var location = SourceLocation(pc);

                if (stepInto && location is null && startLocation is not null)
                {
                    // Stepped from source into code with none: run it back out. Execute Until
                    // Return stops after the *next* RTS, which is a nested call's if this routine
                    // makes any, so keep going until the PC is somewhere with source again.
                    while (location is null && i++ < MaxStepInstructions)
                    {
                        pc = await debugClient.ExecuteUntilReturnAsync();
                        location = SourceLocation(pc);
                    }
                }

                if (!startedInAssembly && location is { } candidate && IsAssemblySourceFile(candidate.FilePath))
                    continue;

                reachedNewLine = location is null || location != startLocation;
            }

            var registers = await debugClient.GetRegistersAsync();
            var watchLines = await FormatWatchesAsync(debugClient);
            var locals = await ReadLocalsAsync(debugClient, registers);
            var views = await ReadDebugViewsAsync(debugClient, registers);
            // Re-marshal onto the UI thread before touching Editor/TextDocument state - the awaits
            // above leave this continuation on whatever thread completed them (Terminal.Gui
            // installs no SynchronizationContext to bring it back).
            Application.Invoke(() =>
            {
                ShowStoppedAt(registers, watchLines, locals, registers["PC"],
                    reachedNewLine ? "" : $" (step limit of {MaxStepInstructions} instructions reached)");
                ShowDebugViews(views);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while stepping");
            Application.Invoke(() => AppendOutputLine($"Error while stepping: {ex.Message}"));
        }
        finally
        {
            _isStepping = false;
        }
    }

    private static bool IsAssemblySourceFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".s", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".asm", StringComparison.OrdinalIgnoreCase);
    }

    private async Task StopDebuggingAsync()
    {
        if (_debugClient is null)
            return;

        await EndDebugSessionAsync();
    }

    /// <summary>
    /// Tears down a debug session, however far it got - shared by Stop Debugging and by
    /// <see cref="StartDebuggingAsync"/>'s own failure path, where there may be no connected
    /// client yet at all.
    /// </summary>
    private async Task EndDebugSessionAsync()
    {
        // Cleared first so any SyncCheckpointsWithViceAsync already queued behind the lock
        // below bails out on its own re-check instead of re-arming checkpoints afterward.
        _isDebugging = false;
        _isStopped = false;

        if (_debugClient is { } debugClient)
        {
            // Deleted before resuming, not just forgotten - VICE keeps its checkpoints after the
            // connection closes, so the now-detached program would otherwise still halt at every
            // breakpoint, dropping the user into VICE's own monitor with no debugger attached.
            await _checkpointLock.WaitAsync();
            try
            {
                await DeleteAllCheckpointsAsync(debugClient);
            }
            finally
            {
                _checkpointLock.Release();
            }

            try { await debugClient.ContinueAsync(); }
            catch { /* VICE may already be gone - fine, we're tearing down the connection either way. */ }

            await debugClient.DisposeAsync();
        }

        _debugClient = null;
        _dbgFile = null;
        // A watch's address was resolved against this session's own _dbgFile - stale the moment
        // it's gone (a rebuild can shift where a symbol ends up), so watches are re-entered per
        // session rather than carried forward, same as WatchEntry's own doc comment says.
        _watches.Clear();
        // Re-marshal onto the UI thread - see OnCheckpointHit's own comment on why the awaits
        // above don't guarantee this continuation is still there.
        Application.Invoke(() =>
        {
            SetDebugLine(null);
            _debugPanel.SetStatus("Not debugging.");
            _debugPanel.SetRegisters(null);
            _debugPanel.SetWatches([]);
            _debugPanel.SetLocals([]);
            _debugPanel.SetCallStack([]);
            _debugPanel.ClearHistory();
            _disassemblyView.Show([], "Start debugging to see the code around the PC.");
            _memoryView.Show(0, [], [], "Start debugging, then enter a symbol or $address.");
            _memorySnapshot = null;
            // EditorPane keeps the editor read-only anyway while no file is open.
            _editorPane.ReadOnly = false;
            _editorPane.Editor.SetNeedsDraw();
        });
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
        LoadBreakpointsForActiveProject();
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
    /// Build > Build Project (F5): builds the startup project, after any libraries it references.
    /// Returns null (having already reported why) if none is loaded, another build is still
    /// running, or the build was cancelled via <see cref="CancelBuild"/> - callers (Run, Start
    /// Debugging) treat that the same as a failed build and stop there.
    /// </summary>
    private Task<BuildResult?> BuildActiveProjectAsync() =>
        BuildProjectsAsync(_workspace.ActiveProject is { } project ? [project] : []);

    /// <summary>Build > Build Solution: every project, each after the libraries it references.</summary>
    private Task<BuildResult?> BuildSolutionAsync() => BuildProjectsAsync(_workspace.Projects.ToList());

    /// <summary>
    /// Builds <paramref name="roots"/> and every library they reference, in dependency order (see
    /// <see cref="ProjectGraph.BuildOrder"/>). A project whose library failed is skipped rather
    /// than linked against a stale or missing .lib; the others still build. The result succeeds
    /// only if every project did, and carries every project's diagnostics, with absolute paths.
    /// </summary>
    private async Task<BuildResult?> BuildProjectsAsync(IReadOnlyList<TedideProject> roots)
    {
        if (roots.Count == 0)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return null;
        }
        // Two builds at once would have two sets of cl65 processes writing the same obj/ files.
        if (_buildCancellation is not null)
        {
            AppendOutputLine("A build is already running - wait for it, or use Build > Cancel Build.");
            return null;
        }

        // Building what's on disk after a failed save would silently build stale source - and
        // before this check, the save's exception simply vanished into this un-awaited task, so
        // F5 on a read-only file did nothing at all with no explanation.
        if (!SaveAll())
            return null;
        _outputView.Clear();
        _errorListView.SetDiagnostics([]);

        var solution = _workspace.Projects.ToList();
        IReadOnlyList<TedideProject> order;
        try
        {
            order = ProjectGraph.BuildOrder(solution, roots);
        }
        catch (InvalidDataException ex)
        {
            // A bad reference (missing, not a library, or a cycle) - nothing is built.
            var diagnostic = new BuildDiagnostic("", 0, DiagnosticSeverity.Error, ex.Message);
            AppendOutputLine($"------ Build FAILED: {ex.Message} ------");
            _errorListView.SetDiagnostics([diagnostic]);
            return new BuildResult(false, 1, [], [diagnostic], TimeSpan.Zero);
        }

        var cancellation = new CancellationTokenSource();
        _buildCancellation = cancellation;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outputLines = new List<string>();
        var diagnostics = new List<BuildDiagnostic>();
        var failed = new HashSet<TedideProject>();
        var succeeded = 0;
        var skipped = 0;
        try
        {
            foreach (var project in order)
            {
                var libraries = ProjectGraph.LinkedLibraries(solution, project);
                if (libraries.FirstOrDefault(failed.Contains) is { } failedLibrary)
                {
                    AppendOutputLine($"------ Skipped {project.Name}: {failedLibrary.Name} didn't build ------");
                    failed.Add(project);
                    skipped++;
                    continue;
                }

                AppendOutputLine($"------ Build started: {project.Name} ({project.Target.ToCl65Id()}{(project.IsLibrary ? ", library" : "")}) ------");
                var result = await _toolchain.BuildAsync(project, AppendOutputLine, cancellation.Token, libraries);
                outputLines.AddRange(result.RawOutputLines);
                // Each project's tools report paths relative to its own folder.
                diagnostics.AddRange(result.Diagnostics.Select(d => d with
                {
                    FilePath = d.FilePath.Length == 0 || Path.IsPathRooted(d.FilePath) ? d.FilePath : Path.GetFullPath(Path.Combine(project.Directory, d.FilePath)),
                    // Shown in the Error List's Project column - only worth one with several projects.
                    Project = solution.Count > 1 ? project.Name : null,
                }));

                if (result.Succeeded)
                {
                    succeeded++;
                    AppendOutputLine($"------ {project.Name}: build succeeded in {result.Duration.TotalSeconds:0.0}s ------");
                    if (new FileInfo(project.ResolvedOutputFile) is { Exists: true } outputFile)
                        AppendOutputLine($"{Path.GetFileName(project.ResolvedOutputFile)}: {outputFile.Length} bytes");
                }
                else
                {
                    failed.Add(project);
                    AppendOutputLine($"------ {project.Name}: build FAILED ({result.Errors.Count()} error(s)) in {result.Duration.TotalSeconds:0.0}s ------");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cc65Toolchain has already killed the cl65 process tree; what it left in obj/ is
            // partial, but the next build simply overwrites it.
            AppendOutputLine("------ Build cancelled ------");
            return null;
        }
        finally
        {
            _buildCancellation = null;
            cancellation.Dispose();
        }

        if (order.Count > 1)
            AppendOutputLine($"========== Build: {succeeded} succeeded, {failed.Count - skipped} failed, {skipped} skipped ==========");

        // Past the await, so not on the UI thread - see OnUiThread.
        OnUiThread(() =>
        {
            _errorListView.SetDiagnostics(diagnostics, DisplayPath);
            // Refreshes the Solution Explorer's "Generated Files" node - e.g. newly-written
            // assembler listings (see SolutionExplorerTree.AddGeneratedFilesNode) only appear once
            // the tree is rebuilt after this build actually wrote them.
            _solutionExplorer.Rebuild(_workspace);
            _symbolPanel.Refresh(_workspace.ActiveProject);
        });

        return new BuildResult(failed.Count == 0, failed.Count == 0 ? 0 : 1, outputLines, diagnostics, stopwatch.Elapsed);
    }

    /// <summary>Build > Cancel Build: stops the running build, if any (see <see cref="BuildActiveProjectAsync"/>).</summary>
    private void CancelBuild()
    {
        if (_buildCancellation is not { } cancellation)
        {
            AppendOutputLine("No build is running.");
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The build finished on its own between the check above and here - nothing left to cancel.
        }
    }

    /// <summary>Builds the active project, then launches it in the VICE emulator matching its target if the build succeeded.</summary>
    private async Task RunActiveProjectAsync()
    {
        ShowOutputTab();
        if (!CheckStartupProjectRuns("run"))
            return;

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
            AppendOutputLine($"------ Launched {ViceEmulator.ExecutableNameFor(project.Target, project.EnableSuperCpu)} ------");
        }
        catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException or InvalidOperationException)
        {
            AppendOutputLine($"Could not launch emulator: {ex.Message}");
        }
    }

    /// <summary>
    /// False, having said why, if the startup project is a library - there's nothing to
    /// <paramref name="action"/>. A library only becomes code that runs inside a program.
    /// </summary>
    private bool CheckStartupProjectRuns(string action)
    {
        if (_workspace.ActiveProject is not { IsLibrary: true } library)
            return true;
        AppendOutputLine($"{library.Name} is a library, so there's nothing to {action}. Right-click an application in the "
            + "Solution Explorer and choose Set as Startup Project.");
        return false;
    }

    /// <summary>Build > Clean Project: deletes the startup project's build artifacts (object files
    /// and its output) without rebuilding.</summary>
    private void CleanActiveProject() => CleanProjects(_workspace.ActiveProject is { } project ? [project] : []);

    /// <summary>Build > Clean Solution: <see cref="CleanActiveProject"/> for every project.</summary>
    private void CleanSolution() => CleanProjects(_workspace.Projects.ToList());

    private void CleanProjects(IReadOnlyList<TedideProject> projects) => Guard("Cleaning", () => CleanProjectsCore(projects));

    private void CleanProjectsCore(IReadOnlyList<TedideProject> projects)
    {
        if (projects.Count == 0)
        {
            AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        var total = 0;
        foreach (var project in projects)
        {
            var removed = _toolchain.Clean(project);
            AppendOutputLine($"------ Clean: {project.Name} ------");
            if (removed.Count == 0)
                AppendOutputLine("Nothing to clean.");
            foreach (var path in removed)
                AppendOutputLine($"Deleted {Path.GetFileName(path)}");
            total += removed.Count;
        }
        AppendOutputLine($"------ Clean complete: {total} file(s) removed ------");

        // Drops (or shrinks) the Solution Explorer's "Generated Files" node if the assembler
        // listings it was showing are among the files just deleted.
        _solutionExplorer.Rebuild(_workspace);
    }

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
            && _breakpoints.Breakpoints.RemoveAll(b => IsSameOrInsideDirectory(Path.GetFullPath(Path.Combine(startup.Directory, b.SourceFile)), project.Directory)) > 0)
        {
            _breakpoints.Save(startup.ResolvedBreakpointsFile);
            RefreshBreakpointHighlights();
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
        LoadBreakpointsForActiveProject();
        _symbolPanel.Refresh(_workspace.ActiveProject);
    }

    /// <summary>
    /// Opens the settings dialog for the active project - its Settings tab (name, target, output
    /// file, extra cl65 arguments) and Optimizer tab (cc65 optimization preset). Rebuilds the
    /// Solution Explorer afterward since its project node label includes the name and target,
    /// which the dialog may have just changed. If the name changed, also renames the project's
    /// folder (and its .tproj file) on disk to match - see <see cref="RenameProjectFolder"/>.
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

    /// <summary>
    /// Runs <paramref name="action"/> right away if already on the UI thread, otherwise posts it
    /// there via Application.Invoke. Needed by any UI update that follows an await: Terminal.Gui
    /// 2.x installs no SynchronizationContext, so an async method started from a menu or key
    /// handler carries on after its first await on whichever thread-pool thread completed the
    /// awaited task, not the UI thread - where touching a view races its draw (confirmed crashes:
    /// "Collection was modified" in OutputView, "Call from invalid thread" in TextDocument).
    /// Posted work runs in order, after anything already queued.
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
