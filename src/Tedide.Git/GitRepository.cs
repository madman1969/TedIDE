namespace Tedide.Git;

/// <summary>
/// A git working tree, by its root folder, and the handful of operations Tedide offers on it:
/// status, blame for one line, a file's diff against HEAD, stage, unstage, discard and commit. Every method runs git in the
/// background and returns its result; nothing here touches the UI.
/// </summary>
public sealed class GitRepository
{
    public string Root { get; }

    private GitRepository(string root) => Root = root;

    /// <summary>The repository <paramref name="directory"/> is in, or null if it isn't in one (or
    /// git isn't installed - the IDE then simply shows no git information).</summary>
    public static async Task<GitRepository?> FindAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
            return null;
        var result = await GitRunner.RunAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken: cancellationToken);
        return result.Succeeded && result.Output.Trim() is { Length: > 0 } root
            ? new GitRepository(Path.GetFullPath(root))
            : null;
    }

    public async Task<GitStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await GitRunner.RunAsync(Root, ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"],
            cancellationToken: cancellationToken);
        return result.Succeeded ? GitStatus.Parse(result.Output, Root) : null;
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
    /// so it reads exactly as <c>git diff</c> would.
    /// </summary>
    public async Task<GitDiff> DiffWithHeadAsync(string file, string? currentText, string? headPath = null,
        int contextLines = 3, CancellationToken cancellationToken = default)
    {
        var head = await ReadHeadAsync(file, headPath, cancellationToken) ?? "";
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

    /// <summary>Commits what's staged, with <paramref name="message"/> (passed on standard input, so
    /// any text is safe). With <paramref name="stageAll"/>, every change - new files included - is
    /// staged first, as Visual Studio's Commit All does.</summary>
    public async Task<GitResult> CommitAsync(string message, bool stageAll, CancellationToken cancellationToken = default)
    {
        if (stageAll)
        {
            var staged = await GitRunner.RunAsync(Root, ["add", "-A"], cancellationToken: cancellationToken);
            if (!staged.Succeeded)
                return staged;
        }
        return await GitRunner.RunAsync(Root, ["commit", "-F", "-"], message, cancellationToken);
    }

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
