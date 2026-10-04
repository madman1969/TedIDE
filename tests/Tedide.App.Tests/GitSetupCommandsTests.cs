using Tedide.App.Views;
using Tedide.Core;
using Tedide.Core.Navigation;
using Tedide.Git;

namespace Tedide.App.Tests;

/// <summary>
/// Git > Create Repository... and Git > Add Remote... through <see cref="GitIntegration"/>, against
/// a real git in a temporary folder that starts out with no repository.
/// </summary>
public sealed class GitSetupCommandsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-gitcmds-").FullName;
    private readonly List<Func<bool>> _timers = [];
    private readonly Workspace _workspace = new();
    private readonly EditorPane _editorPane = new();
    private readonly FakeShell _shell;
    private readonly GitTracker _tracker;
    private readonly GitIntegration _git;

    public GitSetupCommandsTests()
    {
        _shell = new FakeShell(_editorPane);
        _tracker = new GitTracker(
            () => (_workspace.Projects.Select(p => p.Directory).ToList(), _workspace.Solution?.Directory ?? _workspace.ActiveProject?.Directory),
            action => action(),
            (_, callback) => _timers.Add(callback));
        var navigation = new NavigationCommands(_shell, _workspace, _editorPane, new NavigationHistory(), new ReferencesView());
        _git = new GitIntegration(_shell, _workspace, _editorPane, new SolutionExplorerTree(), navigation, _tracker, new GitChangesView(),
            (_, callback) => _timers.Add(callback));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private string GameDir => Path.Combine(_dir, "Game");

    /// <summary>A new Game project and solution, as File > New Project makes them.</summary>
    private void NewGame() => _workspace.NewProject(GameDir, "Game", Cc65Target.C64);

    /// <summary>Lets the tracker find what's there now, as its timer would.</summary>
    private async Task RefreshAsync()
    {
        _tracker.RequestRefresh();
        foreach (var timer in _timers.ToList())
            timer();
        _timers.Clear();
        await _tracker.LastRefresh;
    }

    [Fact]
    public void TheRepository_GoesWhereTheSolutionAndEveryProjectAre()
    {
        NewGame();
        Assert.Equal(GameDir, GitIntegration.RepositoryRootFor(_workspace), ignoreCase: true);

        // A library beside the game's folder: the repository has to hold both.
        _workspace.AddNewProject(Path.Combine(_dir, "Gfx"), "Gfx", Cc65Target.C64, ProjectOutputType.Library);
        Assert.Equal(_dir, GitIntegration.RepositoryRootFor(_workspace), ignoreCase: true);

        Assert.Null(GitIntegration.RepositoryRootFor(new Workspace()));
    }

    [Fact]
    public Task CreateRepository_AsksFirst_ThenMakesOneWithTheIgnoreFile() => UiThread.Run(async () =>
    {
        NewGame();

        await _git.CreateRepositoryAsync();  // the question is closed: nothing happens
        Assert.Null(await GitRepository.FindAsync(GameDir));

        _shell.Dialogs.QueryAnswers.Enqueue(0);
        await _git.CreateRepositoryAsync();

        Assert.StartsWith($"Create Repository: Make {GameDir} a git repository?", _shell.Dialogs.Messages[^1].Replace('\n', ' '));
        Assert.Equal(GameDir, (await GitRepository.FindAsync(GameDir))!.Root, ignoreCase: true);
        Assert.Contains("*.session.json", File.ReadAllLines(Path.Combine(GameDir, ".gitignore")));
        Assert.StartsWith($"git: Created a repository in {GameDir}.", _shell.Output[^1]);
        await RefreshAsync();
        Assert.NotNull(_tracker.Primary);
    });

    [Fact]
    public Task CreateRepository_FromNewProject_DoesntAsk_AndIsRefusedInsideARepository() => UiThread.Run(async () =>
    {
        NewGame();

        await _git.CreateRepositoryAsync(ask: false);
        Assert.Empty(_shell.Dialogs.Messages);
        Assert.NotNull(await GitRepository.FindAsync(GameDir));

        await _git.CreateRepositoryAsync(ask: false);
        Assert.StartsWith($"Create Repository: {GameDir} is already a git repository.", Assert.Single(_shell.Dialogs.Errors).Replace('\n', ' '));
    });

    [Fact]
    public Task CreateRepository_WithNothingLoaded_SaysToOpenAProject() => UiThread.Run(async () =>
    {
        await _git.CreateRepositoryAsync();

        Assert.StartsWith("No project loaded.", Assert.Single(_shell.Output));
    });

    [Fact]
    public Task AddRemote_NeedsARepository_ThenAddsOrRepointsOrigin() => UiThread.Run(async () =>
    {
        NewGame();
        await RefreshAsync();
        await _git.AddRemoteAsync();
        Assert.StartsWith("Add Remote: The solution isn't in a git repository yet.", Assert.Single(_shell.Dialogs.Errors));

        await _git.CreateRepositoryAsync(ask: false);
        await RefreshAsync();
        _shell.Dialogs.Answer = dialog =>
        {
            var add = Assert.IsType<AddRemoteDialog>(dialog);
            Assert.Equal("origin", add._nameField.Text);
            (add.RemoteName, add.Url) = ("origin", "https://github.com/you/Game.git");
        };
        await _git.AddRemoteAsync();

        var repository = (await GitRepository.FindAsync(GameDir))!;
        Assert.Equal("https://github.com/you/Game.git", await repository.GetRemoteUrlAsync("origin"));
        Assert.Equal("git: origin is https://github.com/you/Game.git. Push in the Git tab publishes the branch there.", _shell.Output[^1]);

        // Again, with a different address: replaced, after asking.
        _shell.Dialogs.Answer = dialog =>
        {
            var add = (AddRemoteDialog)dialog;
            (add.RemoteName, add.Url) = ("origin", "https://github.com/you/Other.git");
        };
        await _git.AddRemoteAsync();  // the Replace question is closed: unchanged
        Assert.Equal("https://github.com/you/Game.git", await repository.GetRemoteUrlAsync("origin"));
        _shell.Dialogs.QueryAnswers.Enqueue(0);
        await _git.AddRemoteAsync();
        Assert.StartsWith("Replace Remote: origin already points at https://github.com/you/Game.git.", _shell.Dialogs.Messages[^1].Replace('\n', ' '));
        Assert.Equal("https://github.com/you/Other.git", await repository.GetRemoteUrlAsync("origin"));
    });

    [Theory]
    [InlineData("origin", "https://github.com/you/Game.git", null)]
    [InlineData("", "https://github.com/you/Game.git", "Give the remote a name")]
    [InlineData("my origin", "https://github.com/you/Game.git", "can't be a remote's name")]
    [InlineData("origin", "  ", "Paste the repository's address.")]
    [InlineData("origin", "https://github.com/you/My Game.git", "can't contain spaces")]
    public void AddRemoteDialog_ChecksTheObvious(string name, string url, string? problem)
    {
        var found = AddRemoteDialog.Validate(name, url);

        if (problem is null)
            Assert.Null(found);
        else
            Assert.Contains(problem, found);
    }
}
