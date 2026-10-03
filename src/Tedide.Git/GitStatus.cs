namespace Tedide.Git;

/// <summary>One changed file, from <c>git status</c>. <see cref="Index"/> and <see cref="WorkTree"/>
/// are git's own status letters for the staged and unstaged sides ('.' for unchanged): M modified,
/// A added, D deleted, R renamed, C copied, T type changed.</summary>
public sealed record GitFileStatus(string Path, char Index, char WorkTree, string? OriginalPath = null,
    bool IsUntracked = false, bool IsConflicted = false)
{
    /// <summary>Whether it has anything staged to commit.</summary>
    public bool IsStaged => !IsUntracked && !IsConflicted && Index != '.';

    /// <summary>Whether it has changes that aren't staged yet (new files included).</summary>
    public bool HasUnstagedChanges => IsUntracked || IsConflicted || WorkTree != '.';

    /// <summary>The single letter Tedide shows for it: ! conflicted, ? untracked, else the unstaged
    /// side's letter, else the staged side's (A for a file added and not changed since).</summary>
    public char Marker => IsConflicted ? '!' : IsUntracked ? '?' : WorkTree != '.' ? WorkTree : Index;
}

/// <summary>The branch, and every changed file, from <c>git status --porcelain=v2 --branch -z</c>.</summary>
public sealed record GitStatus(string? Branch, string? Upstream, int Ahead, int Behind, bool HasCommits, IReadOnlyList<GitFileStatus> Files)
{
    /// <summary>"main ↑2 ↓1" - the branch, and how far it is ahead of and behind its upstream. A
    /// detached HEAD shows as "(detached)".</summary>
    public string Describe()
    {
        var text = Branch ?? "(detached)";
        if (Ahead > 0)
            text += $" ↑{Ahead}";
        if (Behind > 0)
            text += $" ↓{Behind}";
        return text;
    }

    /// <summary>
    /// Parses porcelain v2 output (NUL-separated, as <c>-z</c> gives it). Paths are made absolute
    /// against <paramref name="repositoryRoot"/>. Ignored files ("!" records) are left out.
    /// </summary>
    public static GitStatus Parse(string output, string repositoryRoot)
    {
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var hasCommits = true;
        var files = new List<GitFileStatus>();

        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 2)
                continue;
            switch (record[0])
            {
                case '#':
                    var header = record[2..];
                    if (header.StartsWith("branch.oid ", StringComparison.Ordinal))
                        hasCommits = header["branch.oid ".Length..] != "(initial)";
                    else if (header.StartsWith("branch.head ", StringComparison.Ordinal))
                        branch = header["branch.head ".Length..] is var head && head != "(detached)" ? head : null;
                    else if (header.StartsWith("branch.upstream ", StringComparison.Ordinal))
                        upstream = header["branch.upstream ".Length..];
                    else if (header.StartsWith("branch.ab ", StringComparison.Ordinal))
                    {
                        var parts = header["branch.ab ".Length..].Split(' ');
                        ahead = int.Parse(parts[0].TrimStart('+'));
                        behind = int.Parse(parts[1].TrimStart('-'));
                    }
                    break;
                case '1':
                    // 1 XY sub mH mI mW hH hI path
                    var ordinary = record.Split(' ', 9);
                    files.Add(new GitFileStatus(FullPath(repositoryRoot, ordinary[8]), ordinary[1][0], ordinary[1][1]));
                    break;
                case '2':
                    // 2 XY sub mH mI mW hH hI Xscore path, then the original path as its own record
                    var renamed = record.Split(' ', 10);
                    var original = i + 1 < records.Length ? records[++i] : null;
                    files.Add(new GitFileStatus(FullPath(repositoryRoot, renamed[9]), renamed[1][0], renamed[1][1],
                        original is null ? null : FullPath(repositoryRoot, original)));
                    break;
                case 'u':
                    // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    var unmerged = record.Split(' ', 11);
                    files.Add(new GitFileStatus(FullPath(repositoryRoot, unmerged[10]), unmerged[1][0], unmerged[1][1], IsConflicted: true));
                    break;
                case '?':
                    files.Add(new GitFileStatus(FullPath(repositoryRoot, record[2..]), '.', '.', IsUntracked: true));
                    break;
            }
        }
        return new GitStatus(branch, upstream, ahead, behind, hasCommits, files);
    }

    private static string FullPath(string root, string relative) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
}
