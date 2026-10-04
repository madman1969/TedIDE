using System.Diagnostics;
using Serilog;
using Tedide.App.Views;
using Tedide.Build;
using Tedide.Core;
using Tedide.Theming;
using Terminal.Gui.App;

namespace Tedide.App;

/// <summary>
/// Build, Run and Clean: builds projects in dependency order with cl65, fills the Output and Error
/// List tabs, runs the startup project in VICE, and cleans build outputs. Calls back into the shell
/// through <see cref="IShell"/>.
/// </summary>
internal sealed class BuildCommands(IShell shell, Workspace workspace, NavigationCommands navigation, ViceEmulator vice,
    OutputView outputView, ErrorListView errorListView, SolutionExplorerTree solutionExplorer, SymbolPanelView symbolPanel)
{
    private readonly IShell _shell = shell;
    private readonly Workspace _workspace = workspace;
    private readonly NavigationCommands _navigation = navigation;
    private readonly ViceEmulator _vice = vice;
    private readonly OutputView _outputView = outputView;
    private readonly ErrorListView _errorListView = errorListView;
    private readonly SolutionExplorerTree _solutionExplorer = solutionExplorer;
    private readonly SymbolPanelView _symbolPanel = symbolPanel;

    private readonly Cc65Toolchain _toolchain = new();

    /// <summary>Non-null only while a build is running - see <see cref="BuildActiveProjectAsync"/>
    /// and <see cref="CancelBuild"/>.</summary>
    private volatile CancellationTokenSource? _buildCancellation;

    /// <summary>
    /// Build > Build Project: builds the startup project, after any libraries it references.
    /// Returns null (having already reported why) if none is loaded, another build is still
    /// running, or the build was cancelled via <see cref="CancelBuild"/> - callers (Run, Start
    /// Debugging) treat that the same as a failed build and stop there.
    /// </summary>
    public Task<BuildResult?> BuildActiveProjectAsync() =>
        BuildProjectsAsync(_workspace.ActiveProject is { } project ? [project] : []);

    /// <summary>Build > Build Solution: every project, each after the libraries it references.</summary>
    public Task<BuildResult?> BuildSolutionAsync() => BuildProjectsAsync(_workspace.Projects.ToList());

    /// <summary>
    /// Builds <paramref name="roots"/> and every library they reference, in dependency order (see
    /// <see cref="ProjectGraph.BuildOrder"/>). A project whose library failed is skipped rather
    /// than linked against a stale or missing .lib; the others still build. The result succeeds
    /// only if every project did, and carries every project's diagnostics, with absolute paths.
    /// </summary>
    public async Task<BuildResult?> BuildProjectsAsync(IReadOnlyList<TedideProject> roots)
    {
        if (roots.Count == 0)
        {
            _shell.AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return null;
        }
        // Two builds at once would have two sets of cl65 processes writing the same obj/ files.
        if (_buildCancellation is not null)
        {
            _shell.AppendOutputLine("A build is already running - wait for it, or use Build > Cancel Build.");
            return null;
        }

        // Building what's on disk after a failed save would silently build stale source - and
        // before this check, the save's exception simply vanished into this un-awaited task, so
        // Building from a read-only file did nothing at all with no explanation.
        if (!_shell.SaveAll())
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
            _shell.AppendOutputLine($"------ Build FAILED: {ex.Message} ------");
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
                    _shell.AppendOutputLine($"------ Skipped {project.Name}: {failedLibrary.Name} didn't build ------");
                    failed.Add(project);
                    skipped++;
                    continue;
                }

                _shell.AppendOutputLine($"------ Build started: {project.Name} ({project.Target.ToCl65Id()}{(project.IsLibrary ? ", library" : "")}) ------");
                var result = await _toolchain.BuildAsync(project, _shell.AppendOutputLine, cancellation.Token, libraries);
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
                    _shell.AppendOutputLine($"------ {project.Name}: build succeeded in {result.Duration.TotalSeconds:0.0}s ------");
                    if (new FileInfo(project.ResolvedOutputFile) is { Exists: true } outputFile)
                        _shell.AppendOutputLine($"{Path.GetFileName(project.ResolvedOutputFile)}: {outputFile.Length} bytes");
                }
                else
                {
                    failed.Add(project);
                    _shell.AppendOutputLine($"------ {project.Name}: build FAILED ({result.Errors.Count()} error(s)) in {result.Duration.TotalSeconds:0.0}s ------");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cc65Toolchain has already killed the cl65 process tree; what it left in obj/ is
            // partial, but the next build simply overwrites it.
            _shell.AppendOutputLine("------ Build cancelled ------");
            return null;
        }
        finally
        {
            _buildCancellation = null;
            cancellation.Dispose();
        }

        if (order.Count > 1)
            _shell.AppendOutputLine($"========== Build: {succeeded} succeeded, {failed.Count - skipped} failed, {skipped} skipped ==========");

        _errorListView.SetDiagnostics(diagnostics, _navigation.DisplayPath);
        // Refreshes the Solution Explorer's "Generated Files" node - e.g. newly-written
        // assembler listings (see SolutionExplorerTree.AddGeneratedFilesNode) only appear once
        // the tree is rebuilt after this build actually wrote them.
        _solutionExplorer.Rebuild(_workspace);
        _symbolPanel.Refresh(_workspace.ActiveProject);

        return new BuildResult(failed.Count == 0, failed.Count == 0 ? 0 : 1, outputLines, diagnostics, stopwatch.Elapsed);
    }

    /// <summary>Build > Cancel Build: stops the running build, if any (see <see cref="BuildActiveProjectAsync"/>).</summary>
    public void CancelBuild()
    {
        if (_buildCancellation is not { } cancellation)
        {
            _shell.AppendOutputLine("No build is running.");
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
    public async Task RunActiveProjectAsync()
    {
        _shell.ShowOutputTab();
        if (!CheckStartupProjectRuns("run"))
            return;

        var result = await BuildActiveProjectAsync();
        if (result is null)
            return;

        if (!result.Succeeded)
        {
            _shell.AppendOutputLine("Not launching emulator - build failed.");
            return;
        }

        var project = _workspace.ActiveProject!;
        try
        {
            _vice.Launch(project, onOutputLine: line => Application.Invoke(() => _shell.AppendOutputLine(line)));
            _shell.AppendOutputLine($"------ Launched {ViceEmulator.ExecutableNameFor(project.Target, project.EnableSuperCpu)} ------");
        }
        catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException or InvalidOperationException)
        {
            _shell.AppendOutputLine($"Could not launch emulator: {ex.Message}");
        }
    }

    /// <summary>
    /// False, having said why, if the startup project is a library - there's nothing to
    /// <paramref name="action"/>. A library only becomes code that runs inside a program.
    /// </summary>
    public bool CheckStartupProjectRuns(string action)
    {
        if (_workspace.ActiveProject is not { IsLibrary: true } library)
            return true;
        _shell.AppendOutputLine($"{library.Name} is a library, so there's nothing to {action}. Right-click an application in the "
            + "Solution Explorer and choose Set as Startup Project.");
        return false;
    }

    /// <summary>Build > Clean Project: deletes the startup project's build artifacts (object files
    /// and its output) without rebuilding.</summary>
    public void CleanActiveProject() => CleanProjects(_workspace.ActiveProject is { } project ? [project] : []);

    /// <summary>Build > Clean Solution: <see cref="CleanActiveProject"/> for every project.</summary>
    public void CleanSolution() => CleanProjects(_workspace.Projects.ToList());

    public void CleanProjects(IReadOnlyList<TedideProject> projects) => _shell.Guard("Cleaning", () => CleanProjectsCore(projects));

    private void CleanProjectsCore(IReadOnlyList<TedideProject> projects)
    {
        if (projects.Count == 0)
        {
            _shell.AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        var total = 0;
        foreach (var project in projects)
        {
            var removed = _toolchain.Clean(project);
            _shell.AppendOutputLine($"------ Clean: {project.Name} ------");
            if (removed.Count == 0)
                _shell.AppendOutputLine("Nothing to clean.");
            foreach (var path in removed)
                _shell.AppendOutputLine($"Deleted {Path.GetFileName(path)}");
            total += removed.Count;
        }
        _shell.AppendOutputLine($"------ Clean complete: {total} file(s) removed ------");

        // Drops (or shrinks) the Solution Explorer's "Generated Files" node if the assembler
        // listings it was showing are among the files just deleted.
        _solutionExplorer.Rebuild(_workspace);
    }
}
