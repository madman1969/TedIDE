namespace Tedide.Git.Tests;

/// <summary>Against a real git, in a fresh temporary repository per test.</summary>
public sealed class GitRepositoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-git-").FullName;

    public void Dispose()
    {
        // git makes its object files read-only.
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private async Task<GitRepository> InitAsync()
    {
        foreach (var args in (string[][])[["init", "-q", "-b", "main"], ["config", "user.name", "Tester"], ["config", "user.email", "t@example.com"], ["config", "core.autocrlf", "false"]])
            Assert.True((await GitRunner.RunAsync(_dir, args)).Succeeded);
        return (await GitRepository.FindAsync(Path.Combine(_dir)))!;
    }

    [Fact]
    public async Task FindAsync_IsNullOutsideARepository()
    {
        Assert.Null(await GitRepository.FindAsync(_dir));
        Assert.Null(await GitRepository.FindAsync(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public async Task StageCommitModifyAndDiscard_RoundTrip()
    {
        var repo = await InitAsync();
        Assert.Equal(Path.GetFullPath(_dir), repo.Root, ignoreCase: true);
        var main = Write("src/main.c", "int main(void)\n{\n    return 0;\n}\n");

        var status = (await repo.GetStatusAsync())!;
        Assert.False(status.HasCommits);
        Assert.Equal('?', Assert.Single(status.Files).Marker);

        Assert.True((await repo.StageAsync([main])).Succeeded);
        Assert.True(Assert.Single((await repo.GetStatusAsync())!.Files).IsStaged);

        // Unstaging before the first commit can't restore from HEAD - it takes the file out of the index.
        Assert.True((await repo.UnstageAsync([main], hasCommits: false)).Succeeded);
        Assert.True(Assert.Single((await repo.GetStatusAsync())!.Files).IsUntracked);

        Assert.True((await repo.CommitAsync("First commit\n\nWith a body.", stageAll: true)).Succeeded);
        Assert.Equal("First commit", (await repo.DescribeHeadAsync())![8..]);
        status = (await repo.GetStatusAsync())!;
        Assert.True(status.HasCommits);
        Assert.Empty(status.Files);

        File.WriteAllText(main, "int main(void)\n{\n    return 1;\n}\n");
        var changed = Assert.Single((await repo.GetStatusAsync())!.Files);
        Assert.Equal('M', changed.Marker);

        Assert.True((await repo.DiscardAsync([changed])).Succeeded);
        Assert.Contains("return 0;", File.ReadAllText(main));
        Assert.Empty((await repo.GetStatusAsync())!.Files);
    }

    [Fact]
    public async Task DiscardAsync_DeletesAnUntrackedFile_AndUnstageAfterCommitsKeepsTheChange()
    {
        var repo = await InitAsync();
        var main = Write("main.c", "a\n");
        await repo.CommitAsync("Start", stageAll: true);

        var scratch = Write("scratch.c", "x\n");
        await repo.DiscardAsync([Assert.Single((await repo.GetStatusAsync())!.Files)]);
        Assert.False(File.Exists(scratch));

        File.WriteAllText(main, "b\n");
        await repo.StageAsync([main]);
        Assert.True((await repo.UnstageAsync([main], hasCommits: true)).Succeeded);
        var file = Assert.Single((await repo.GetStatusAsync())!.Files);
        Assert.False(file.IsStaged);
        Assert.Equal("b\n", File.ReadAllText(main));
    }

    [Fact]
    public async Task BlameLineAsync_UsesTheEditorsText()
    {
        var repo = await InitAsync();
        var main = Write("main.c", "one\ntwo\n");
        await repo.CommitAsync("Add main", stageAll: true);

        var committed = (await repo.BlameLineAsync(main, 2, "one\ntwo\n"))!;
        Assert.True(committed.IsCommitted);
        Assert.Equal("Tester", committed.Author);
        Assert.Equal("Add main", committed.Summary);

        // An unsaved edit in the editor: line 2 is new, and the committed "two" moved to line 3.
        Assert.False((await repo.BlameLineAsync(main, 2, "one\nedited\ntwo\n"))!.IsCommitted);
        Assert.True((await repo.BlameLineAsync(main, 3, "one\nedited\ntwo\n"))!.IsCommitted);

        // A file git doesn't track has no blame.
        Assert.Null(await repo.BlameLineAsync(Write("new.c", "x\n"), 1, "x\n"));
    }

    [Fact]
    public async Task CommitAsync_ReportsWhyNothingWasCommitted()
    {
        var repo = await InitAsync();
        var result = await repo.CommitAsync("Empty", stageAll: false);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public async Task DiffWithHeadAsync_ComparesTheEditorsTextWithTheLastCommit()
    {
        var repo = await InitAsync();
        var main = Write("src/main.c", "one\ntwo\nthree\n");
        await repo.CommitAsync("Add main", stageAll: true);

        // Unchanged, even with the editor's CRLF line endings.
        Assert.Empty((await repo.DiffWithHeadAsync(main, "one\r\ntwo\r\nthree\r\n")).Hunks);

        var diff = await repo.DiffWithHeadAsync(main, "one\nTWO\nthree\nfour\n");
        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal((2, 1), (diff.Added, diff.Removed));
        Assert.Contains(new DiffLine(DiffLineKind.Removed, "two", 2, null), hunk.Lines);
        Assert.Contains(new DiffLine(DiffLineKind.Added, "four", null, 4), hunk.Lines);

        // A deleted file is all removed; a new one all added.
        var gone = await repo.DiffWithHeadAsync(main, null);
        Assert.Equal((0, 3), (gone.Added, gone.Removed));
        var added = await repo.DiffWithHeadAsync(Write("new.c", "x\n"), "x\ny\n");
        Assert.Equal((2, 0), (added.Added, added.Removed));
        Assert.Null(await repo.ReadHeadAsync(Path.Combine(_dir, "new.c")));
    }
}
