namespace Tedide.Git;

/// <summary>
/// A git working tree, by its root folder, and the handful of operations Tedide offers on it:
/// status, blame, diffs, history, stage, unstage, discard, commit, branches, fetch/pull/push and
/// resolving conflicts. Every method runs git in the background and returns its result; nothing
/// here touches the UI.
/// </summary>
public sealed class GitRepository
{
    public string Root { get; }

    /// <summary>The repository's .git folder (a worktree's own, for a linked worktree) - where git
    /// keeps the files that say a merge or rebase is in progress.</summary>
    public string GitDirectory { get; }

    private GitRepository(string root, string gitDirectory)
    {
        Root = root;
        GitDirectory = gitDirectory;
    }

    /// <summary>The repository <paramref name="directory"/> is in, or null if it isn't in one (or
    /// git isn't installed - the IDE then simply shows no git information).</summary>
    public static async Task<GitRepository?> FindAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
            return null;
        // The root is worked out from the directory as given (--show-cdup is "../.." or empty), not
        // taken from --show-toplevel: git resolves a subst drive or a junction to the real path,
        // and then no file Tedide has open would match it.
        var result = await GitRunner.RunAsync(directory, ["rev-parse", "--absolute-git-dir", "--show-cdup"], cancellationToken: cancellationToken);
        if (!result.Succeeded || result.Output.Split('\n', StringSplitOptions.TrimEntries) is not [var gitDirectory, .. var rest] || gitDirectory.Length == 0)
            return null;
        var up = rest is [var cdup, ..] ? cdup : "";
        return new GitRepository(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(directory, up))), Path.GetFullPath(gitDirectory));
    }

    public async Task<GitStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await GitRunner.RunAsync(Root, ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"],
            cancellationToken: cancellationToken);
        return result.Succeeded ? GitStatus.Parse(result.Output, Root) with { Operation = GetOperation() } : null;
    }

    /// <summary>The merge, rebase, cherry-pick or revert that's stopped part-way (on conflicts, or
    /// waiting to be continued), read from the marker files git leaves in its folder.</summary>
    public GitOperation GetOperation() =>
        Directory.Exists(Path.Combine(GitDirectory, "rebase-merge")) || Directory.Exists(Path.Combine(GitDirectory, "rebase-apply"))
            ? GitOperation.Rebase
        : File.Exists(Path.Combine(GitDirectory, "MERGE_HEAD")) ? GitOperation.Merge
        : File.Exists(Path.Combine(GitDirectory, "CHERRY_PICK_HEAD")) ? GitOperation.CherryPick
        : File.Exists(Path.Combine(GitDirectory, "REVERT_HEAD")) ? GitOperation.Revert
        : GitOperation.None;

    /// <summary>
    /// Settles a conflicted file by taking one side whole - <paramref name="keepMine"/> for the
    /// user's own work, else the incoming change - and marks it resolved. During a rebase git's
    /// "ours" is the branch being rebased onto and "theirs" the user's commit, so the sides swap.
    /// </summary>
    public async Task<GitResult> ResolveWithAsync(string file, bool keepMine, GitOperation operation, CancellationToken cancellationToken = default)
    {
        var side = keepMine != (operation == GitOperation.Rebase) ? "--ours" : "--theirs";
        // Brings the conflict back first, from the resolve-undo record git keeps: once a file's
        // resolved its two sides are gone from the index, and choosing again took the resolved
        // version whatever the side (caught by a test).
        await GitRunner.RunAsync(Root, ["checkout", "-m", "--", Relative(file)], cancellationToken: cancellationToken);
        var taken = await GitRunner.RunAsync(Root, ["checkout", side, "--", Relative(file)], cancellationToken: cancellationToken);
        return taken.Succeeded
            ? await GitRunner.RunAsync(Root, ["add", "--", Relative(file)], cancellationToken: cancellationToken)
            : taken;
    }

    /// <summary>
    /// Finishes <paramref name="operation"/> once its conflicts are resolved: a merge commits
    /// (with <paramref name="message"/>, else git's prepared message), the others continue. git is
    /// never allowed an editor - nobody would see it.
    /// </summary>
    public Task<GitResult> ContinueAsync(GitOperation operation, string? message = null, CancellationToken cancellationToken = default) =>
        operation switch
        {
            GitOperation.Merge when !string.IsNullOrWhiteSpace(message) =>
                GitRunner.RunAsync(Root, ["-c", "core.editor=true", "commit", "-F", "-"], message.Trim(), cancellationToken),
            GitOperation.Merge => GitRunner.RunAsync(Root, ["-c", "core.editor=true", "commit", "--no-edit"], cancellationToken: cancellationToken),
            GitOperation.Rebase => GitRunner.RunAsync(Root, ["-c", "core.editor=true", "rebase", "--continue"], cancellationToken: cancellationToken),
            GitOperation.CherryPick => GitRunner.RunAsync(Root, ["-c", "core.editor=true", "cherry-pick", "--continue"], cancellationToken: cancellationToken),
            GitOperation.Revert => GitRunner.RunAsync(Root, ["-c", "core.editor=true", "revert", "--continue"], cancellationToken: cancellationToken),
            _ => Task.FromResult(new GitResult(1, "", "Nothing is in progress to continue.")),
        };

    /// <summary>Abandons <paramref name="operation"/>, putting the branch and files back as they
    /// were before it started.</summary>
    public Task<GitResult> AbortAsync(GitOperation operation, CancellationToken cancellationToken = default) =>
        operation switch
        {
            GitOperation.Merge => GitRunner.RunAsync(Root, ["merge", "--abort"], cancellationToken: cancellationToken),
            GitOperation.Rebase => GitRunner.RunAsync(Root, ["rebase", "--abort"], cancellationToken: cancellationToken),
            GitOperation.CherryPick => GitRunner.RunAsync(Root, ["cherry-pick", "--abort"], cancellationToken: cancellationToken),
            GitOperation.Revert => GitRunner.RunAsync(Root, ["revert", "--abort"], cancellationToken: cancellationToken),
            _ => Task.FromResult(new GitResult(1, "", "Nothing is in progress to abort.")),
        };

    /// <summary>
    /// The newest <paramref name="limit"/> commits, each with the files it changed - of the whole
    /// repository, or just of <paramref name="file"/>, followed back through renames. A merge lists
    /// what it changed relative to its first parent.
    /// </summary>
    public async Task<IReadOnlyList<GitCommit>> GetLogAsync(string? file = null, int limit = 300, CancellationToken cancellationToken = default)
    {
        string[] arguments = ["log", $"-n{limit}", "--format=%x1e%H%x1f%an%x1f%at%x1f%s", "--name-status", "-M", "--diff-merges=first-parent"];
        var result = await GitRunner.RunAsync(Root, file is null ? arguments : [.. arguments, "--follow", "--", Relative(file)],
            cancellationToken: cancellationToken);
        return result.Succeeded ? GitCommit.ParseLog(result.Output) : [];
    }

    /// <summary>What <paramref name="commit"/> changed in <paramref name="file"/>, against its
    /// (first) parent - a renamed file compared with its old path.</summary>
    public async Task<GitDiff> GetCommitDiffAsync(GitCommit commit, GitCommitFile file, CancellationToken cancellationToken = default)
    {
        string[] paths = file.OldPath is { } old ? [old, file.Path] : [file.Path];
        var result = await GitRunner.RunAsync(Root,
            ["show", "--format=", "--no-color", "--no-ext-diff", "-M", "--diff-merges=first-parent", "-U3", commit.Hash, "--", .. paths],
            cancellationToken: cancellationToken);
        return result.Succeeded ? GitDiff.Parse(result.Output) : throw new IOException(result.Message);
    }

    /// <summary>
    /// Who last changed 1-based <paramref name="line"/> of <paramref name="file"/>, blaming
    /// <paramref name="currentText"/> - the editor's text, unsaved edits included - so line numbers
    /// match what's on screen and an edited line reads as not committed. Null for a file git doesn't
    /// track, or a line that isn't there.
    /// </summary>
    public async Task<GitBlameLine?> BlameLineAsync(string file, int line, string currentText, CancellationToken cancellationToken = default)
    {
        var result = await GitRunner.RunAsync(Root, ["blame", "--porcelain", "-L", $"{line},{line}", "--contents", "-", "--", Relative(file)],
            currentText, cancellationToken);
        return result.Succeeded ? GitBlameLine.Parse(result.Output) : null;
    }

    /// <summary>
    /// Who last changed every line of <paramref name="file"/>, blaming <paramref name="currentText"/>
    /// (the editor's text, unsaved edits included) as <see cref="BlameLineAsync"/> does. Null for a
    /// file git doesn't track.
    /// </summary>
    public async Task<IReadOnlyList<GitBlameFileLine>?> BlameFileAsync(string file, string currentText, CancellationToken cancellationToken = default)
    {
        var result = await GitRunner.RunAsync(Root, ["blame", "--line-porcelain", "--contents", "-", "--", Relative(file)],
            currentText, cancellationToken);
        return result.Succeeded ? GitBlameLine.ParseFile(result.Output) : null;
    }

    /// <summary>
    /// <paramref name="file"/>'s committed contents (HEAD), or null when HEAD doesn't have it - a
    /// new file, or a repository with no commits yet. <paramref name="headPath"/> is where it was
    /// in HEAD, for a file renamed since.
    /// </summary>
    public async Task<string?> ReadHeadAsync(string file, string? headPath = null, CancellationToken cancellationToken = default)
    {
        var result = await GitRunner.RunAsync(Root, ["show", $"HEAD:{Relative(headPath ?? file)}"], cancellationToken: cancellationToken);
        return result.Succeeded ? result.Output : null;
    }

    /// <summary>
    /// What changed in <paramref name="file"/> since the last commit: HEAD's contents against
    /// <paramref name="currentText"/> - the editor's text, unsaved edits included (null for a file
    /// that's been deleted). Line endings are ignored, so a CRLF checkout of an LF commit only shows
    /// real edits. Both sides go through git's own diff (<c>--no-index</c> on two temporary files),
    /// so it reads exactly as <c>git diff</c> would. A file HEAD doesn't have reads as all added,
    /// or as no changes with <paramref name="emptyIfNotInHead"/>.
    /// </summary>
    public async Task<GitDiff> DiffWithHeadAsync(string file, string? currentText, string? headPath = null,
        int contextLines = 3, CancellationToken cancellationToken = default, bool emptyIfNotInHead = false)
    {
        var committed = await ReadHeadAsync(file, headPath, cancellationToken);
        if (committed is null && emptyIfNotInHead)
            return GitDiff.Empty;
        var head = committed ?? "";
        var folder = Directory.CreateTempSubdirectory("tedide-diff-").FullName;
        try
        {
            var name = Path.GetFileName(file);
            var oldFile = Path.Combine(folder, "HEAD-" + name);
            var newFile = Path.Combine(folder, name);
            await File.WriteAllTextAsync(oldFile, Normalize(head), cancellationToken);
            await File.WriteAllTextAsync(newFile, Normalize(currentText ?? ""), cancellationToken);
            // Exit code 1 just means "they differ".
            var result = await GitRunner.RunAsync(folder,
                ["diff", "--no-index", "--no-color", "--no-ext-diff", $"-U{contextLines}", "--", oldFile, newFile],
                cancellationToken: cancellationToken);
            return result.ExitCode is 0 or 1
                ? GitDiff.Parse(result.Output)
                : throw new IOException(result.Message);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    /// <summary>Stages each file's changes - additions and deletions included.</summary>
    public Task<GitResult> StageAsync(IEnumerable<string> files, CancellationToken cancellationToken = default) =>
        GitRunner.RunAsync(Root, ["add", "-A", "--", .. files.Select(Relative)], cancellationToken: cancellationToken);

    /// <summary>Takes each file's changes back out of the index, leaving the files themselves alone.
    /// Before the first commit there's no HEAD to restore from, so they're just removed from the index.</summary>
    public Task<GitResult> UnstageAsync(IEnumerable<string> files, bool hasCommits, CancellationToken cancellationToken = default) =>
        GitRunner.RunAsync(Root,
            hasCommits ? ["restore", "--staged", "--", .. files.Select(Relative)] : ["rm", "--cached", "-q", "-r", "--", .. files.Select(Relative)],
            cancellationToken: cancellationToken);

    /// <summary>
    /// Throws away each file's unstaged changes: a tracked file goes back to its staged (else
    /// committed) contents, and a file git doesn't track yet is deleted. Can't be undone.
    /// </summary>
    public async Task<GitResult> DiscardAsync(IEnumerable<GitFileStatus> files, CancellationToken cancellationToken = default)
    {
        var all = files.ToList();
        foreach (var untracked in all.Where(f => f.IsUntracked))
            File.Delete(untracked.Path);
        var tracked = all.Where(f => !f.IsUntracked).Select(f => Relative(f.Path)).ToList();
        return tracked.Count == 0
            ? new GitResult(0, "", "")
            : await GitRunner.RunAsync(Root, ["restore", "--", .. tracked], cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Commits what's staged, with <paramref name="message"/> (passed on standard input, so any
    /// text is safe). With <paramref name="stageAll"/>, every change - new files included - is
    /// staged first, as Visual Studio's Commit All does. With <paramref name="amend"/>, the last
    /// commit is replaced instead - by one with what's staged added and <paramref name="message"/>,
    /// or its own message when that's empty.
    /// </summary>
    public async Task<GitResult> CommitAsync(string message, bool stageAll, bool amend = false, CancellationToken cancellationToken = default)
    {
        if (stageAll)
        {
            var staged = await GitRunner.RunAsync(Root, ["add", "-A"], cancellationToken: cancellationToken);
            if (!staged.Succeeded)
                return staged;
        }
        if (amend && string.IsNullOrWhiteSpace(message))
            return await GitRunner.RunAsync(Root, ["commit", "--amend", "--no-edit"], cancellationToken: cancellationToken);
        return await GitRunner.RunAsync(Root, amend ? ["commit", "--amend", "-F", "-"] : ["commit", "-F", "-"], message, cancellationToken);
    }

    /// <summary>HEAD's full commit message - for editing it when amending. Null before the first commit.</summary>
    public async Task<string?> GetLastCommitMessageAsync(CancellationToken cancellationToken = default)
    {
        var result = await GitRunner.RunAsync(Root, ["log", "-1", "--format=%B"], cancellationToken: cancellationToken);
        return result.Succeeded ? result.Output.TrimEnd() : null;
    }

    /// <summary>
    /// Puts every uncommitted change - staged, unstaged and new files - away in a new stash, with
    /// <paramref name="message"/> if given, leaving the working tree clean. git reports "No local
    /// changes to save" (and succeeds) when there's nothing.
    /// </summary>
    public Task<GitResult> StashAsync(string? message = null, CancellationToken cancellationToken = default) =>
        GitRunner.RunAsync(Root,
            string.IsNullOrWhiteSpace(message) ? ["stash", "push", "--include-untracked"] : ["stash", "push", "--include-untracked", "-m", message.Trim()],
            cancellationToken: cancellationToken);

    /// <summary>The stashes, newest first.</summary>
    public async Task<IReadOnlyList<GitStash>> GetStashesAsync(CancellationToken cancellationToken = default)
    {
        var result = await GitRunner.RunAsync(Root, ["stash", "list", "--format=%gd%x1f%gs%x1f%ct"], cancellationToken: cancellationToken);
        return result.Succeeded ? GitStash.Parse(result.Output) : [];
    }

    /// <summary>
    /// Puts <paramref name="stash"/>'s changes back - and, with <paramref name="drop"/> (pop),
    /// removes the stash once they're in. Changes that clash with the working tree stop as
    /// conflicts, and the stash is kept.
    /// </summary>
    public Task<GitResult> UnstashAsync(GitStash stash, bool drop, CancellationToken cancellationToken = default) =>
        GitRunner.RunAsync(Root, ["stash", drop ? "pop" : "apply", stash.Name], cancellationToken: cancellationToken);

    /// <summary>Deletes <paramref name="stash"/> without applying it. Can't be undone.</summary>
    public Task<GitResult> DropStashAsync(GitStash stash, CancellationToken cancellationToken = default) =>
        GitRunner.RunAsync(Root, ["stash", "drop", stash.Name], cancellationToken: cancellationToken);

    /// <summary>
    /// The local branches, then the remote ones nothing local tracks yet - each remote's
    /// "origin/HEAD" pointer left out, since it isn't a branch of its own.
    /// </summary>
    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default)
    {
        var result = await GitRunner.RunAsync(Root,
            ["for-each-ref", "--format=%(refname)%09%(refname:short)%09%(upstream:short)%09%(HEAD)%09%(symref)", "refs/heads", "refs/remotes"],
            cancellationToken: cancellationToken);
        return result.Succeeded ? GitBranch.Parse(result.Output) : [];
    }

    /// <summary>
    /// Switches to <paramref name="branch"/>. A remote branch is checked out as a new local branch
    /// of the same name that tracks it ("origin/topic" becomes "topic"). Uncommitted changes come
    /// along when they don't clash with the branch; when they do, git refuses and changes nothing.
    /// </summary>
    public Task<GitResult> SwitchAsync(GitBranch branch, CancellationToken cancellationToken = default) =>
        GitRunner.RunAsync(Root, branch.IsRemote ? ["switch", "--track", branch.Name] : ["switch", branch.Name],
            cancellationToken: cancellationToken);

    /// <summary>Creates <paramref name="name"/> at the current commit and switches to it,
    /// uncommitted changes and all. git rejects a name that isn't valid or already exists.</summary>
    public Task<GitResult> CreateBranchAsync(string name, CancellationToken cancellationToken = default) =>
        GitRunner.RunAsync(Root, ["switch", "-c", name], cancellationToken: cancellationToken);

    /// <summary>Deletes a local branch - only one already merged (<c>git branch -d</c>), so no
    /// commits can be lost. Never the current branch.</summary>
    public Task<GitResult> DeleteBranchAsync(string name, CancellationToken cancellationToken = default) =>
        GitRunner.RunAsync(Root, ["branch", "-d", name], cancellationToken: cancellationToken);

    /// <summary>The repository's remotes, by name ("origin").</summary>
    public async Task<IReadOnlyList<string>> GetRemotesAsync(CancellationToken cancellationToken = default)
    {
        var result = await GitRunner.RunAsync(Root, ["remote"], cancellationToken: cancellationToken);
        return result.Succeeded
            ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    /// <summary>Downloads what's new on the current branch's remote (origin if it has none), and
    /// forgets remote branches deleted there. Changes nothing in the working tree.</summary>
    public Task<GitResult> FetchAsync(CancellationToken cancellationToken = default) =>
        GitRunner.RunAsync(Root, ["fetch", "--prune"], cancellationToken: cancellationToken);

    /// <summary>
    /// Fetches and integrates the current branch's upstream. The user's own pull.rebase/pull.ff
    /// settings decide how; with neither set, divergent branches are merged, as Visual Studio does
    /// (git otherwise refuses and asks which). A merge commit takes git's default message rather
    /// than opening an editor, which nobody would see.
    /// </summary>
    public async Task<GitResult> PullAsync(CancellationToken cancellationToken = default)
    {
        var configured = await GitRunner.RunAsync(Root, ["config", "--get-regexp", @"^pull\.(rebase|ff)$"], cancellationToken: cancellationToken);
        var hasPreference = configured.Succeeded && configured.Output.Trim().Length > 0;
        return await GitRunner.RunAsync(Root, hasPreference ? ["pull", "--no-edit"] : ["pull", "--no-edit", "--no-rebase"],
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Pushes the current branch to its upstream - or, with <paramref name="publishTo"/>, to that
    /// remote under the same name, making it the upstream (a branch's first push). Never forces.
    /// </summary>
    public Task<GitResult> PushAsync(string? publishTo = null, string? branch = null, CancellationToken cancellationToken = default) =>
        GitRunner.RunAsync(Root,
            publishTo is null ? ["push"] : ["push", "--set-upstream", publishTo, branch ?? "HEAD"],
            cancellationToken: cancellationToken);

    /// <summary>The short hash and subject of HEAD - "a1b2c3d Add tabs" - for reporting a commit.</summary>
    public async Task<string?> DescribeHeadAsync(CancellationToken cancellationToken = default)
    {
        var result = await GitRunner.RunAsync(Root, ["log", "-1", "--format=%h %s"], cancellationToken: cancellationToken);
        return result.Succeeded ? result.Output.Trim() : null;
    }

    /// <summary><paramref name="path"/> relative to <see cref="Root"/>, with "/" as git prefers.</summary>
    public string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    /// <summary>Whether <paramref name="path"/> is inside this working tree.</summary>
    public bool Contains(string path)
    {
        var relative = Path.GetRelativePath(Root, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
