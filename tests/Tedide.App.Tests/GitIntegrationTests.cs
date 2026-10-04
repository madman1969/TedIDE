using Tedide.App.Views;
using Tedide.Core;
using Tedide.Core.Navigation;
using Tedide.Git;
using Terminal.Gui.Views;

namespace Tedide.App.Tests;

/// <summary>
/// The Git tab's commands and the editor's git features, through <see cref="GitIntegration"/>,
/// against a real git in a temporary repository (and a bare one beside it as the remote). Timers
/// are run by hand and dialogs answered by the test.
/// </summary>
public sealed class GitIntegrationTests : IDisposable
{
    private const string MainText = "int main(void)\n{\n    return 0;\n}\n";

    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-gitint-").FullName;
    private readonly List<Func<bool>> _timers = [];
    private readonly Workspace _workspace = new();
    private readonly EditorPane _editorPane = new();
    private readonly GitChangesView _gitView = new();
    private readonly FakeShell _shell;
    private readonly GitTracker _tracker;
    private readonly GitIntegration _git;

    public GitIntegrationTests()
    {
        _shell = new FakeShell(_editorPane);
        _tracker = new GitTracker(
            () => (_workspace.Projects.Select(p => p.Directory).ToList(), _workspace.ActiveProject?.Directory),
            action => action(),
            (_, callback) => AddTimer(callback));
        var navigation = new NavigationCommands(_shell, _workspace, _editorPane, new NavigationHistory(), new ReferencesView());
        _git = new GitIntegration(_shell, _workspace, _editorPane, new SolutionExplorerTree(), navigation, _tracker, _gitView,
            (_, callback) => AddTimer(callback));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private string Repo => Path.Combine(_dir, "repo");
    private string Remote => Path.Combine(_dir, "remote.git");
    private string Main => Path.Combine(Repo, "main.c");
    private string ProjectFile => Path.Combine(Repo, "Game.tproj");

    private void AddTimer(Func<bool> callback)
    {
        lock (_timers)
            _timers.Add(callback);
    }

    private static async Task Git(string directory, params string[] args)
    {
        var result = await ToolProcess.RunAsync(ToolProcess.StartInfo("git", args, directory));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)}: {result.Error}");
    }

    /// <summary>A repository holding the Game project, with main.c committed as "First".</summary>
    private async Task SetUpAsync(bool commit = true)
    {
        Directory.CreateDirectory(Repo);
        await Git(Repo, "init", "-q", "-b", "main");
        await Git(Repo, "config", "user.name", "Tester");
        await Git(Repo, "config", "user.email", "t@example.com");
        await Git(Repo, "config", "core.autocrlf", "false");
        File.WriteAllText(Main, MainText);
        var project = new TedideProject { Name = "Game", Target = Cc65Target.C64, SourceFiles = ["main.c"] };
        project.Save(ProjectFile);
        _workspace.Projects.Add(project);
        if (commit)
        {
            await Git(Repo, "add", ".");
            await Git(Repo, "commit", "-q", "-m", "First");
        }
        await SettleAsync();
    }

    /// <summary>Lets everything started finish: the background tasks, a git refresh (as the
    /// periodic one would), and the timers they set - repeated until nothing new is left.</summary>
    private async Task SettleAsync()
    {
        for (var round = 0; round < 10; round++)
        {
            await _shell.SettleAsync();
            _tracker.RequestRefresh();
            List<Func<bool>> due;
            lock (_timers)
            {
                due = [.. _timers];
                _timers.Clear();
            }
            foreach (var timer in due)
                timer();
            await _tracker.LastRefresh;
            await _shell.SettleAsync();
            lock (_timers)
            {
                if (_timers.Count == 0)
                    return;
            }
        }
    }

    private GitFileStatus FileStatus(string path) => _tracker.Files[path];

    /// <summary>Answers each dialog shown with the next of <paramref name="answers"/>; any after
    /// those are cancelled.</summary>
    private void AnswerDialogs(params Action<Dialog>[] answers)
    {
        var queue = new Queue<Action<Dialog>>(answers);
        _shell.Dialogs.Answer = dialog =>
        {
            if (queue.TryDequeue(out var answer))
                answer(dialog);
        };
    }

    [Fact]
    public Task StageThenCommit_ReportsTheNewCommit() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        File.WriteAllText(Main, MainText + "// more\n");
        await SettleAsync();

        _git.StageFiles([FileStatus(Main)]);
        await SettleAsync();
        Assert.True(FileStatus(Main).IsStaged);

        _git.UnstageFiles([FileStatus(Main)]);
        await SettleAsync();
        Assert.False(FileStatus(Main).IsStaged);

        _git.StageFiles([FileStatus(Main)]);
        await SettleAsync();
        _git.Commit("Second", stageAll: false, amend: false);
        await SettleAsync();

        Assert.Matches("^Committed [0-9a-f]+ Second$", _shell.Output[^1]);
        Assert.Empty(_tracker.Files);
    });

    [Fact]
    public Task Commit_SavesUnsavedEditsFirst() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        _editorPane.Open(Main);
        _editorPane.Editor.Document!.Insert(0, "// edited\n");

        _git.Commit("Edit", stageAll: true, amend: false);
        await SettleAsync();

        Assert.StartsWith("// edited", File.ReadAllText(Main));
        Assert.False(_editorPane.IsModified);
        Assert.Empty(_tracker.Files);
    });

    [Fact]
    public Task Commit_RefusesWithoutAMessage_AndAmendWithoutACommit() => UiThread.Run(async () =>
    {
        await SetUpAsync(commit: false);

        _git.Commit("  ", stageAll: true, amend: false);
        _git.Commit("", stageAll: true, amend: true);

        Assert.Equal(["Commit: Write a commit message first.", "Amend: There's no commit yet to amend."], _shell.Dialogs.Errors);
    });

    [Fact]
    public Task Amend_ReplacesTheLastCommit_AndLoadsItsMessage() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        File.WriteAllText(Main, MainText + "// fix\n");

        await _git.LoadAmendMessageAsync(true);
        Assert.Equal("", _gitView.Message);  // only loaded while the Amend box is ticked
        _git.Commit("First, fixed", stageAll: true, amend: true);
        await SettleAsync();

        Assert.Matches("^Amended [0-9a-f]+ First, fixed$", _shell.Output[^1]);
        Assert.Single(await _tracker.Primary!.GetLogAsync());
    });

    [Fact]
    public Task Discard_AsksFirst_ThenRestoresTheFile_AndTheOpenTab() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        File.WriteAllText(Main, "changed\n");
        _editorPane.Open(Main);
        await SettleAsync();

        _git.DiscardFile(FileStatus(Main));
        await SettleAsync();
        Assert.Equal("changed\n", File.ReadAllText(Main));
        Assert.StartsWith("Discard Changes: Discard the changes to main.c?", _shell.Dialogs.Messages[^1]);

        _shell.Dialogs.QueryAnswers.Enqueue(0);
        _git.DiscardFile(FileStatus(Main));
        await SettleAsync();
        Assert.Equal(MainText, File.ReadAllText(Main));
        Assert.Equal(MainText, _editorPane.Editor.Text);
    });

    [Fact]
    public Task Discard_OfAnUntrackedFile_DeletesIt_AndClosesItsTab() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        var scratch = Path.Combine(Repo, "scratch.c");
        File.WriteAllText(scratch, "int x;\n");
        _editorPane.Open(scratch);
        await SettleAsync();

        _shell.Dialogs.QueryAnswers.Enqueue(0);
        _git.DiscardFile(FileStatus(scratch));
        await SettleAsync();

        Assert.False(File.Exists(scratch));
        Assert.False(_editorPane.IsOpen(scratch));
    });

    [Fact]
    public Task Stash_PutsChangesAway_AndPopBringsThemBack() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        File.WriteAllText(Main, "work in progress\n");
        await SettleAsync();

        AnswerDialogs(dialog => Assert.IsType<StashesDialog>(dialog).Choice = (StashAction.Stash, null, "wip"));
        await _git.ShowStashesAsync();
        await SettleAsync();
        Assert.Equal("git: Changes stashed.", _shell.Output[^1]);
        Assert.Equal(MainText, File.ReadAllText(Main));

        var stash = Assert.Single(await _tracker.Primary!.GetStashesAsync());
        AnswerDialogs(dialog => Assert.IsType<StashesDialog>(dialog).Choice = (StashAction.Pop, stash, null));
        await _git.ShowStashesAsync();
        await SettleAsync();
        Assert.Equal($"git: Popped {stash.Name}.", _shell.Output[^1]);
        Assert.Equal("work in progress\n", File.ReadAllText(Main));
        Assert.Empty(await _tracker.Primary.GetStashesAsync());
    });

    [Fact]
    public Task Branches_CreateSwitchAndDelete() => UiThread.Run(async () =>
    {
        await SetUpAsync();

        AnswerDialogs(dialog => Assert.IsType<BranchesDialog>(dialog).Choice = (BranchAction.Create, null, "feature"));
        await _git.ShowBranchesAsync();
        await SettleAsync();
        Assert.Equal("git: Switched to feature.", _shell.Output[^1]);
        Assert.Equal("feature", _tracker.PrimaryStatus!.Branch);

        AnswerDialogs(dialog => Assert.IsType<BranchesDialog>(dialog).Choice = (BranchAction.Switch, new GitBranch("main", false, false, null), null));
        await _git.ShowBranchesAsync();
        await SettleAsync();
        Assert.Equal("main", _tracker.PrimaryStatus!.Branch);

        AnswerDialogs(dialog => Assert.IsType<BranchesDialog>(dialog).Choice = (BranchAction.Delete, new GitBranch("feature", false, false, null), null));
        _shell.Dialogs.Shown.Clear();
        await _git.ShowBranchesAsync();
        await SettleAsync();
        Assert.Equal("git: Deleted branch feature.", _shell.Output[^1]);
        Assert.Equal(2, _shell.Dialogs.Shown.Count);  // the list comes back after deleting
        Assert.DoesNotContain(await _tracker.Primary!.GetBranchesAsync(), b => b.Name == "feature");
    });

    [Fact]
    public Task SwitchingToABranchWithADifferentProjectFile_ReopensTheSolution() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        await Git(Repo, "switch", "-q", "-c", "other");
        File.WriteAllText(ProjectFile, File.ReadAllText(ProjectFile).Replace("\"Game\"", "\"Other\""));
        await Git(Repo, "commit", "-q", "-am", "Rename the project");
        await Git(Repo, "switch", "-q", "main");
        await SettleAsync();

        AnswerDialogs(dialog => Assert.IsType<BranchesDialog>(dialog).Choice = (BranchAction.Switch, new GitBranch("other", false, false, null), null));
        await _git.ShowBranchesAsync();
        await SettleAsync();

        Assert.Equal([ProjectFile], _shell.Opened);
    });

    [Fact]
    public Task Push_WithNoRemote_SaysSo_ThenPublishesOnceThereIsOne() => UiThread.Run(async () =>
    {
        await SetUpAsync();

        await _git.PushToRemoteAsync();
        Assert.StartsWith("Push: This repository has no remote to push to.", Assert.Single(_shell.Dialogs.Errors));

        Directory.CreateDirectory(Remote);
        await Git(Remote, "init", "-q", "--bare", "-b", "main");
        await Git(Repo, "remote", "add", "origin", Remote);
        _shell.Dialogs.QueryAnswers.Enqueue(0);
        await _git.PushToRemoteAsync();
        await SettleAsync();

        Assert.StartsWith("Publish Branch: main isn't on origin yet.", _shell.Dialogs.Messages[^1]);
        Assert.Equal("git: Push done.", _shell.Output[^1]);
        Assert.Equal("origin/main", _tracker.PrimaryStatus!.Upstream);

        _git.FetchFromRemote();
        await SettleAsync();
        Assert.Equal("git: Fetch done.", _shell.Output[^1]);

        _git.PullFromRemote();
        await SettleAsync();
        Assert.Equal("git: Pull done.", _shell.Output[^1]);
    });

    [Fact]
    public Task AMergeConflict_IsResolved_ThenContinued() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        await StartConflictingMergeAsync();

        _git.ContinueOperation(GitOperation.Merge, "");
        Assert.StartsWith("Continue merge: 1 file still has conflicts", Assert.Single(_shell.Dialogs.Errors));

        AnswerDialogs(dialog => Assert.IsType<ConflictDialog>(dialog).Choice = ConflictChoice.KeepMine);
        _git.ResolveConflict(FileStatus(Main));
        await SettleAsync();
        Assert.Equal("git: main.c resolved - kept mine.", _shell.Output[^1]);
        Assert.Contains("return 2;", File.ReadAllText(Main));

        _git.ContinueOperation(GitOperation.Merge, "Merge feature");
        await SettleAsync();
        Assert.Equal("git: merge continued.", _shell.Output[^1]);
        Assert.Equal(GitOperation.None, _tracker.PrimaryStatus!.Operation);
    });

    [Fact]
    public Task AMergeConflict_CanBeOpenedAtTheFirstConflict_OrAborted() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        await StartConflictingMergeAsync();

        AnswerDialogs(dialog => Assert.IsType<ConflictDialog>(dialog).Choice = ConflictChoice.Edit);
        _git.ResolveConflict(FileStatus(Main));
        Assert.True(_editorPane.IsShown(Main));
        Assert.StartsWith("<<<<<<<", _editorPane.Editor.Document!.GetText(_editorPane.Editor.Document.GetLineByNumber(_editorPane.CaretPosition.Line)));

        _shell.Dialogs.QueryAnswers.Enqueue(0);
        _git.AbortOperation(GitOperation.Merge);
        await SettleAsync();
        Assert.Equal("git: merge aborted.", _shell.Output[^1]);
        Assert.Equal(GitOperation.None, _tracker.PrimaryStatus!.Operation);
        Assert.Contains("return 2;", _editorPane.Editor.Text);  // the open tab followed the files back
    });

    /// <summary>main.c changed one way on main and another on feature, and feature merged in.</summary>
    private async Task StartConflictingMergeAsync()
    {
        await Git(Repo, "switch", "-q", "-c", "feature");
        File.WriteAllText(Main, MainText.Replace("return 0;", "return 1;"));
        await Git(Repo, "commit", "-q", "-am", "One");
        await Git(Repo, "switch", "-q", "main");
        File.WriteAllText(Main, MainText.Replace("return 0;", "return 2;"));
        await Git(Repo, "commit", "-q", "-am", "Two");
        var merge = await ToolProcess.RunAsync(ToolProcess.StartInfo("git", ["merge", "-q", "feature"], Repo));
        Assert.NotEqual(0, merge.ExitCode);
        await SettleAsync();
        Assert.Equal(GitOperation.Merge, _tracker.PrimaryStatus!.Operation);
        Assert.True(FileStatus(Main).IsConflicted);
    }

    [Fact]
    public Task History_ShowsACommitsChange_ThenComesBack() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        File.WriteAllText(Main, MainText.Replace("return 0;", "return 3;"));
        await Git(Repo, "commit", "-q", "-am", "Three");
        await SettleAsync();
        var commit = (await _tracker.Primary!.GetLogAsync())[0];

        AnswerDialogs(dialog => Assert.IsType<HistoryDialog>(dialog).Choice = (commit, commit.Files[0]));
        await _git.ShowHistoryAsync(null);
        await SettleAsync();

        Assert.Collection(_shell.Dialogs.Shown,
            dialog => Assert.IsType<HistoryDialog>(dialog),
            dialog => Assert.IsType<CompareDialog>(dialog),
            dialog => Assert.IsType<HistoryDialog>(dialog));
    });

    [Fact]
    public Task History_OutsideARepositoryOrWithNoCommits_SaysSo() => UiThread.Run(async () =>
    {
        await _git.ShowHistoryAsync(null);
        Assert.Equal("History: The solution isn't in a git repository.", Assert.Single(_shell.Dialogs.Errors));

        await SetUpAsync(commit: false);
        await _git.ShowHistoryAsync(null);
        Assert.Equal("History: There are no commits yet.", Assert.Single(_shell.Dialogs.Messages));
    });

    [Fact]
    public Task CompareWithLastCommit_ShowsTheChange_AndGoesToALine() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        _editorPane.Open(Main);

        _git.CompareActiveWithHead();
        await SettleAsync();
        Assert.Equal("Compare with Last Commit: main.c hasn't changed since the last commit.", Assert.Single(_shell.Dialogs.Messages));

        _editorPane.Editor.Document!.Insert(0, "// new\n");
        AnswerDialogs(dialog => Assert.IsType<CompareDialog>(dialog).GoToLine = 4);
        _git.CompareActiveWithHead();
        await SettleAsync();
        Assert.IsType<CompareDialog>(Assert.Single(_shell.Dialogs.Shown));
        Assert.Equal(4, _editorPane.CaretPosition.Line);
    });

    [Fact]
    public Task Blame_ShowsWhoChangedEachLine_ButNotForAnUntrackedFile() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        var scratch = Path.Combine(Repo, "scratch.c");
        File.WriteAllText(scratch, "int x;\n");
        await SettleAsync();

        await _git.ShowBlameAsync(scratch);
        Assert.Equal("Blame: git doesn't track scratch.c yet, so it has no history to show.", Assert.Single(_shell.Dialogs.Errors));

        AnswerDialogs(dialog => Assert.IsType<BlameDialog>(dialog).GoToLine = 3);
        await _git.ShowBlameAsync(Main);
        Assert.True(_editorPane.IsShown(Main));
        Assert.Equal(3, _editorPane.CaretPosition.Line);
    });

    [Fact]
    public Task TheEditorAnnotation_ShowsTheBranch_AndWhoChangedTheCaretLine() => UiThread.Run(async () =>
    {
        await SetUpAsync();
        _editorPane.Open(Main);
        _git.ActiveDocumentChanged();
        await SettleAsync();

        Assert.StartsWith("main  ·  Ln 1: Tester", _editorPane.Annotation);
    });
}
