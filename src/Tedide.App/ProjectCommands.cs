using Tedide.App.Views;
using Tedide.Build;
using Tedide.Core;
using Tedide.Core.Navigation;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace Tedide.App;

/// <summary>
/// Projects, solutions and files: New/Open/Close Project, the Solution Explorer's file and project
/// commands, opening, closing and saving tabs, Project Settings, and each project's remembered open
/// tabs. Calls back into the shell through <see cref="IShell"/>; the shell keeps the menus, and
/// hears about changes it shows elsewhere through <see cref="RecentProjectsChanged"/> and
/// <see cref="FileSaved"/>.
/// </summary>
internal sealed class ProjectCommands(IShell shell, Workspace workspace, EditorPane editorPane, SolutionExplorerTree solutionExplorer,
    SymbolPanelView symbolPanel, DebugSession debug, GitIntegration gitIntegration, NavigationHistory navigationHistory,
    RecentProjectsSettings recentProjects, ViceEmulator vice, string toolchainFile)
{
    private readonly IShell _shell = shell;
    private readonly IDialogs _dialogs = shell.Dialogs;
    private readonly Workspace _workspace = workspace;
    private readonly EditorPane _editorPane = editorPane;
    private readonly SolutionExplorerTree _solutionExplorer = solutionExplorer;
    private readonly SymbolPanelView _symbolPanel = symbolPanel;
    private readonly DebugSession _debug = debug;
    private readonly GitIntegration _gitIntegration = gitIntegration;
    private readonly NavigationHistory _navigationHistory = navigationHistory;
    private readonly RecentProjectsSettings _recentProjects = recentProjects;
    private readonly ViceEmulator _vice = vice;
    private readonly string _toolchainFile = toolchainFile;

    /// <summary>The active project's remembered open tabs - see <see cref="SaveLastOpenFileForActiveProject"/>.</summary>
    private SessionStateFile _sessionState = new();

    /// <summary>The Recent Projects and Solutions list changed - the File menu shows it.</summary>
    public event Action? RecentProjectsChanged;

    /// <summary>A file was written: git status, the error check and completion may all change.</summary>
    public event Action<string>? FileSaved;

    internal void NewProject() => _shell.Guard("Creating the project", () => NewProjectCore());

    private void NewProjectCore()
    {
        var dialog = new NewProjectDialog();
        _dialogs.Run(dialog);
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
            if (dialog.CreateRepository)
                _shell.Fire(_gitIntegration.CreateRepositoryAsync(ask: false), "Creating the repository");
        }
    }

    internal void OpenProject()
    {
        var path = _dialogs.PickFiles(new OpenDialog { Title = "Open Project" }).FirstOrDefault();
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
    internal void OpenProjectOrSolution(string path) => _shell.Guard("Opening the project", () => OpenProjectOrSolutionCore(path));

    private void OpenProjectOrSolutionCore(string path)
    {
        if (!File.Exists(path))
        {
            _dialogs.ErrorQuery("File Not Found", $"'{path}' no longer exists.", ["OK"]);
            _recentProjects.Remove(path);
            RecentProjectsChanged?.Invoke();
            return;
        }

        var isSolution = path.EndsWith(TedideSolution.FileExtension, StringComparison.OrdinalIgnoreCase);
        if (!isSolution && !path.EndsWith(TedideProject.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ErrorQuery("Unsupported file",
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
        RecentProjectsChanged?.Invoke();
    }
    /// <summary>
    /// Creates a new source/header file directly in <paramref name="directory"/> - the folder the
    /// user right-clicked in the Solution Explorer to get here (New File isn't offered on the
    /// project root itself - see <see cref="SolutionExplorerTree.TryShowContextMenu"/>).
    /// Compilable files (.c/.s/.asm) are added to the owning project's SourceFiles and the
    /// project is saved; headers are not, since cl65 never compiles them directly. The new file
    /// is opened in the editor once created.
    /// </summary>
    /// <param name="project">The project whose Solution Explorer node the folder is under - which
    /// one a new source joins when several share the folder. Null: <see cref="Workspace.ProjectFor"/>.</param>
    internal void NewFile(string directory, TedideProject? project = null) => _shell.Guard("Creating the file", () => NewFileCore(directory, project));

    private void NewFileCore(string directory, TedideProject? project)
    {
        project ??= _workspace.ProjectFor(directory);
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
        _dialogs.Run(dialog);
        if (dialog.FileName is not { } fileName)
            return;

        var filePath = Path.Combine(directory, fileName);
        if (File.Exists(filePath))
        {
            _dialogs.ErrorQuery("File Exists", $"'{fileName}' already exists.", ["OK"]);
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
    /// <param name="project">As for <see cref="NewFile"/>.</param>
    internal void AddExistingItem(string directory, TedideProject? project = null) => _shell.Guard("Adding the files", () => AddExistingItemCore(directory, project));

    private void AddExistingItemCore(string directory, TedideProject? project)
    {
        project ??= _workspace.ProjectFor(directory);
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
        var chosen = _dialogs.PickFiles(dialog);
        if (chosen.Count == 0)
            return;

        var skipped = new List<string>();
        var addedAny = false;
        foreach (var sourcePath in chosen)
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
            _dialogs.ErrorQuery("Some Files Skipped",
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
    internal void RenameFile(string path) => _shell.Guard("Renaming the file", () => RenameFileCore(path));

    private void RenameFileCore(string path)
    {
        var dialog = new RenameFileDialog(Path.GetFileName(path));
        _dialogs.Run(dialog);
        if (dialog.NewFileName is not { } newFileName)
            return;

        var newPath = Path.Combine(Path.GetDirectoryName(path)!, newFileName);
        // A case-only rename (main.c -> Main.c) names the same file on Windows, so File.Exists is
        // true for it - that's not a collision, and File.Move handles it fine.
        var isCaseOnlyRename = string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        if (File.Exists(newPath) && !isCaseOnlyRename)
        {
            _dialogs.ErrorQuery("File Exists", $"'{newFileName}' already exists.", ["OK"]);
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
    internal void DeleteFile(string path) => _shell.Guard("Deleting the file", () => DeleteFileCore(path));

    private void DeleteFileCore(string path)
    {
        var choice = _dialogs.Query("Delete File",
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
    /// Shows the given file in the editor: its tab if it's already open, otherwise a new tab.
    /// Everything that follows the shown file (title, highlighting, breakpoints) is refreshed by
    /// <see cref="AppShell.OnActiveDocumentChanged"/>.
    /// </summary>
    internal void OpenFile(string path) => _shell.Guard("Opening the file", () => _editorPane.Open(path));

    /// <summary>
    /// File > Open File...: any source files, in a project or not, with or without a project open.
    /// A file outside every project is edited, saved and checked for errors as you type like any
    /// other (see <see cref="LiveErrorChecking.StandInProjectFor"/>), but isn't built - Add
    /// Existing Item puts one in a project.
    /// </summary>
    internal void OpenFiles()
    {
        var dialog = new OpenDialog
        {
            Title = "Open File",
            OpenMode = OpenMode.File,
            AllowsMultipleSelection = true,
            AllowedTypes =
            [
                new AllowedType("C Files", ".c", ".h"),
                new AllowedType("Source/Header Files", SolutionExplorerTree.DisplayedExtensions),
                new AllowedTypeAny(),
            ],
        };
        foreach (var path in _dialogs.PickFiles(dialog))
            OpenFile(path);
    }
    /// <summary>Records whichever file is currently open (if any) as the active project's "last
    /// open file", so <see cref="LoadLastOpenFileForActiveProject"/> can reopen it automatically
    /// next time this same project becomes active again. Called right before switching away from
    /// the active project - to a different project/solution (<see cref="OpenProjectOrSolution"/>),
    /// a freshly created one (<see cref="NewProject"/>), or none at all (<see cref="CloseSolution"/>) -
    /// and once more from Program.cs on quit (see <see cref="AppShell.SaveSessionState"/>), the same two
    /// moments <see cref="AppShell.SaveLayoutSettings"/> is saved at. A no-op if no project is loaded (there's
    /// nowhere to save the sidecar file).</summary>
    internal void SaveLastOpenFileForActiveProject() => _shell.Guard("Saving the session", () => SaveLastOpenFileForActiveProjectCore());

    internal void SaveLastOpenFileForActiveProjectCore()
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
            _shell.AppendOutputLine(problem);

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
            var fullPath = Path.GetFullPath(Path.Combine(activeProject.Directory, relativePath));
            if (File.Exists(fullPath))
                OpenFile(fullPath);
        }

        // Finish on the tab that was showing.
        if (_sessionState.LastOpenFile is { } last && Path.GetFullPath(Path.Combine(activeProject.Directory, last)) is var lastPath && _editorPane.IsOpen(lastPath))
            _editorPane.Open(lastPath);
    }

    /// <summary>File > Close File (Ctrl+W): closes the tab being shown, asking about unsaved changes first.</summary>
    internal void CloseActiveFile()
    {
        if (_editorPane.OpenPath is { } path)
            CloseFile(path);
    }

    /// <summary>Closes one tab, asking about its unsaved changes first. False if the user cancelled.</summary>
    internal bool CloseFile(string path)
    {
        if (!ConfirmCloseFiles([path]))
            return false;
        _editorPane.Close(path);
        return true;
    }

    /// <summary>File > Close All Files: closes every tab, asking about unsaved changes once for all of them.</summary>
    internal void CloseAllFiles()
    {
        if (ConfirmCloseFiles(_editorPane.OpenPaths))
            _editorPane.CloseAll();
    }

    /// <summary>
    /// Closes the currently loaded solution/project(s) - clearing the Solution Explorer and
    /// closing the open file first (prompting to save it if modified, same as
    /// <see cref="CloseActiveFile"/>). Does nothing if nothing is loaded. Files already saved to
    /// disk are untouched; this only clears the in-memory session, same as <see cref="Workspace.Close"/>.
    /// </summary>
    internal void CloseSolution()
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
    /// Before closing the given open files: if any have unsaved changes, asks once whether to save
    /// them, discard them, or cancel - naming the file when there's one, listing them when there
    /// are several. True if it's fine to go ahead (nothing modified, or Save/Discard chosen and any
    /// save worked), false if the user cancelled or a save failed.
    /// </summary>
    internal bool ConfirmCloseFiles(IReadOnlyCollection<string> paths)
    {
        var modified = paths.Where(_editorPane.IsModifiedFile).ToList();
        if (modified.Count == 0)
            return true;

        var message = modified.Count == 1
            ? $"Save changes to {Path.GetFileName(modified[0])}?"
            : $"Save changes to these {modified.Count} files?\n\n{string.Join('\n', modified.Take(8).Select(Path.GetFileName))}"
              + (modified.Count > 8 ? $"\n...and {modified.Count - 8} more" : "");
        var choice = _dialogs.Query("Unsaved Changes", message, [modified.Count == 1 ? "Save" : "Save All", "Discard", "Cancel"]);
        return choice switch
        {
            0 => modified.All(SaveFile), // a failed save must not go on to lose the buffer
            1 => true,
            _ => false, // Cancel, or Esc
        };
    }

    /// <summary>Saves one open file (see <see cref="EditorPane.Save(string)"/>), reporting any
    /// encoding change in Output. False, having already told the user why, if it couldn't be written.</summary>
    internal bool SaveFile(string path) => _shell.Guard($"Saving {Path.GetFileName(path)}", () =>
    {
        if (_editorPane.Save(path) is { } notice)
            _shell.AppendOutputLine(notice);
        FileSaved?.Invoke(path);
    });

    /// <summary>File > Save (Ctrl+S): the file being shown, plus the loaded project/solution files.</summary>
    internal bool SaveActive() =>
        (_editorPane.OpenPath is not { } path || SaveFile(path)) && _shell.Guard("Saving the project", _workspace.SaveAll);

    /// <summary>File > Save All, and every build: each modified open file plus the project/solution
    /// files. False, having already told the user why, if anything couldn't be written.</summary>
    internal bool SaveAll() =>
        _editorPane.ModifiedPaths.All(SaveFile) && _shell.Guard("Saving the project", _workspace.SaveAll);
    /// <summary>
    /// Solution Explorer > Add New Project: scaffolds a project (an application or a library) in
    /// its own folder beside the solution file, and adds it to the solution.
    /// </summary>
    internal void AddNewProject() => _shell.Guard("Adding the project", () =>
    {
        if (_workspace.Solution is not { } solution)
            return;
        var dialog = new NewProjectDialog(_workspace.DefaultNewProjectParent(), "Add New Project", offerRepository: false);
        _dialogs.Run(dialog);
        if (dialog.Target is not { } target || string.IsNullOrWhiteSpace(dialog.ProjectName))
            return;
        var project = _workspace.AddNewProject(dialog.Directory, dialog.ProjectName.Trim(), target, dialog.OutputType);
        _solutionExplorer.Rebuild(_workspace);
        _shell.AppendOutputLine($"Added {project.Name} ({(project.IsLibrary ? "library" : "application")}) to the solution.");
    });

    /// <summary>Solution Explorer > Add Existing Project: adds a .tproj from disk to the solution.</summary>
    internal void AddExistingProject() => _shell.Guard("Adding the project", () =>
    {
        if (_workspace.Solution is null)
            return;
        if (_dialogs.PickFiles(new OpenDialog { Title = "Add Existing Project" }).FirstOrDefault() is not { } path)
            return;
        if (!path.EndsWith(TedideProject.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ErrorQuery("Unsupported file", $"Expected a {TedideProject.FileExtension} file.", ["OK"]);
            return;
        }
        var project = _workspace.AddExistingProject(path);
        _solutionExplorer.Rebuild(_workspace);
        _shell.AppendOutputLine($"Added {project.Name} to the solution.");
    });

    /// <summary>
    /// Solution Explorer > Remove from Solution: drops the project (its files stay on disk) and
    /// other projects' references to it, after closing its tabs.
    /// </summary>
    internal void RemoveProject(TedideProject project) => _shell.Guard("Removing the project", () =>
    {
        var choice = _dialogs.Query("Remove Project",
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
        _shell.AppendOutputLine($"Removed {project.Name} from the solution.");
    });

    /// <summary>
    /// Solution Explorer > Delete Project: removes the project from the solution (as Remove from
    /// Solution does) and sends its whole folder to the Recycle Bin, after confirming. Refused when
    /// the folder also holds the solution file or another project - see <see cref="ProjectDeletion"/>.
    /// Its tabs close without asking about unsaved changes, since the files are going anyway.
    /// </summary>
    internal void DeleteProject(TedideProject project) => _shell.Guard("Deleting the project", () =>
    {
        if (ProjectDeletion.WhyNot(_workspace, project) is { } reason)
        {
            _dialogs.ErrorQuery("Can't Delete Project", reason, ["OK"]);
            return;
        }

        var choice = _dialogs.Query("Delete Project",
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
        _shell.AppendOutputLine($"Deleted {project.Name}: {project.Directory} is in the Recycle Bin.");
    });

    /// <summary>Solution Explorer > Set as Startup Project: the project Run, Start Debugging and
    /// Build Project act on, remembered in the solution file.</summary>
    internal void SetStartupProject(TedideProject project) => _shell.Guard("Setting the startup project", () =>
    {
        SaveLastOpenFileForActiveProject();
        _workspace.SetStartupProject(project);
        _solutionExplorer.Rebuild(_workspace);
        OnStartupProjectChanged();
        _shell.AppendOutputLine($"{project.Name} is now the startup project.");
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
    internal void ShowProjectSettings() => ShowProjectSettings(_workspace.ActiveProject);

    /// <summary>The same, for any project - the Solution Explorer's project Settings... item.</summary>
    internal void ShowProjectSettings(TedideProject? project) => _shell.Guard("Saving project settings", () => ShowProjectSettingsCore(project));

    private void ShowProjectSettingsCore(TedideProject? project)
    {
        if (project is null)
        {
            _shell.AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        var oldName = project.Name;
        var oldFilePath = project.FilePath;
        var oldDirectory = project.Directory;

        var dialog = new ProjectSettingsDialog(project, _workspace.Projects, _toolchainFile);
        _dialogs.Run(dialog);
        if (!dialog.Saved)
            return;

        // The dialog's "CC65"/"VICE" tabs already persisted to ToolchainSettings on Save (they're
        // not project state - see ProjectSettingsDialog) - reload and apply immediately so a
        // changed value takes effect without restarting Tedide. Unlike the startup call in the
        // constructor, an explicit Save is allowed to unset CC65_HOME.
        ApplyToolchainSettings(ToolchainSettings.Load(_toolchainFile), allowUnsettingCc65Home: true);

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
    internal void ApplyToolchainSettings(ToolchainSettings settings, bool allowUnsettingCc65Home)
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
            _dialogs.ErrorQuery("Folder Not Renamed",
                $"The project was renamed to '{project.Name}', but its folder was left as-is: {reason}.", ["OK"]);
    }
}
