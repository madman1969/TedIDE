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

/// <summary>The per-user settings files <see cref="AppShell"/> reads and writes.</summary>
internal sealed record AppShellSettings(string RecentProjectsFile, string LayoutFile, string ToolchainFile, string EditorFile)
{
    public static AppShellSettings Default { get; } =
        new(RecentProjectsSettings.DefaultFilePath, LayoutSettings.DefaultFilePath, ToolchainSettings.DefaultFilePath, EditorSettings.DefaultFilePath);
}

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
    private readonly IDialogs _dialogs;
    private readonly AppShellSettings _settings;
    private readonly Workspace _workspace = new();

    /// <summary>Git status for the Solution Explorer, the status bar and the Git tab - see <see cref="GitTracker"/>.</summary>
    private readonly GitTracker _git;
    private readonly GitChangesView _gitView = new();
    private View _gitTab = null!;



    // Not inline-initialized (unlike its siblings below) - its BinDirectory depends on
    // ToolchainSettings, applied in the constructor via ApplyToolchainSettings (also reapplied
    // after every ProjectSettingsDialog save - see ShowProjectSettings).
    private readonly ViceEmulator _vice = new();
    private readonly RecentProjectsSettings _recentProjects;

    /// <summary>Navigate Backward/Forward (Alt+Left/Alt+Right) - see <see cref="NavigationCommands.RecordJump"/>.</summary>
    private readonly NavigationHistory _navigationHistory = new();
    private readonly LayoutSettings _layoutSettings;
    private readonly EditorSettings _editorSettings;

    /// <summary>The File menu's "Recent Projects and Solutions" item - kept as a field so its
    /// SubMenu can be rebuilt in place whenever <see cref="_recentProjects"/> changes.</summary>
    private MenuItem _recentProjectsMenuItem = null!;

    private readonly SolutionExplorerTree _solutionExplorer = new();

    /// <summary>View > Document Outline: the shown file's structure, on the left pane's second tab.</summary>
    private readonly DocumentOutlineView _outline = new();
    private readonly DocumentOutlineTracking _outlineTracking;

    /// <summary>The left pane's tabs: the Solution Explorer, then the Document Outline.</summary>
    private PaneTabs _leftTabs = null!;
    private const int OutlineTab = 1;
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
    /// <summary>Breakpoints and the VICE debug session - see <see cref="DebugSession"/>.</summary>
    private readonly DebugSession _debug;
    /// <summary>Find, Go To, references, rename and Navigate Backward/Forward - see <see cref="NavigationCommands"/>.</summary>
    private readonly NavigationCommands _navigation;
    /// <summary>Build, Run and Clean - see <see cref="BuildCommands"/>.</summary>
    private readonly BuildCommands _build;
    /// <summary>The Git tab, blame, change bars, Compare and Blame - see <see cref="GitIntegration"/>.</summary>
    private readonly GitIntegration _gitIntegration;
    /// <summary>Checking as you type - see <see cref="LiveErrorChecking"/>.</summary>
    private readonly LiveErrorChecking _liveErrors;
    /// <summary>Suggestions and signatures while typing - see <see cref="CodeCompletion"/>.</summary>
    private readonly CodeCompletion _completion;
    /// <summary>Projects, files and tabs - see <see cref="ProjectCommands"/>.</summary>
    private readonly ProjectCommands _projects;
    private Tabs _outputTabs = null!;
    private View _outputTab = null!;
    private View _debugTab = null!;
    private View _referencesTab = null!;
    private readonly EditorMenuBar _menuBar;
    private readonly EditorStatusBar _statusBar;

    public AppShell()
        : this(AppShellSettings.Default, new TerminalDialogs())
    {
    }

    /// <param name="settings">The per-user settings files - the user's own, or a test's.</param>
    /// <param name="dialogs">Shows the window's dialogs and message boxes - for real, or a test's answers.</param>
    internal AppShell(AppShellSettings settings, IDialogs dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        _recentProjects = RecentProjectsSettings.Load(settings.RecentProjectsFile);
        _layoutSettings = LayoutSettings.Load(settings.LayoutFile);
        _editorSettings = EditorSettings.Load(settings.EditorFile);
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
        _outlineTracking = new DocumentOutlineTracking(_workspace, _editorPane, _outline, () => _leftTabs?.Selected == OutlineTab);
        _liveErrors = new LiveErrorChecking(this, _workspace, _editorPane, _errorListView, _navigation.DisplayPath)
        {
            Enabled = _editorSettings.CheckAsYouType,
        };
        _completion = new CodeCompletion(this, _workspace, _editorPane)
        {
            Enabled = _editorSettings.CodeCompletion,
        };
        _projects = new ProjectCommands(this, _workspace, _editorPane, _solutionExplorer, _symbolPanel, _debug, _gitIntegration,
            _navigationHistory, _recentProjects, _vice, settings.ToolchainFile);
        _projects.RecentProjectsChanged += RefreshRecentProjectsMenu;
        _projects.FileSaved += _ =>
        {
            _git.RequestRefresh();
            // A saved header changes what the shown file compiles against, and what it can see.
            _liveErrors.RequestCheck();
            _completion.FileSaved();
        };
        Width = Dim.Fill();
        Height = Dim.Fill();

        // At startup, an unset Cc65Home must not clobber a CC65_HOME the user has set some other
        // way (shell profile, system env) before launching Tedide - only an explicit edit via the
        // CC65 tab's Save (see ShowProjectSettings) is allowed to unset it, hence allowUnsettingCc65Home: false here.
        _projects.ApplyToolchainSettings(ToolchainSettings.Load(settings.ToolchainFile), allowUnsettingCc65Home: false);

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
        _solutionExplorer.FileActivated += _projects.OpenFile;
        _solutionExplorer.NewFileRequested += (directory, project) => _projects.NewFile(directory, project);
        _solutionExplorer.AddExistingItemRequested += (directory, project) => _projects.AddExistingItem(directory, project);
        _solutionExplorer.RenameFileRequested += _projects.RenameFile;
        _solutionExplorer.DeleteFileRequested += _projects.DeleteFile;
        _solutionExplorer.AddNewProjectRequested += _projects.AddNewProject;
        _solutionExplorer.AddExistingProjectRequested += _projects.AddExistingProject;
        _solutionExplorer.BuildSolutionRequested += () => Fire(_build.BuildSolutionAsync());
        _solutionExplorer.CleanSolutionRequested += _build.CleanSolution;
        _solutionExplorer.SetStartupProjectRequested += _projects.SetStartupProject;
        _solutionExplorer.BuildProjectRequested += project => Fire(_build.BuildProjectsAsync([project]));
        _solutionExplorer.CleanProjectRequested += project => _build.CleanProjects([project]);
        _solutionExplorer.ProjectSettingsRequested += _projects.ShowProjectSettings;
        _solutionExplorer.RemoveProjectRequested += _projects.RemoveProject;
        _solutionExplorer.DeleteProjectRequested += _projects.DeleteProject;
        _outline.NodeActivated += GoToOutlineNode;
        _outline.FindReferencesRequested += node =>
        {
            GoToOutlineNode(node);
            _navigation.FindAllReferences();
        };
        _outline.RenameRequested += node =>
        {
            GoToOutlineNode(node);
            _navigation.RenameSymbol();
        };
        // Solution | Outline, as Visual Studio docks its Document Outline beside the Solution
        // Explorer. The frame's own border serves both, and its title names the one shown.
        _leftTabs = new PaneTabs(bordered: false, ("Solution", _solutionExplorer), ("Outline", _outline))
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        _leftTabs.SelectionChanged += index =>
        {
            explorerFrame.Title = index == OutlineTab ? "Document Outline" : "Solution Explorer";
            if (index == OutlineTab)
                _outlineTracking.Shown();
        };
        explorerFrame.Add(_leftTabs);

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
        _editorPane.CloseRequested += path => _projects.CloseFile(path);
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

        // Checking as you type's underlines first: the breakpoint and debug-line highlights below
        // recolour whole lines, and should win on a line that has both.
        _editorPane.Editor.LineTransformers.Add(_liveErrors.LineTransformer);
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
    /// Program.cs, right after <c>_dialogs.Run(shell)</c> returns - i.e. when the user quits.
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
    /// <see cref="ProjectCommands.SaveLastOpenFileForActiveProject"/>) so it's reopened next time this same
    /// project loads (see <see cref="ProjectCommands.LoadLastOpenFileForActiveProject"/>). Called once, from
    /// Program.cs, right after <c>_dialogs.Run(shell)</c> returns - i.e. when the user quits -
    /// alongside <see cref="SaveLayoutSettings"/>.</summary>
    public void SaveSessionState() => LogOnlyOnExit("Saving the session", _projects.SaveLastOpenFileForActiveProjectCore);

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
            new MenuItem("_New Project...", "", _projects.NewProject, Key.N.WithCtrl) { BindKeyToApplication = true },
            new MenuItem("_Open Project...", "", _projects.OpenProject, Key.O.WithCtrl) { BindKeyToApplication = true },
            // No key: Visual Studio's Ctrl+Shift+O arrives as Ctrl+O in Windows Terminal.
            new MenuItem("Open _File...", "", _projects.OpenFiles, Key.Empty),
            _recentProjectsMenuItem,
            new MenuItem("Close _Project", "", _projects.CloseSolution, Key.Empty),
            new Line(),
            new MenuItem("_Save", "", () => _projects.SaveActive(), Key.S.WithCtrl),
            // No key: Ctrl+Shift+S arrives as Ctrl+S in Windows Terminal (see FindInFilesKey).
            new MenuItem("Save A_ll", "", () => _projects.SaveAll(), Key.Empty),
            // Ctrl+W as in VS Code; Visual Studio's own Ctrl+F4 works too (OnKeyDown).
            new MenuItem("_Close File", "", _projects.CloseActiveFile, Key.W.WithCtrl) { BindKeyToApplication = true },
            new MenuItem("Close A_ll Files", "", _projects.CloseAllFiles, Key.Empty),
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
            new("_Settings...", "", _projects.ShowProjectSettings, Key.Empty),
        });

        // As in Visual Studio's Git menu: setting a repository up, then the Git tab for the rest.
        // "G_it" - Alt+G is the bottom pane's Git tab, which would win over the menu.
        var gitMenu = new MenuBarItem("G_it", new List<View>
        {
            new MenuItem("Create _Repository...", "", () => Fire(_gitIntegration.CreateRepositoryAsync()), Key.Empty),
            new MenuItem("Add Re_mote...", "", () => Fire(_gitIntegration.AddRemoteAsync()), Key.Empty),
            new Line(),
            new MenuItem("Git _Changes", "", () => ShowPane(_gitView), Key.Empty),
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
        viewMenuItems.AddAt(1, new MenuItem("Document Ou_tline", "", ShowDocumentOutline, DocumentOutlineKey));
        viewMenuItems.AddAt(2, new MenuItem("_Output", "", ShowOutputTab, Key.Empty));
        viewMenuItems.AddAt(3, new MenuItem("_Error List", "", () => ShowPane(_errorListView), Key.Empty));
        viewMenuItems.AddAt(4, new MenuItem("_Git Changes", "", () => ShowPane(_gitView), Key.Empty));
        viewMenuItems.AddAt(5, new Line());
        viewMenuItems.AddAt(6, BuildCheckAsYouTypeMenuItem());
        viewMenuItems.AddAt(7, BuildCodeCompletionMenuItem());
        // No keys: Visual Studio's are Ctrl+M chords, and a terminal sends Ctrl+M as Enter.
        viewMenuItems.AddAt(8, new Line());
        viewMenuItems.AddAt(9, new MenuItem("Co_llapse All Folds", "", () => _editorPane.SetAllFolded(true), Key.Empty));
        viewMenuItems.AddAt(10, new MenuItem("E_xpand All Folds", "", () => _editorPane.SetAllFolded(false), Key.Empty));
        menuBar.Menus = [fileMenu, menuBar.EditMenu, menuBar.ViewMenu, buildMenu, debugMenu, gitMenu, projectMenu, themeMenu, helpMenu];
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

    /// <summary>Visual Studio's own key for its Document Outline.</summary>
    private static readonly Key DocumentOutlineKey = Key.T.WithCtrl.WithAlt;
    private static readonly Key CallStackKey = Key.C.WithCtrl.WithAlt;
    private static readonly Key BreakpointsWindowKey = Key.B.WithCtrl.WithAlt;
    private static readonly Key RegistersKey = Key.G.WithCtrl.WithAlt;
    private static readonly Key LocalsKey = Key.V.WithCtrl.WithAlt;
    private static readonly Key WatchKey = Key.W.WithCtrl.WithAlt;
    private static readonly Key MemoryKey = Key.M.WithCtrl.WithAlt;
    private static readonly Key DisassemblyKey = Key.D.WithCtrl.WithAlt;

    /// <summary>View > Check As You Type: a check box like the editor's own toggles below it, turning
    /// <see cref="LiveErrorChecking"/> on or off and remembering the choice.</summary>
    private MenuItem BuildCheckAsYouTypeMenuItem()
    {
        var checkBox = new CheckBox
        {
            Title = "Check _As You Type",
            Value = _editorSettings.CheckAsYouType ? CheckState.Checked : CheckState.UnChecked,
            CanFocus = false,
        };
        checkBox.ValueChanged += (_, e) => SetCheckAsYouType(e.NewValue == CheckState.Checked);
        return new MenuItem { CommandView = checkBox };
    }

    /// <summary>View > Code Completion: the same, for <see cref="CodeCompletion"/>.</summary>
    private MenuItem BuildCodeCompletionMenuItem()
    {
        var checkBox = new CheckBox
        {
            Title = "_Code Completion",
            Value = _editorSettings.CodeCompletion ? CheckState.Checked : CheckState.UnChecked,
            CanFocus = false,
        };
        checkBox.ValueChanged += (_, e) => SetCodeCompletion(e.NewValue == CheckState.Checked);
        return new MenuItem { CommandView = checkBox };
    }

    internal void SetCodeCompletion(bool on)
    {
        _completion.Enabled = on;
        _editorSettings.CodeCompletion = on;
        Guard("Saving the editor settings", _editorSettings.Save);
    }

    internal void SetCheckAsYouType(bool on)
    {
        _liveErrors.Enabled = on;
        _editorSettings.CheckAsYouType = on;
        Guard("Saving the editor settings", _editorSettings.Save);
    }

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
        // Esc never quits from the main window. It's Terminal.Gui's default quit key, so an Esc
        // pressed to close the completion list a moment after it had closed, or out of habit,
        // closed the whole IDE. Ctrl+Q and File > Quit still quit; menus and dialogs get their Esc
        // before it reaches here.
        if (key == Key.Esc)
            return true;

        Action? action = null;
        if (key == Key.F5.WithShift)
            action = () => Fire(_debug.StopDebuggingAsync());
        else if (key == Key.F9.WithCtrl)
            action = _debug.EnableBreakpointAtCursor;
        else if (key == Key.F4.WithCtrl)
            action = _projects.CloseActiveFile;
        else if (key == Key.Q.WithCtrl)
            action = Quit;
        // Also on the File menu, but a menu item's key only works while its menu is open.
        else if (key == Key.N.WithCtrl)
            action = _projects.NewProject;
        else if (key == Key.O.WithCtrl)
            action = _projects.OpenProject;
        else if (key == Key.W.WithCtrl)
            action = _projects.CloseActiveFile;
        else if (key == SolutionExplorerKey)
            action = ShowSolutionExplorer;
        else if (key == DocumentOutlineKey)
            action = ShowDocumentOutline;
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
        statusBar.Add(new Shortcut(Key.S.WithCtrl, "Save", () => _projects.SaveActive()));
        statusBar.Add(new Shortcut(Key.G.WithCtrl, "Go To", _navigation.ShowGoToLine));
        statusBar.X = 0;
        statusBar.Y = Pos.AnchorEnd(1);
        statusBar.Width = Dim.Fill();
        return statusBar;
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

    internal List<MenuItem> BuildRecentProjectsMenuItems()
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
            () => _projects.OpenProjectOrSolution(path),
            Key.Empty).WithLiteralHelpText()).ToList();
    }


    /// <summary>
    /// Brings everything tied to "the file in the editor" up to date after a tab switch, open,
    /// close or rename: the frame title, the status bar's language, the breakpoint highlights,
    /// and the current-debug-line highlight, which only belongs on the file execution stopped in.
    /// </summary>
    private void OnActiveDocumentChanged()
    {
        _gitIntegration.ActiveDocumentChanged();
        _liveErrors.ActiveDocumentChanged();
        _completion.ActiveDocumentChanged();
        _outlineTracking.ActiveDocumentChanged();
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
        _dialogs.Run(new AboutDialog());
    }

    /// <summary>File > Quit (Ctrl+Q): asks about any unsaved changes, then exits.</summary>
    private void Quit()
    {
        if (_projects.ConfirmCloseFiles(_editorPane.OpenPaths))
            Application.RequestStop(this);
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
            OnUiThread(() => _dialogs.ErrorQuery("Error", $"{action} failed:\n{ex.Message}", ["OK"]));
            return false;
        }
    }

    internal static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or NotSupportedException or System.Security.SecurityException
        // Invalid path characters surface as a plain ArgumentException; its null/out-of-range
        // subclasses would be bugs, not bad input.
        || (ex is ArgumentException && ex is not ArgumentNullException and not ArgumentOutOfRangeException);


    // For tests: what the commands above work on and report.
    internal Workspace Workspace => _workspace;
    internal ProjectCommands Projects => _projects;
    internal EditorPane EditorPane => _editorPane;
    internal RecentProjectsSettings RecentProjects => _recentProjects;
    internal DebugSession Debug => _debug;
    internal CodeCompletion Completion => _completion;
    internal DocumentOutlineView Outline => _outline;
    internal PaneTabs LeftTabs => _leftTabs;
    internal string OutputText => _outputView.Text;

    /// <summary>Safe to call from any thread - see <see cref="OnUiThread"/>.</summary>
    private void AppendOutputLine(string line) => OnUiThread(() => _outputView.AppendLine(line));

    void IShell.AppendOutputLine(string line) => AppendOutputLine(line);
    void IShell.Fire(Task task, string what) => Fire(task, what);
    bool IShell.Guard(string action, Action body) => Guard(action, body);
    void IShell.OpenFile(string path) => _projects.OpenFile(path);
    bool IShell.SaveFile(string path) => _projects.SaveFile(path);
    bool IShell.SaveAll() => _projects.SaveAll();
    void IShell.OpenProjectOrSolution(string path) => _projects.OpenProjectOrSolution(path);
    void IShell.ShowOutputTab() => ShowOutputTab();
    void IShell.BringOutputForward() => _outputTabs.Value = _outputTab;
    void IShell.ShowPane(View content) => ShowPane(content);

    ViceEmulator IDebugSessionHost.Vice => _vice;
    IDialogs IDebugSessionHost.Dialogs => _dialogs;
    IDialogs IShell.Dialogs => _dialogs;
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
    private void OnUiThread(Action action)
    {
        if (Environment.CurrentManagedThreadId == _uiThreadId)
            action();
        else if (_uiContext is not null)
            _uiContext.Post(_ => action(), null);
        else
            Application.Invoke(action);
    }

    /// <summary>The thread this window was made on - the UI thread - and its synchronization
    /// context: <see cref="UiSynchronizationContext"/>, posting through Terminal.Gui's main loop, in
    /// the app; a test's own UI thread in a test.</summary>
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

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

    private void ShowSolutionExplorer()
    {
        _leftTabs.Select(0);
        _solutionExplorer.SetFocus();
    }

    /// <summary>View > Document Outline: the left pane's Outline tab, with the focus on it.</summary>
    internal void ShowDocumentOutline()
    {
        _leftTabs.Select(OutlineTab);
        _outline.Tree.SetFocus();
    }

    /// <summary>Goes to an outline node's symbol in the editor, recorded for Navigate Backward -
    /// leaving the focus in the outline after a single click, so the next click needs no switching back.</summary>
    private void GoToOutlineNode(Tedide.Core.Navigation.OutlineNode node, bool keepFocus = false)
    {
        _navigation.NavigateTo(node.Definition.FilePath, node.Definition.Line, node.Definition.Column, node.Definition.Name.Length);
        if (keepFocus)
            _outline.Tree.SetFocus();
    }

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
