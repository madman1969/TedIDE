namespace Tedide.Git.Tests;

/// <summary>
/// Fetch, pull and push against a real git: a bare repository in a temporary folder plays the
/// remote (a plain path, so no login), with two clones - "mine", which the tests drive through
/// <see cref="GitRepository"/>, and "theirs", standing in for someone else pushing.
/// </summary>
public sealed class GitRemoteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-remote-").FullName;
    private string Remote => Path.Combine(_dir, "remote.git");
    private string Mine => Path.Combine(_dir, "mine");
    private string Theirs => Path.Combine(_dir, "theirs");

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private static async Task Git(string directory, params string[] args)
    {
        var result = await GitRunner.RunAsync(directory, args);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.Message}");
    }

    private static async Task Configure(string directory)
    {
        await Git(directory, "config", "user.name", "Tester");
        await Git(directory, "config", "user.email", "t@example.com");
        await Git(directory, "config", "core.autocrlf", "false");
    }

    private static void Write(string directory, string file, string text) =>
        File.WriteAllText(Path.Combine(directory, file), text);

    /// <summary>"mine" with one commit, not yet pushed; the remote empty; no "theirs" yet.</summary>
    private async Task<GitRepository> SetUpAsync()
    {
        Directory.CreateDirectory(Remote);
        await Git(Remote, "init", "-q", "--bare", "-b", "main");
        Directory.CreateDirectory(Mine);
        await Git(Mine, "init", "-q", "-b", "main");
        await Configure(Mine);
        await Git(Mine, "remote", "add", "origin", Remote);
        Write(Mine, "main.c", "one\ntwo\nthree\n");
        var repository = (await GitRepository.FindAsync(Mine))!;
        Assert.True((await repository.CommitAsync("First", stageAll: true)).Succeeded);
        return repository;
    }

    /// <summary>A second clone that commits <paramref name="text"/> to main.c and pushes it.</summary>
    private async Task TheyPushAsync(string text, string message)
    {
        if (!Directory.Exists(Theirs))
        {
            await Git(_dir, "clone", "-q", Remote, Theirs);
            await Configure(Theirs);
        }
        else
            await Git(Theirs, "pull", "-q", "--no-rebase");
        Write(Theirs, "main.c", text);
        await Git(Theirs, "commit", "-q", "-am", message);
        await Git(Theirs, "push", "-q");
    }

    [Fact]
    public async Task PushAsync_PublishesANewBranchThenPushesToItsUpstream()
    {
        var repository = await SetUpAsync();
        Assert.Equal(["origin"], await repository.GetRemotesAsync());
        Assert.Null((await repository.GetStatusAsync())!.Upstream);

        // Plain push has nowhere to go yet.
        Assert.False((await repository.PushAsync()).Succeeded);

        Assert.True((await repository.PushAsync(publishTo: "origin", branch: "main")).Succeeded);
        var status = (await repository.GetStatusAsync())!;
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(0, status.Ahead);

        Write(Mine, "main.c", "one\ntwo\nthree\nfour\n");
        await repository.CommitAsync("Second", stageAll: true);
        Assert.Equal(1, (await repository.GetStatusAsync())!.Ahead);
        Assert.True((await repository.PushAsync()).Succeeded);
        Assert.Equal(0, (await repository.GetStatusAsync())!.Ahead);
    }

    [Fact]
    public async Task FetchThenPull_FastForwardsToTheirCommit()
    {
        var repository = await SetUpAsync();
        await repository.PushAsync(publishTo: "origin", branch: "main");
        await TheyPushAsync("one\nTWO\nthree\n", "Theirs");

        // Fetch only learns about it.
        Assert.True((await repository.FetchAsync()).Succeeded);
        Assert.Equal(1, (await repository.GetStatusAsync())!.Behind);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(Path.Combine(Mine, "main.c")));

        Assert.True((await repository.PullAsync()).Succeeded);
        Assert.Equal(0, (await repository.GetStatusAsync())!.Behind);
        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(Path.Combine(Mine, "main.c")));
    }

    [Fact]
    public async Task Push_IsRejectedWhenTheRemoteMovedOn_AndPullMergesWithoutAnEditor()
    {
        var repository = await SetUpAsync();
        await repository.PushAsync(publishTo: "origin", branch: "main");
        await TheyPushAsync("zero\none\ntwo\nthree\n", "Theirs: add a line at the top");
        Write(Mine, "main.c", "one\ntwo\nthree\nfour\n");
        await repository.CommitAsync("Mine: add a line at the bottom", stageAll: true);

        var rejected = await repository.PushAsync();
        Assert.False(rejected.Succeeded);
        Assert.StartsWith("The remote has commits you don't have yet. Pull first", rejected.Explanation);

        // Divergent, with no pull.rebase/pull.ff set: merged, as Visual Studio does.
        var pulled = await repository.PullAsync();
        Assert.True(pulled.Succeeded, pulled.Message);
        Assert.Equal("zero\none\ntwo\nthree\nfour\n", File.ReadAllText(Path.Combine(Mine, "main.c")));
        Assert.True((await repository.PushAsync()).Succeeded);
    }

    [Fact]
    public async Task PullAsync_StopsOnAConflict_LeavingTheFileConflicted()
    {
        var repository = await SetUpAsync();
        await repository.PushAsync(publishTo: "origin", branch: "main");
        await TheyPushAsync("one\nTHEIRS\nthree\n", "Theirs");
        Write(Mine, "main.c", "one\nMINE\nthree\n");
        await repository.CommitAsync("Mine", stageAll: true);

        Assert.False((await repository.PullAsync()).Succeeded);
        var file = Assert.Single((await repository.GetStatusAsync())!.Files);
        Assert.True(file.IsConflicted);
    }

    [Fact]
    public async Task PullAsync_FollowsTheUsersRebaseSetting()
    {
        var repository = await SetUpAsync();
        await repository.PushAsync(publishTo: "origin", branch: "main");
        await TheyPushAsync("zero\none\ntwo\nthree\n", "Theirs");
        Write(Mine, "main.c", "one\ntwo\nthree\nfour\n");
        await repository.CommitAsync("Mine", stageAll: true);
        await Git(Mine, "config", "pull.rebase", "true");

        Assert.True((await repository.PullAsync()).Succeeded);
        // Rebased: no merge commit, so HEAD is still "Mine", now on top of "Theirs".
        Assert.EndsWith(" Mine", await repository.DescribeHeadAsync());
        Assert.Equal(1, (await repository.GetStatusAsync())!.Ahead);
    }

    [Theory]
    [InlineData("fatal: User cancelled dialog.", "Sign-in was cancelled.")]
    [InlineData("fatal: Cannot prompt because user interactivity has been disabled.", "Git Credential Manager isn't allowed")]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", "git needs a login")]
    [InlineData("git@github.com: Permission denied (publickey).", "The remote refused your SSH key.")]
    [InlineData(" ! [rejected]        main -> main (fetch first)", "The remote has commits you don't have yet.")]
    public void Explanation_PutsCommonFailuresPlainly(string error, string expectedStart)
    {
        var explanation = new GitResult(128, "", error).Explanation;
        Assert.StartsWith(expectedStart, explanation);
        Assert.EndsWith(error.Trim(), explanation);
    }

    [Fact]
    public async Task Branches_ListsLocalThenUntrackedRemote_AndSwitchingToARemoteOneTracksIt()
    {
        var repository = await SetUpAsync();
        await repository.PushAsync(publishTo: "origin", branch: "main");
        await TheyPushAsync("one\ntwo\nthree\nfour\n", "Theirs"); // creates their clone
        await Git(Theirs, "switch", "-q", "-c", "topic");
        Write(Theirs, "main.c", "topic\n");
        await Git(Theirs, "commit", "-q", "-am", "Topic work");
        await Git(Theirs, "push", "-q", "-u", "origin", "topic");
        await repository.FetchAsync();

        var branches = await repository.GetBranchesAsync();
        Assert.Equal(
            [new GitBranch("main", false, true, "origin/main"), new GitBranch("origin/topic", true, false, null)],
            branches);
        Assert.Equal("topic", branches[1].LocalName);

        Assert.True((await repository.SwitchAsync(branches[1])).Succeeded);
        var status = (await repository.GetStatusAsync())!;
        Assert.Equal("topic", status.Branch);
        Assert.Equal("origin/topic", status.Upstream);
        Assert.Equal("topic\n", File.ReadAllText(Path.Combine(Mine, "main.c")));
        // Now tracked locally, the remote one isn't offered separately.
        Assert.DoesNotContain(await repository.GetBranchesAsync(), b => b.IsRemote);
    }

    [Fact]
    public async Task CreateSwitchAndDelete_OnlyDeletesMergedBranches()
    {
        var repository = await SetUpAsync();
        var main = new GitBranch("main", false, false, null);

        Assert.True((await repository.CreateBranchAsync("merged")).Succeeded);
        Assert.Equal("merged", (await repository.GetStatusAsync())!.Branch);
        Assert.True((await repository.SwitchAsync(main)).Succeeded);
        Assert.True((await repository.DeleteBranchAsync("merged")).Succeeded);

        await repository.CreateBranchAsync("unmerged");
        Write(Mine, "main.c", "changed\n");
        await repository.CommitAsync("Only on unmerged", stageAll: true);
        await repository.SwitchAsync(main);
        var refused = await repository.DeleteBranchAsync("unmerged");
        Assert.False(refused.Succeeded);
        Assert.StartsWith("That branch has commits that aren't merged", refused.Explanation);
        Assert.Contains(await repository.GetBranchesAsync(), b => b.Name == "unmerged");

        var badName = await repository.CreateBranchAsync("two words");
        Assert.StartsWith("That isn't a valid branch name", badName.Explanation);
        Assert.StartsWith("A branch with that name already exists.", (await repository.CreateBranchAsync("unmerged")).Explanation);
    }

    [Fact]
    public async Task SwitchAsync_RefusesWhenUncommittedChangesClash()
    {
        var repository = await SetUpAsync();
        await repository.CreateBranchAsync("other");
        Write(Mine, "main.c", "other's version\n");
        await repository.CommitAsync("Other", stageAll: true);
        await repository.SwitchAsync(new GitBranch("main", false, false, null));
        Write(Mine, "main.c", "an uncommitted edit\n");

        var refused = await repository.SwitchAsync(new GitBranch("other", false, false, null));

        Assert.False(refused.Succeeded);
        Assert.StartsWith("Your uncommitted changes clash", refused.Explanation);
        Assert.Equal("main", (await repository.GetStatusAsync())!.Branch);
        Assert.Equal("an uncommitted edit\n", File.ReadAllText(Path.Combine(Mine, "main.c")));
    }

    [Fact]
    public void Branch_ParseSkipsSymbolicRefs()
    {
        var output = string.Join('\n',
            "refs/heads/main\tmain\torigin/main\t*\t",
            "refs/heads/old\told\t\t \t",
            "refs/remotes/origin/HEAD\torigin\t\t \trefs/remotes/origin/main",
            "refs/remotes/origin/main\torigin/main\t\t \t",
            "refs/remotes/origin/old\torigin/old\t\t \t",
            "refs/remotes/origin/new\torigin/new\t\t \t",
            "");

        Assert.Equal(
            [
                new GitBranch("main", false, true, "origin/main"),
                new GitBranch("old", false, false, null),
                new GitBranch("origin/new", true, false, null),
            ],
            GitBranch.Parse(output));
    }

    [Fact]
    public void Explanation_OfAnythingElseIsGitsOwnMessage()
    {
        Assert.Equal("fatal: something else", new GitResult(1, "", "fatal: something else\n").Explanation);
    }
}
