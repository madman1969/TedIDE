using System.Runtime.CompilerServices;
using Serilog;
using Tedide.App.Views;
using Tedide.Core;
using Tedide.Git;
using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Editor;
using Terminal.Gui.Views;

namespace Tedide.App;

/// <summary>
/// Git in the IDE: the Git tab's commands (stage, commit, stash, branches, history, conflicts,
/// fetch, pull and push), the branch and blame above the editor, the gutter's change bars, and
/// Compare and Blame. <see cref="GitTracker"/> keeps the status; this acts on it. Calls back into
/// the shell through <see cref="IShell"/>.
/// </summary>
internal sealed class GitIntegration
{
    private readonly IShell _shell;
    private readonly Workspace _workspace;
    private readonly EditorPane _editorPane;
    private readonly SolutionExplorerTree _solutionExplorer;
    private readonly NavigationCommands _navigation;
    private readonly GitTracker _git;
    private readonly GitChangesView _gitView;

    public GitIntegration(IShell shell, Workspace workspace, EditorPane editorPane,
        SolutionExplorerTree solutionExplorer, NavigationCommands navigation, GitTracker git, GitChangesView gitView,
        Action<TimeSpan, Func<bool>>? addTimeout = null)
    {
        _shell = shell;
        // Terminal.Gui's timers, or a test's - see GitTracker.
        addTimeout ??= (delay, callback) => Application.AddTimeout(delay, callback);
        // A burst of caret moves (typing, holding an arrow key) runs one git blame, and a burst of
        // edits one diff, once they pause.
        _blame = new Debouncer(TimeSpan.FromMilliseconds(400), () => Fire(BlameCaretLineAsync(_blame!.Generation)), addTimeout);
        _lineMarkers = new Debouncer(TimeSpan.FromMilliseconds(400), () => Fire(UpdateLineMarkersAsync(_lineMarkers!.Generation)), addTimeout);
        _workspace = workspace;
        _editorPane = editorPane;
        _solutionExplorer = solutionExplorer;
        _navigation = navigation;
        _git = git;
        _gitView = gitView;

        _gitView.StageRequested += StageFiles;
        _gitView.UnstageRequested += UnstageFiles;
        _gitView.DiscardRequested += DiscardFile;
        _gitView.OpenRequested += _shell.OpenFile;
        _gitView.CompareRequested += file => Fire(CompareWithHeadAsync(file.Path, file.OriginalPath));
        _gitView.BlameRequested += file => Fire(ShowBlameAsync(file.Path));
        _gitView.CommitRequested += Commit;
        _gitView.RefreshRequested += () => _git.RequestRefresh();
        _gitView.FetchRequested += FetchFromRemote;
        _gitView.BranchesRequested += () => Fire(ShowBranchesAsync());
        _gitView.AmendToggled += amending => Fire(LoadAmendMessageAsync(amending));
        _gitView.StashesRequested += () => Fire(ShowStashesAsync());
        _gitView.HistoryRequested += () => Fire(ShowHistoryAsync(null));
        _gitView.FileHistoryRequested += file => Fire(ShowHistoryAsync(file.Path));
        _gitView.ConflictRequested += ResolveConflict;
        _gitView.ContinueRequested += ContinueOperation;
        _gitView.AbortRequested += AbortOperation;
        _gitView.PullRequested += PullFromRemote;
        _gitView.PushRequested += () => Fire(PushToRemoteAsync());
        _gitView.CancelRequested += () => _syncCancellation?.Cancel();

        _gitLineMarkers = new GitLineMarkers(_editorPane.Editor);
        _editorPane.Editor.BackgroundRenderers.Add(_gitLineMarkers);
        _git.Changed += OnGitChanged;
        _editorPane.Editor.CaretChanged += (_, _) => _blame.Request();
        _editorPane.Editor.ContentChanged += (_, _) => _lineMarkers.Request();
    }

    private void Fire(Task task, [CallerArgumentExpression(nameof(task))] string what = "") => _shell.Fire(task, what);

    /// <summary>After a tab switch, open or close: the caret line's blame, and the change bars -
    /// cleared at once for a different file, so the last file's bars don't show on it while its own
    /// are worked out.</summary>
    public void ActiveDocumentChanged()
    {
        _blame.Request();
        if (!EditorPane.SamePath(_editorPane.OpenPath, _lineMarkersPath))
        {
            _gitLineMarkers.Changes = new Dictionary<int, LineChangeKind>();
            _lineMarkers.Request();
        }
    }

    /// <summary>Who last changed the caret's line - see <see cref="BlameCaretLineAsync"/>.</summary>
    private string? _blameText;

    /// <summary>Blames the caret's line soon - see <see cref="BlameCaretLineAsync"/>.</summary>
    private readonly Debouncer _blame;

    private CancellationTokenSource? _blameCancellation;

    /// <summary>Set while a fetch, pull or push runs - one at a time; the Git tab's Cancel cancels it.</summary>
    private CancellationTokenSource? _syncCancellation;

    /// <summary>Git's change bars in the editor's gutter - see <see cref="UpdateLineMarkersAsync"/>.</summary>
    private readonly GitLineMarkers _gitLineMarkers;

    /// <summary>Works out the change bars soon - see <see cref="UpdateLineMarkersAsync"/>.</summary>
    private readonly Debouncer _lineMarkers;

    private CancellationTokenSource? _lineMarkersCancellation;

    /// <summary>The file the bars are for, and the git state they were worked out against - a git
    /// refresh only recomputes them when that changes.</summary>
    private string? _lineMarkersPath, _lineMarkersGitState;

    /// <summary>New git status: the Solution Explorer's markers, the Git tab, the branch and blame.</summary>
    private void OnGitChanged()
    {
        _solutionExplorer.SetGitStatus(_git.Files);
        _gitView.SetStatus(_git.Primary, _git.PrimaryStatus);
        UpdateGitAnnotation();
        _blame.Request();
        if (LineMarkersGitState() != _lineMarkersGitState)
            _lineMarkers.Request();
    }

    /// <summary>
    /// "main ↑2 · Ln 12: aross, 3 days ago: Add tabs" at the right of the editor's tab row - the
    /// branch, then who last changed the caret's line. Empty outside a repository. Not the status
    /// bar: it's full at ordinary window widths, leaving the text cut to "main ·" (confirmed live).
    /// </summary>
    public void UpdateGitAnnotation() =>
        _editorPane.Annotation = _git.PrimaryStatus is not { } status ? ""
            : _blameText is { } blame ? $"{status.Describe()}  ·  {blame}"
            : status.Describe();

    /// <summary>Phase 5a of git support: who last changed the caret's line, blamed against the
    /// editor's own text so unsaved edits read as "Not committed yet". Nothing for a file outside a
    /// repository or one git doesn't track.</summary>
    private async Task BlameCaretLineAsync(int generation)
    {
        _blameCancellation?.Cancel();
        if (_editorPane.OpenPath is not { } path || _git.RepositoryFor(path) is not { } repository
            || _git.Files.TryGetValue(path, out var file) && file.IsUntracked)
        {
            _blameText = null;
            UpdateGitAnnotation();
            return;
        }

        // Read here, on the UI thread - the document belongs to it.
        var line = _editorPane.CaretPosition.Line;
        var text = _editorPane.Editor.Text;
        var cancellation = new CancellationTokenSource();
        _blameCancellation = cancellation;
        GitBlameLine? blame;
        try
        {
            blame = await repository.BlameLineAsync(path, line, text, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_blame.IsCurrent(generation))
            return;
        _blameText = blame is null ? null : $"Ln {line}: {blame.Describe(DateTimeOffset.Now)}";
        UpdateGitAnnotation();
    }

    /// <summary>What the shown file's change bars depend on besides its text: HEAD, and the file's
    /// own git status (a commit, a discard or a checkout changes one of them).</summary>
    private string LineMarkersGitState() =>
        _editorPane.OpenPath is { } path
            ? $"{_git.PrimaryStatus?.Head}|{_git.Files.GetValueOrDefault(path)}|{_git.RepositoryFor(path)?.Root}"
            : "";

    /// <summary>
    /// Git phase 2: diffs the shown file's text - unsaved edits included - against HEAD and shows
    /// the result as bars in the gutter (see <see cref="GitLineMarkers"/>). None for a file outside
    /// a repository or one git doesn't track yet, as in VS Code.
    /// </summary>
    private async Task UpdateLineMarkersAsync(int generation)
    {
        _lineMarkersCancellation?.Cancel();
        var path = _editorPane.OpenPath;
        _lineMarkersPath = path;
        _lineMarkersGitState = LineMarkersGitState();
        var file = path is null ? null : _git.Files.GetValueOrDefault(path);
        if (path is null || _git.RepositoryFor(path) is not { } repository || file is { IsUntracked: true })
        {
            _gitLineMarkers.Changes = new Dictionary<int, LineChangeKind>();
            return;
        }

        // Read here, on the UI thread - the document belongs to it.
        var text = _editorPane.Editor.Text;
        var lineCount = _editorPane.Editor.Document!.LineCount;
        var cancellation = new CancellationTokenSource();
        _lineMarkersCancellation = cancellation;
        GitDiff diff;
        try
        {
            // A file git reports nothing about and HEAD doesn't have is ignored - a listing or a
            // linker map - so it gets no bars rather than every line marked as added.
            diff = await repository.DiffWithHeadAsync(path, text, file?.OriginalPath, contextLines: 0, cancellation.Token,
                emptyIfNotInHead: file is null);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never worth an error dialog: the bars just stay as they were.
            Log.Warning(ex, "Working out git change markers for {Path} failed", path);
            return;
        }

        if (_lineMarkers.IsCurrent(generation) && _editorPane.IsShown(path))
            _gitLineMarkers.Changes = diff.LineChanges(lineCount);
    }

    /// <summary>
    /// Runs a git operation on the solution's repository in the background, then refreshes. A
    /// failure is shown in git's own words. <paramref name="after"/> runs on the UI thread when it
    /// worked, <paramref name="afterAnyway"/> whether or not - for operations that change files
    /// even when they fail (a rebase stopping on new conflicts).
    /// </summary>
    private async Task RunGitAsync(string what, Func<GitRepository, Task<GitResult>> operation, Action? after = null,
        Action? afterAnyway = null)
    {
        if (_git.Primary is not { } repository)
            return;
        GitResult result;
        try
        {
            result = await operation(repository);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = new GitResult(1, "", ex.Message);
        }
        afterAnyway?.Invoke();
        if (result.Succeeded)
            after?.Invoke();
        else
            _shell.Dialogs.ErrorQuery($"{what} Failed", RenameSymbolDialog.Wrap(result.Explanation), ["OK"]);
        _git.RequestRefresh();
    }

    /// <summary>
    /// Before staging or committing: saves the open files with unsaved edits, so what git takes is
    /// what's on screen. Not <see cref="IShell.SaveAll"/> - that also rewrites the .tproj/.tsln files,
    /// which then showed up as changed themselves (confirmed live).
    /// </summary>
    private bool SaveOpenFiles() => _editorPane.ModifiedPaths.All(_shell.SaveFile);

    /// <summary>Git tab > Space or Stage All.</summary>
    internal void StageFiles(IReadOnlyList<GitFileStatus> files)
    {
        if (files.Count == 0 || !SaveOpenFiles())
            return;
        Fire(RunGitAsync("Staging", repository => repository.StageAsync(files.Select(f => f.Path))));
    }

    /// <summary>Git tab > Space on a staged file, or Unstage All. The files themselves don't change.</summary>
    internal void UnstageFiles(IReadOnlyList<GitFileStatus> files)
    {
        if (files.Count == 0)
            return;
        var hasCommits = _git.PrimaryStatus?.HasCommits ?? true;
        Fire(RunGitAsync("Unstaging", repository => repository.UnstageAsync(files.Select(f => f.Path), hasCommits)));
    }

    /// <summary>
    /// Git tab > Delete: throws away a file's unstaged changes after asking - a file git doesn't
    /// track yet is deleted. An open tab follows: reloaded from disk, or closed for a deleted file,
    /// its unsaved edits going too (the question says so).
    /// </summary>
    internal void DiscardFile(GitFileStatus file)
    {
        var name = Path.GetFileName(file.Path);
        var unsaved = _editorPane.IsModifiedFile(file.Path) ? " Its unsaved edits in the editor go too." : "";
        var question = file.IsUntracked
            ? $"Delete {name}? git doesn't track it, so this can't be undone.{unsaved}"
            : $"Discard the changes to {name}? This can't be undone.{unsaved}";
        if (_shell.Dialogs.Query("Discard Changes", RenameSymbolDialog.Wrap(question), ["Discard", "Cancel"]) != 0)
            return;
        Fire(RunGitAsync("Discarding", repository => repository.DiscardAsync([file]), () =>
        {
            if (file.IsUntracked)
                _editorPane.Close(file.Path);
            else
                _shell.Guard($"Reloading {name}", () => _editorPane.Reload(file.Path));
        }));
    }

    /// <summary>Git tab > Commit Staged / Commit All. Saves open files first (see <see cref="SaveOpenFiles"/>),
    /// then reports the new commit in Output.</summary>
    internal void Commit(string message, bool stageAll, bool amend)
    {
        if (amend)
        {
            if (_git.PrimaryStatus is { HasCommits: false })
            {
                _shell.Dialogs.ErrorQuery("Amend", "There's no commit yet to amend.", ["OK"]);
                return;
            }
            // The last commit is already on the remote when the branch isn't ahead of it.
            if (_git.PrimaryStatus is { Upstream: { } upstream, Ahead: 0 } && _shell.Dialogs.Query("Amend Pushed Commit",
                    RenameSymbolDialog.Wrap($"The last commit is already pushed to {upstream}. Amending replaces it, so the next push will be rejected - " +
                        "it would need a force push, which Tedide never does. Amend anyway?"), ["Amend", "Cancel"]) != 0)
                return;
        }
        else if (string.IsNullOrWhiteSpace(message))
        {
            _shell.Dialogs.ErrorQuery("Commit", "Write a commit message first.", ["OK"]);
            return;
        }
        if (!SaveOpenFiles())
            return;
        string? head = null;
        Fire(RunGitAsync(amend ? "Amend" : "Commit", async repository =>
        {
            var result = await repository.CommitAsync(message.Trim(), stageAll, amend);
            if (result.Succeeded)
                head = await repository.DescribeHeadAsync();
            return result;
        }, () =>
        {
            _gitView.ClearMessage();
            _shell.AppendOutputLine($"{(amend ? "Amended" : "Committed")} {head}");
        }));
    }

    /// <summary>The message Amend last commit loaded into the commit box, to recognise it unedited.</summary>
    private string? _amendMessage;

    /// <summary>
    /// Amend last commit ticked: an empty commit box gets the last commit's message to edit.
    /// Cleared: that message goes again, unless it's been edited.
    /// </summary>
    internal async Task LoadAmendMessageAsync(bool amending)
    {
        if (!amending)
        {
            if (_amendMessage is not null && _gitView.Message == _amendMessage)
                _gitView.Message = "";
            _amendMessage = null;
            return;
        }
        if (_gitView.Message.Trim().Length > 0 || _git.Primary is not { } repository)
            return;
        var message = await repository.GetLastCommitMessageAsync();
        if (message is null || !_gitView.IsAmending || _gitView.Message.Trim().Length > 0)
            return;
        _gitView.Message = message;
        _amendMessage = message;
    }

    /// <summary>
    /// Git tab > Stashes: <see cref="StashesDialog"/>, then stash every change away, or pop, apply
    /// or drop a stash. Open files are saved first so git takes what's on screen, and the editor
    /// follows the files afterwards - even when a pop stops on conflicts, which keeps the stash.
    /// Dropping reopens the list.
    /// </summary>
    internal async Task ShowStashesAsync()
    {
        if (_syncCancellation is not null || _git.Primary is not { } repository)
            return;
        var stashes = await repository.GetStashesAsync();
        var changes = _git.Files.Values.Count(f => repository.Contains(f.Path)) + _editorPane.ModifiedPaths.Count(p => !_git.Files.ContainsKey(p));
        var dialog = new StashesDialog(stashes, changes);
        _shell.Dialogs.Run(dialog);
        if (dialog.Choice is not { } choice)
            return;
        if (choice.Action == StashAction.Drop)
        {
            var stash = choice.Stash!;
            if (_shell.Dialogs.Query("Drop Stash", RenameSymbolDialog.Wrap($"Delete {stash.Name} ({stash.Description})? Its changes are lost - this can't be undone."),
                    ["Drop", "Cancel"]) == 0)
                Fire(RunGitAsync("Dropping the Stash", r => r.DropStashAsync(stash), () =>
                {
                    _shell.AppendOutputLine($"git: Dropped {stash.Name}.");
                    Fire(ShowStashesAsync());
                }));
            return;
        }
        if (!SaveOpenFiles())
            return;
        var projectFiles = ProjectFileContents();
        var (what, operation) = choice.Action switch
        {
            StashAction.Stash => ("Stashing", (Func<GitRepository, Task<GitResult>>)(r => r.StashAsync(choice.Message))),
            StashAction.Pop => ("Popping the Stash", r => r.UnstashAsync(choice.Stash!, drop: true)),
            _ => ("Applying the Stash", r => r.UnstashAsync(choice.Stash!, drop: false)),
        };
        GitResult? outcome = null;
        Fire(RunGitAsync(what, async r => outcome = await operation(r), () =>
        {
            var report = outcome?.Message ?? "";
            _shell.AppendOutputLine(choice.Action switch
            {
                StashAction.Stash when report.Contains("No local changes to save", StringComparison.Ordinal) => "git: Nothing to stash.",
                StashAction.Stash => "git: Changes stashed.",
                StashAction.Pop => $"git: Popped {choice.Stash!.Name}.",
                _ => $"git: Applied {choice.Stash!.Name} (kept).",
            });
        }, () => FollowWorkingTree(projectFiles)));
    }

    /// <summary>
    /// Git phase 6: runs a fetch, pull or push on the solution's repository in the background -
    /// one at a time, cancellable from the Git tab, with no time limit (signing in through Git
    /// Credential Manager's window or the browser can take a while). git's own report goes to
    /// Output; a failure is also shown, common ones put plainly (see <see cref="GitResult.Explanation"/>).
    /// <paramref name="after"/> runs on the UI thread when it worked.
    /// </summary>
    private async Task SyncAsync(string activity, string title, Func<GitRepository, CancellationToken, Task<GitResult>> operation,
        Action? after = null, Action? afterAnyway = null)
    {
        if (_syncCancellation is not null || _git.Primary is not { } repository)
            return;
        var cancellation = new CancellationTokenSource();
        _syncCancellation = cancellation;
        _gitView.SetActivity($"{activity}...");
        _shell.AppendOutputLine($"git: {activity} {Path.GetFileName(repository.Root)}...");

        GitResult? result;
        try
        {
            result = await operation(repository, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            result = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = new GitResult(1, "", ex.Message);
        }

        _syncCancellation = null;
        _gitView.SetActivity(null);
        afterAnyway?.Invoke();
        if (result is null)
            _shell.AppendOutputLine($"git: {title} cancelled.");
        else
        {
            foreach (var line in $"{result.Output}\n{result.Error}".Split('\n'))
            {
                if (line.Trim().Length > 0)
                    _shell.AppendOutputLine($"  {line.TrimEnd('\r')}");
            }
            _shell.AppendOutputLine(result.Succeeded ? $"git: {title} done." : $"git: {title} failed.");
            if (result.Succeeded)
                after?.Invoke();
            else
                _shell.Dialogs.ErrorQuery($"{title} Failed", RenameSymbolDialog.Wrap(result.Explanation), ["OK"]);
        }
        _git.RequestRefresh();
    }

    /// <summary>What a new repository's .gitignore leaves out: build output, and Tedide's own
    /// per-user files (the open tabs, breakpoints, and a damaged file's recovered copy).</summary>
    internal static readonly string[] IgnoredFiles =
        ["bin/", "obj/", "*.dbg", "*.lbl", "lnk.map", "*.session.json", "*.breakpoints.json", "*.corrupt"];

    /// <summary>
    /// Where a repository for the loaded solution goes: the folder holding the solution file and
    /// every project - normally the solution's own folder, or the one above when a project sits
    /// beside it. Null with nothing loaded.
    /// </summary>
    internal static string? RepositoryRootFor(Workspace workspace)
    {
        var folders = workspace.Projects.Select(p => p.Directory)
            .Concat(workspace.Solution?.Directory is { } solution ? [solution] : [])
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
            .ToList();
        if (folders.Count == 0)
            return null;
        var root = folders[0];
        foreach (var folder in folders.Skip(1))
            while (!ProjectCommands.IsSameOrInsideDirectory(folder, root))
                root = Path.GetDirectoryName(root) ?? root;
        return root;
    }

    /// <summary>
    /// Git > Create Repository... (and New Project's "Create a git repository"): makes the folder
    /// holding the solution a git repository on a "main" branch, with a .gitignore of
    /// <see cref="IgnoredFiles"/>. Refused when it's already in one. <paramref name="ask"/> false
    /// skips the confirmation - New Project's tick box already asked.
    /// </summary>
    internal async Task CreateRepositoryAsync(bool ask = true)
    {
        if (RepositoryRootFor(_workspace) is not { } root)
        {
            _shell.AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }
        if (await GitRepository.FindAsync(root) is { } existing)
        {
            var where = string.Equals(existing.Root, root, StringComparison.OrdinalIgnoreCase)
                ? $"{root} is already a git repository."
                : $"{root} is already inside the git repository at {existing.Root}.";
            _shell.Dialogs.ErrorQuery("Create Repository", RenameSymbolDialog.Wrap($"{where} Its Git tab is ready to use."), ["OK"]);
            return;
        }
        if (ask && _shell.Dialogs.Query("Create Repository", RenameSymbolDialog.Wrap(
                $"Make {root} a git repository? A .gitignore leaves out build output (bin/, obj/, .dbg and the like) "
                + "and Tedide's per-user files."), ["Create", "Cancel"]) != 0)
            return;

        var (result, _) = await GitRepository.CreateAsync(root, IgnoredFiles);
        if (!result.Succeeded)
        {
            _shell.Dialogs.ErrorQuery("Create Repository Failed", RenameSymbolDialog.Wrap(result.Explanation), ["OK"]);
            return;
        }
        _shell.AppendOutputLine($"git: Created a repository in {root}. Commit All in the Git tab makes its first commit; "
            + "Git > Add Remote... links it to GitHub.");
        _git.ProjectsChanged();
        _shell.ShowPane(_gitView);
    }

    /// <summary>
    /// Git > Add Remote...: links the solution's repository to another - "origin" at an empty
    /// GitHub repository, typically - so Push can publish to it. A remote that already exists is
    /// pointed at the new address, after asking.
    /// </summary>
    internal async Task AddRemoteAsync()
    {
        if (_git.Primary is not { } repository)
        {
            _shell.Dialogs.ErrorQuery("Add Remote", "The solution isn't in a git repository yet. Git > Create Repository... makes one.", ["OK"]);
            return;
        }

        var remotes = await repository.GetRemotesAsync();
        var dialog = new AddRemoteDialog();
        _shell.Dialogs.Run(dialog);
        if (dialog.RemoteName is not { } name || dialog.Url is not { } url)
            return;

        GitResult result;
        if (remotes.Contains(name))
        {
            var current = await repository.GetRemoteUrlAsync(name);
            if (_shell.Dialogs.Query("Replace Remote", RenameSymbolDialog.Wrap(
                    $"{name} already points at {current}. Point it at {url} instead?"), ["Replace", "Cancel"]) != 0)
                return;
            result = await repository.SetRemoteUrlAsync(name, url);
        }
        else
        {
            result = await repository.AddRemoteAsync(name, url);
        }

        if (!result.Succeeded)
        {
            _shell.Dialogs.ErrorQuery("Add Remote Failed", RenameSymbolDialog.Wrap(result.Explanation), ["OK"]);
            return;
        }
        _shell.AppendOutputLine($"git: {name} is {url}. Push in the Git tab publishes the branch there.");
        _git.RequestRefresh();
    }

    /// <summary>Git tab > Fetch: learns what's new on the remote; changes no files.</summary>
    internal void FetchFromRemote() => Fire(SyncAsync("Fetching", "Fetch", (repository, token) => repository.FetchAsync(token)));

    /// <summary>
    /// Git tab > Pull. Saves open files with unsaved edits first, as Commit does, so git sees
    /// what's on screen; afterwards the editor follows the files on disk (see
    /// <see cref="FollowWorkingTree"/>) - even when the pull stops on conflicts, so the conflict
    /// markers show; the files show "!" in the Git tab.
    /// </summary>
    internal void PullFromRemote()
    {
        if (_syncCancellation is not null || !SaveOpenFiles())
            return;
        var projectFiles = ProjectFileContents();
        Fire(SyncAsync("Pulling", "Pull", (repository, token) => repository.PullAsync(token),
            afterAnyway: () => FollowWorkingTree(projectFiles)));
    }

    /// <summary>After a pull: open files without unsaved edits follow what's now on disk.</summary>
    private void ReloadFilesChangedOnDisk()
    {
        foreach (var path in _editorPane.OpenPaths)
        {
            if (_editorPane.IsModifiedFile(path))
                continue;
            if (!File.Exists(path))
                _editorPane.Close(path);
            else
                _shell.Guard($"Reloading {Path.GetFileName(path)}", () =>
                {
                    if (SourceFileText.Read(path).Text != _editorPane.TextOf(path))
                        _editorPane.Reload(path);
                });
        }
    }

    /// <summary>
    /// Git tab > Push. A branch already on a remote is pushed to it; one that isn't is published -
    /// to origin, else the only remote - after asking. Never forced: a rejection says to pull first.
    /// </summary>
    internal async Task PushToRemoteAsync()
    {
        if (_syncCancellation is not null || _git.Primary is not { } repository || _git.PrimaryStatus is not { } status)
            return;
        if (status.Branch is not { } branch)
        {
            _shell.Dialogs.ErrorQuery("Push", "HEAD isn't on a branch (it's detached), so there's nothing to push. Check out a branch first.", ["OK"]);
            return;
        }
        if (status.Upstream is not null)
        {
            Fire(SyncAsync("Pushing", "Push", (r, token) => r.PushAsync(cancellationToken: token)));
            return;
        }

        var remotes = await repository.GetRemotesAsync();
        var remote = remotes.Contains("origin") ? "origin" : remotes.Count == 1 ? remotes[0] : null;
        if (remote is null)
        {
            _shell.Dialogs.ErrorQuery("Push", RenameSymbolDialog.Wrap(remotes.Count == 0
                ? "This repository has no remote to push to. Add one with git remote add origin <url> first."
                : $"{branch} isn't on a remote yet, and there's no origin to publish it to (remotes: {string.Join(", ", remotes)})."), ["OK"]);
            return;
        }
        var question = $"{branch} isn't on {remote} yet. Publish it there, and push to it from now on?";
        if (_shell.Dialogs.Query("Publish Branch", RenameSymbolDialog.Wrap(question), ["Publish", "Cancel"]) == 0)
            Fire(SyncAsync("Pushing", "Push", (r, token) => r.PushAsync(remote, branch, token)));
    }

    /// <summary>
    /// Git tab > Branches: lists the branches in <see cref="BranchesDialog"/> and does what's
    /// chosen there - switch, create (and switch to), or delete. Deleting reopens the list.
    /// </summary>
    internal async Task ShowBranchesAsync()
    {
        if (_syncCancellation is not null || _git.Primary is not { } repository)
            return;
        var branches = await repository.GetBranchesAsync();
        var dialog = new BranchesDialog(branches, _git.PrimaryStatus?.Branch);
        _shell.Dialogs.Run(dialog);
        switch (dialog.Choice)
        {
            case (BranchAction.Switch, { IsCurrent: false } branch, _):
                ChangeBranch("Switching Branch", r => r.SwitchAsync(branch), branch.LocalName);
                break;
            case (BranchAction.Create, _, { } name):
                ChangeBranch("Creating the Branch", r => r.CreateBranchAsync(name), name);
                break;
            case (BranchAction.Delete, { } branch, _):
                Fire(RunGitAsync("Deleting the Branch", r => r.DeleteBranchAsync(branch.Name), () =>
                {
                    _shell.AppendOutputLine($"git: Deleted branch {branch.Name}.");
                    Fire(ShowBranchesAsync());
                }));
                break;
        }
    }

    /// <summary>
    /// Switches (or creates and switches) branch. Open files with unsaved edits are saved first, so
    /// git carries them over - or refuses, if they clash with the other branch. Afterwards the
    /// editor and Solution Explorer follow the files now on disk; if the branch has different
    /// project or solution files, the solution is reopened from them.
    /// </summary>
    private void ChangeBranch(string what, Func<GitRepository, Task<GitResult>> operation, string branch)
    {
        if (!SaveOpenFiles())
            return;
        var projectFiles = ProjectFileContents();
        Fire(RunGitAsync(what, operation, () =>
        {
            _shell.AppendOutputLine($"git: Switched to {branch}.");
            FollowWorkingTree(projectFiles);
        }));
    }

    /// <summary>
    /// After git changed files under the editor (a switch, pull, continue or abort): if the
    /// loaded solution's or projects' files are no longer what <paramref name="projectFiles"/>
    /// recorded, the solution is reopened from them; otherwise open files without unsaved edits
    /// follow what's on disk and the Solution Explorer is rebuilt.
    /// </summary>
    private void FollowWorkingTree(List<KeyValuePair<string, string?>> projectFiles)
    {
        var reopen = _workspace.Solution?.FilePath ?? _workspace.ActiveProject?.FilePath;
        if (reopen is not null && !ProjectFileContents().SequenceEqual(projectFiles))
            _shell.OpenProjectOrSolution(reopen);
        else
        {
            ReloadFilesChangedOnDisk();
            _solutionExplorer.Rebuild(_workspace);
        }
    }

    /// <summary>
    /// Git tab > History (all commits) or File History / H (one file's): <see cref="HistoryDialog"/>,
    /// and from it a commit's change to a file in a <see cref="CompareDialog"/> - closing that comes
    /// back to the history at the same commit.
    /// </summary>
    public async Task ShowHistoryAsync(string? file)
    {
        if ((file is null ? _git.Primary : _git.RepositoryFor(file)) is not { } repository)
        {
            _shell.Dialogs.ErrorQuery("History", file is null ? "The solution isn't in a git repository." : $"{Path.GetFileName(file)} isn't in a git repository.", ["OK"]);
            return;
        }
        var commits = await repository.GetLogAsync(file);
        ShowHistory(repository, file, commits, 0);
    }

    private void ShowHistory(GitRepository repository, string? file, IReadOnlyList<GitCommit> commits, int selected)
    {
        if (commits.Count == 0)
        {
            _shell.Dialogs.Query("History", file is null ? "There are no commits yet." : $"git has no history for {Path.GetFileName(file)} - it isn't committed yet.", ["OK"]);
            return;
        }
        var title = file is null ? $"History - {Path.GetFileName(repository.Root)}" : $"History - {_navigation.DisplayPath(file)}";
        var dialog = new HistoryDialog(title, commits, selected);
        _shell.Dialogs.Run(dialog);
        if (dialog.Choice is { } choice)
            Fire(ShowCommitChangeAsync(repository, choice.Commit, choice.File, () => ShowHistory(repository, file, commits, dialog.SelectedIndex)));
    }

    /// <summary>One commit's change to one file, read-only - its line numbers are the file's as of
    /// that commit, so there's no Go to Line. <paramref name="back"/> returns to the history.</summary>
    private async Task ShowCommitChangeAsync(GitRepository repository, GitCommit commit, GitCommitFile file, Action back)
    {
        GitDiff diff;
        try
        {
            diff = await repository.GetCommitDiffAsync(commit, file);
        }
        catch (IOException ex)
        {
            _shell.Dialogs.ErrorQuery("History", RenameSymbolDialog.Wrap(ex.Message), ["OK"]);
            back();
            return;
        }
        if (diff.IsBinary || diff.Hunks.Count == 0)
            _shell.Dialogs.Query("History", RenameSymbolDialog.Wrap(diff.IsBinary
                ? $"{file.Path} is a binary file; {commit.ShortHash} changed it."
                : $"{commit.ShortHash} didn't change any lines of {file.Path} (a rename or a mode change)."), ["OK"]);
        else
            _shell.Dialogs.Run(new CompareDialog($"{commit.ShortHash} {commit.Subject} - {file.Path}",
                $"{commit.ShortHash} by {commit.Author}, {GitBlameLine.Ago(DateTimeOffset.Now - commit.When)}:", diff, canGoToLine: false));
        back();
    }

    /// <summary>
    /// Git tab > Enter on a conflicted file: <see cref="ConflictDialog"/>, then keep one side, open
    /// the file at its first conflict, or mark it resolved (asking first if markers remain).
    /// </summary>
    internal void ResolveConflict(GitFileStatus file)
    {
        var path = file.Path;
        var operation = _git.PrimaryStatus?.Operation ?? GitOperation.None;
        string text;
        try
        {
            text = _editorPane.TextOf(path) ?? (File.Exists(path) ? File.ReadAllText(path) : "");
        }
        catch (Exception ex) when (AppShell.IsFileError(ex))
        {
            _shell.Dialogs.ErrorQuery("Resolve Conflict", RenameSymbolDialog.Wrap(ex.Message), ["OK"]);
            return;
        }
        var dialog = new ConflictDialog(_navigation.DisplayPath(path), operation, ConflictDialog.CountSections(text));
        _shell.Dialogs.Run(dialog);
        switch (dialog.Choice)
        {
            case ConflictChoice.Edit:
                var lines = text.Split('\n');
                var first = Array.FindIndex(lines, l => l.StartsWith("<<<<<<<", StringComparison.Ordinal));
                if (File.Exists(path))
                    _navigation.NavigateTo(path, first < 0 ? 1 : first + 1, 1);
                break;
            case ConflictChoice.KeepMine or ConflictChoice.TakeTheirs:
                if (!SaveOpenFiles())
                    return;
                var keepMine = dialog.Choice == ConflictChoice.KeepMine;
                Fire(RunGitAsync("Resolving the Conflict", r => r.ResolveWithAsync(path, keepMine, operation), () =>
                {
                    _shell.AppendOutputLine($"git: {_navigation.DisplayPath(path)} resolved - {(keepMine ? "kept mine" : "took theirs")}.");
                    _shell.Guard($"Reloading {Path.GetFileName(path)}", () => _editorPane.Reload(path));
                }));
                break;
            case ConflictChoice.MarkResolved:
                if (!SaveOpenFiles())
                    return;
                var left = File.Exists(path) ? ConflictDialog.CountSections(File.ReadAllText(path)) : 0;
                if (left > 0 && _shell.Dialogs.Query("Mark Resolved", RenameSymbolDialog.Wrap(
                        $"{Path.GetFileName(path)} still has {(left == 1 ? "a conflict section" : $"{left} conflict sections")} (<<<<<<< markers). Mark it resolved anyway?"),
                        ["Mark Resolved", "Cancel"]) != 0)
                    return;
                Fire(RunGitAsync("Marking Resolved", r => r.StageAsync([path]),
                    () => _shell.AppendOutputLine($"git: {_navigation.DisplayPath(path)} marked resolved.")));
                break;
        }
    }

    /// <summary>
    /// Git tab > Continue: finishes the stopped merge (committing, with the message box's text if
    /// any), rebase, cherry-pick or revert - once no file is still conflicted. A rebase can stop
    /// again on the next commit's conflicts, so the editor follows the files either way.
    /// </summary>
    internal void ContinueOperation(GitOperation operation, string message)
    {
        var conflicted = _git.Files.Values.Count(f => f.IsConflicted && _git.Primary?.Contains(f.Path) == true);
        if (conflicted > 0)
        {
            _shell.Dialogs.ErrorQuery($"Continue {operation.Describe()}", RenameSymbolDialog.Wrap(
                $"{(conflicted == 1 ? "1 file still has" : $"{conflicted} files still have")} conflicts (marked ! in the Git tab). Press Enter on each to resolve it first."), ["OK"]);
            return;
        }
        if (!SaveOpenFiles())
            return;
        var projectFiles = ProjectFileContents();
        Fire(RunGitAsync($"Continuing the {operation.Describe()}", r => r.ContinueAsync(operation, operation == GitOperation.Merge ? message : null),
            () =>
            {
                _gitView.ClearMessage();
                _shell.AppendOutputLine($"git: {operation.Describe()} continued.");
            },
            () => FollowWorkingTree(projectFiles)));
    }

    /// <summary>Git tab > Abort: after asking, abandons the stopped operation - the branch and
    /// files go back to how they were before it started.</summary>
    internal void AbortOperation(GitOperation operation)
    {
        var question = $"Abort the {operation.Describe()}? The branch and its files go back to how they were before it started, and any conflicts you've resolved are lost.";
        if (_shell.Dialogs.Query($"Abort {operation.Describe()}", RenameSymbolDialog.Wrap(question), ["Abort", "Cancel"]) != 0)
            return;
        var projectFiles = ProjectFileContents();
        Fire(RunGitAsync($"Aborting the {operation.Describe()}", r => r.AbortAsync(operation),
            () => _shell.AppendOutputLine($"git: {operation.Describe()} aborted."),
            () => FollowWorkingTree(projectFiles)));
    }

    /// <summary>The loaded solution's and projects' files as they are on disk (null for one that's
    /// gone), to tell whether a branch switch changed them.</summary>
    private List<KeyValuePair<string, string?>> ProjectFileContents() =>
        _workspace.Projects.Select(p => p.FilePath)
            .Prepend(_workspace.Solution?.FilePath)
            .OfType<string>()
            .Select(path =>
            {
                try
                {
                    return KeyValuePair.Create(path, File.Exists(path) ? File.ReadAllText(path) : null);
                }
                catch (Exception ex) when (AppShell.IsFileError(ex))
                {
                    return KeyValuePair.Create(path, (string?)null);
                }
            })
            .ToList();

    /// <summary>Editor right-click > Compare with Last Commit, for the file shown.</summary>
    public void CompareActiveWithHead()
    {
        if (_editorPane.OpenPath is { } path)
            Fire(CompareWithHeadAsync(path));
    }

    /// <summary>
    /// Git phase 3: shows what changed in <paramref name="path"/> since the last commit, in a
    /// <see cref="CompareDialog"/> - an open file's unsaved edits included, a renamed file compared
    /// with where it was. Going to a line from there opens the file at it.
    /// </summary>
    internal async Task CompareWithHeadAsync(string path, string? headPath = null)
    {
        var name = Path.GetFileName(path);
        if (_git.RepositoryFor(path) is not { } repository)
        {
            _shell.Dialogs.ErrorQuery("Compare with Last Commit", $"{name} isn't in a git repository.", ["OK"]);
            return;
        }
        // Read here, on the UI thread - the document belongs to it.
        headPath ??= _git.Files.GetValueOrDefault(path)?.OriginalPath;
        var unsaved = _editorPane.IsModifiedFile(path);
        string? text;
        try
        {
            text = _editorPane.TextOf(path) ?? (File.Exists(path) ? await File.ReadAllTextAsync(path) : null);
        }
        catch (Exception ex) when (AppShell.IsFileError(ex))
        {
            _shell.Dialogs.ErrorQuery("Compare with Last Commit", RenameSymbolDialog.Wrap(ex.Message), ["OK"]);
            return;
        }

        GitDiff diff;
        try
        {
            diff = await repository.DiffWithHeadAsync(path, text, headPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Comparing {Path} with HEAD failed", path);
            _shell.Dialogs.ErrorQuery("Compare with Last Commit", RenameSymbolDialog.Wrap(ex.Message), ["OK"]);
            return;
        }

        if (diff.IsBinary || diff.Hunks.Count == 0)
        {
            _shell.Dialogs.Query("Compare with Last Commit",
                diff.IsBinary ? $"{name} is a binary file, and has changed." : $"{name} hasn't changed since the last commit.", ["OK"]);
            return;
        }
        var dialog = new CompareDialog(_navigation.DisplayPath(path), diff, unsaved);
        _shell.Dialogs.Run(dialog);
        if (dialog.GoToLine is not { } line || !File.Exists(path) && !_editorPane.IsOpen(path))
            return;
        if (!_editorPane.IsShown(path))
            _shell.OpenFile(path);
        if (_editorPane.IsShown(path))
            _navigation.NavigateTo(path, Math.Min(line, _editorPane.Editor.Document!.LineCount), 1);
    }

    /// <summary>
    /// Git phase 5b: who last changed every line of <paramref name="path"/>, in a
    /// <see cref="BlameDialog"/> opened at the caret's line - an open file's unsaved edits included,
    /// as "not committed yet". Going to a line from there opens the file at it.
    /// </summary>
    public async Task ShowBlameAsync(string path)
    {
        const string title = "Blame";
        var name = Path.GetFileName(path);
        if (_git.RepositoryFor(path) is not { } repository)
        {
            _shell.Dialogs.ErrorQuery(title, $"{name} isn't in a git repository.", ["OK"]);
            return;
        }
        if (_git.Files.GetValueOrDefault(path) is { IsUntracked: true })
        {
            _shell.Dialogs.ErrorQuery(title, $"git doesn't track {name} yet, so it has no history to show.", ["OK"]);
            return;
        }
        // Read here, on the UI thread - the document belongs to it.
        var caretLine = _editorPane.IsShown(path) ? _editorPane.CaretPosition.Line : 1;
        IReadOnlyList<GitBlameFileLine>? lines;
        try
        {
            var text = _editorPane.TextOf(path) ?? await File.ReadAllTextAsync(path);
            lines = await repository.BlameFileAsync(path, text);
        }
        catch (Exception ex) when (AppShell.IsFileError(ex))
        {
            _shell.Dialogs.ErrorQuery(title, RenameSymbolDialog.Wrap(ex.Message), ["OK"]);
            return;
        }

        if (lines is null)
        {
            _shell.Dialogs.ErrorQuery(title, $"git couldn't blame {name}.", ["OK"]);
            return;
        }
        var dialog = new BlameDialog(_navigation.DisplayPath(path), lines, caretLine);
        _shell.Dialogs.Run(dialog);
        if (dialog.GoToLine is not { } line)
            return;
        if (!_editorPane.IsShown(path))
            _shell.OpenFile(path);
        if (_editorPane.IsShown(path))
            _navigation.NavigateTo(path, Math.Min(line, _editorPane.Editor.Document!.LineCount), 1);
    }
}
