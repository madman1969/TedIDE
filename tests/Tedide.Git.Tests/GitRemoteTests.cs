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
    public async Task GetLogAsync_ListsCommitsWithTheirFiles_AndFollowsAFileThroughARename()
    {
        var repository = await SetUpAsync();                       // "First": adds main.c
        Write(Mine, "other.c", "x\n");
        await repository.CommitAsync("Add other", stageAll: true);
        await Git(Mine, "mv", "main.c", "game.c");
        await repository.CommitAsync("Rename main to game", stageAll: true);
        Write(Mine, "game.c", "one\nTWO\nthree\n");
        await repository.CommitAsync("Edit game", stageAll: true);

        var log = await repository.GetLogAsync();
        Assert.Equal(["Edit game", "Rename main to game", "Add other", "First"], log.Select(c => c.Subject));
        Assert.Equal("Tester", log[0].Author);
        Assert.Equal(new GitCommitFile('M', "game.c"), Assert.Single(log[0].Files));
        Assert.Equal(new GitCommitFile('R', "game.c", "main.c"), Assert.Single(log[1].Files));
        Assert.Equal(new GitCommitFile('A', "main.c"), Assert.Single(log[3].Files));

        var history = await repository.GetLogAsync(Path.Combine(Mine, "game.c"));
        Assert.Equal(["Edit game", "Rename main to game", "First"], history.Select(c => c.Subject));

        var edit = await repository.GetCommitDiffAsync(log[0], log[0].Files[0]);
        Assert.Equal((1, 1), (edit.Added, edit.Removed));
        Assert.Contains(new DiffLine(DiffLineKind.Added, "TWO", null, 2), Assert.Single(edit.Hunks).Lines);
        // The first commit has no parent: everything is added.
        Assert.Equal((3, 0), (await repository.GetCommitDiffAsync(log[3], log[3].Files[0])) is var first ? (first.Added, first.Removed) : default);
    }

    [Fact]
    public void ParseLog_ReadsHeadersAndNameStatus()
    {
        var output = "\x1e" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\x1f" + "Ann Other\x1f" + "1759500000\x1f" + "Fix: a\tb\n\nM\tsrc/a.c\nR087\told.c\tnew.c\n"
            + "\x1e" + "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\x1f" + "Me\x1f" + "1759400000\x1f" + "Empty\n";

        var log = GitCommit.ParseLog(output);

        Assert.Equal(2, log.Count);
        Assert.Equal("aaaaaaa", log[0].ShortHash);
        Assert.Equal("Fix: a\tb", log[0].Subject);
        Assert.Equal([new GitCommitFile('M', "src/a.c"), new GitCommitFile('R', "new.c", "old.c")], log[0].Files);
        Assert.Empty(log[1].Files);
    }

    /// <summary>"mine" on main and a branch "other" that both changed line 2 of main.c.</summary>
    private async Task<GitRepository> DivergeAsync()
    {
        var repository = await SetUpAsync();
        await repository.CreateBranchAsync("other");
        Write(Mine, "main.c", "one\nTHEIRS\nthree\n");
        await repository.CommitAsync("Theirs", stageAll: true);
        await repository.SwitchAsync(new GitBranch("main", false, false, null));
        Write(Mine, "main.c", "one\nMINE\nthree\n");
        await repository.CommitAsync("Mine", stageAll: true);
        return repository;
    }

    [Fact]
    public async Task MergeConflict_IsDetected_ResolvedWithEitherSide_AndContinued()
    {
        var repository = await DivergeAsync();
        var main = Path.Combine(Mine, "main.c");

        var merge = await GitRunner.RunAsync(Mine, ["merge", "--no-edit", "other"]);
        Assert.False(merge.Succeeded);
        Assert.StartsWith("git stopped on conflicts", merge.Explanation);
        var status = (await repository.GetStatusAsync())!;
        Assert.Equal(GitOperation.Merge, status.Operation);
        Assert.True(Assert.Single(status.Files).IsConflicted);

        Assert.True((await repository.ResolveWithAsync(main, keepMine: false, GitOperation.Merge)).Succeeded);
        Assert.Equal("one\nTHEIRS\nthree\n", File.ReadAllText(main));
        Assert.True((await repository.ResolveWithAsync(main, keepMine: true, GitOperation.Merge)).Succeeded);
        Assert.Equal("one\nMINE\nthree\n", File.ReadAllText(main));

        Assert.True((await repository.ContinueAsync(GitOperation.Merge)).Succeeded);
        status = (await repository.GetStatusAsync())!;
        Assert.Equal(GitOperation.None, status.Operation);
        Assert.Empty(status.Files);
        Assert.StartsWith("Merge branch 'other'", (await repository.DescribeHeadAsync())![8..]);
    }

    [Fact]
    public async Task RebaseConflict_KeepMineMeansTheUsersCommit_AndContinuesWithoutAnEditor()
    {
        var repository = await DivergeAsync();
        var main = Path.Combine(Mine, "main.c");

        Assert.False((await GitRunner.RunAsync(Mine, ["rebase", "other"])).Succeeded);
        Assert.Equal(GitOperation.Rebase, (await repository.GetStatusAsync())!.Operation);

        // git's --ours during a rebase is "other"; Keep Mine must still mean the user's "Mine".
        Assert.True((await repository.ResolveWithAsync(main, keepMine: true, GitOperation.Rebase)).Succeeded);
        Assert.Equal("one\nMINE\nthree\n", File.ReadAllText(main));

        var continued = await repository.ContinueAsync(GitOperation.Rebase);
        Assert.True(continued.Succeeded, continued.Message);
        Assert.Equal(GitOperation.None, (await repository.GetStatusAsync())!.Operation);
        Assert.EndsWith(" Mine", await repository.DescribeHeadAsync());
    }

    [Fact]
    public async Task AbortAsync_PutsTheBranchBackAsItWas()
    {
        var repository = await DivergeAsync();
        await GitRunner.RunAsync(Mine, ["merge", "--no-edit", "other"]);

        Assert.True((await repository.AbortAsync(GitOperation.Merge)).Succeeded);

        var status = (await repository.GetStatusAsync())!;
        Assert.Equal(GitOperation.None, status.Operation);
        Assert.Empty(status.Files);
        Assert.Equal("one\nMINE\nthree\n", File.ReadAllText(Path.Combine(Mine, "main.c")));
    }

    [Fact]
    public async Task Amend_ReplacesTheLastCommit_KeepingOrChangingItsMessage()
    {
        var repository = await SetUpAsync();                  // "First"
        Write(Mine, "main.c", "one\ntwo\nthree\nfour\n");
        await repository.CommitAsync("Second, with a tpyo\n\nAnd a body.", stageAll: true);
        Assert.Equal("Second, with a tpyo\n\nAnd a body.", await repository.GetLastCommitMessageAsync());

        // A new message, nothing else staged.
        Assert.True((await repository.CommitAsync("Second", stageAll: false, amend: true)).Succeeded);
        var log = await repository.GetLogAsync();
        Assert.Equal(["Second", "First"], log.Select(c => c.Subject));

        // An empty message keeps it; what's staged joins the commit.
        Write(Mine, "forgotten.c", "x\n");
        Assert.True((await repository.CommitAsync("", stageAll: true, amend: true)).Succeeded);
        log = await repository.GetLogAsync();
        Assert.Equal(["Second", "First"], log.Select(c => c.Subject));
        Assert.Contains(new GitCommitFile('A', "forgotten.c"), log[0].Files);
        Assert.Empty((await repository.GetStatusAsync())!.Files);
    }

    [Fact]
    public async Task Stash_PutsEverythingAway_AndApplyPopDropBringItBackOrNot()
    {
        var repository = await SetUpAsync();
        var main = Path.Combine(Mine, "main.c");
        Write(Mine, "main.c", "edited\n");
        Write(Mine, "new.c", "new\n");

        Assert.True((await repository.StashAsync("Work in progress")).Succeeded);
        Assert.Empty((await repository.GetStatusAsync())!.Files);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(main));
        Assert.False(File.Exists(Path.Combine(Mine, "new.c")));
        var stash = Assert.Single(await repository.GetStashesAsync());
        Assert.Equal("stash@{0}", stash.Name);
        Assert.Equal("On main: Work in progress", stash.Description);

        // Nothing left to stash: git says so and succeeds.
        Assert.Contains("No local changes to save", (await repository.StashAsync()).Message);

        // Apply keeps the stash; pop removes it.
        Assert.True((await repository.UnstashAsync(stash, drop: false)).Succeeded);
        Assert.Equal("edited\n", File.ReadAllText(main));
        Assert.Single(await repository.GetStashesAsync());
        await Git(Mine, "checkout", "--", "main.c");
        File.Delete(Path.Combine(Mine, "new.c"));
        Assert.True((await repository.UnstashAsync(stash, drop: true)).Succeeded);
        Assert.Equal("edited\n", File.ReadAllText(main));
        Assert.True(File.Exists(Path.Combine(Mine, "new.c")));
        Assert.Empty(await repository.GetStashesAsync());

        // Drop throws one away.
        await repository.StashAsync();
        Assert.True((await repository.DropStashAsync(Assert.Single(await repository.GetStashesAsync()))).Succeeded);
        Assert.Empty(await repository.GetStashesAsync());
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(main));
    }

    [Fact]
    public async Task Pop_ThatClashes_StopsOnAConflict_AndKeepsTheStash()
    {
        var repository = await SetUpAsync();
        Write(Mine, "main.c", "one\nSTASHED\nthree\n");
        await repository.StashAsync();
        Write(Mine, "main.c", "one\nCOMMITTED\nthree\n");
        await repository.CommitAsync("Meanwhile", stageAll: true);

        var popped = await repository.UnstashAsync(Assert.Single(await repository.GetStashesAsync()), drop: true);

        Assert.False(popped.Succeeded);
        Assert.True(Assert.Single((await repository.GetStatusAsync())!.Files).IsConflicted);
        Assert.Single(await repository.GetStashesAsync());
    }

    [Fact]
    public void Stash_ParseReadsNameDescriptionAndTime()
    {
        // \u001f, not \x1f: \x takes up to four hex digits, so "\x1f1759..." is one wrong character.
        var stashes = GitStash.Parse("stash@{0}\u001fOn main: tidy\u001f1759500000\nstash@{1}\u001fWIP on main: abc1234 First\u001f1759400000\n");
        Assert.Equal(
            [
                new GitStash("stash@{0}", "On main: tidy", DateTimeOffset.FromUnixTimeSeconds(1759500000)),
                new GitStash("stash@{1}", "WIP on main: abc1234 First", DateTimeOffset.FromUnixTimeSeconds(1759400000)),
            ],
            stashes);
    }

    [Fact]
    public void Explanation_SuggestsStashingWhenChangesBlockAPull()
    {
        var error = "error: Your local changes to the following files would be overwritten by merge:\n\tmain.c";
        Assert.StartsWith("Your uncommitted changes clash with what's coming in. Commit or stash them", new GitResult(1, "", error).Explanation);
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
