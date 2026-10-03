namespace Tedide.Git;

/// <summary>One file a commit changed: git's status letter (A added, M modified, D deleted, R
/// renamed, C copied, T type changed), its path relative to the repository root ("/"-separated),
/// and where it was renamed or copied from.</summary>
public sealed record GitCommitFile(char Status, string Path, string? OldPath = null);

/// <summary>A commit, from <c>git log</c>: its id, author, date and subject, and the files it changed.</summary>
public sealed record GitCommit(string Hash, string Author, DateTimeOffset When, string Subject, IReadOnlyList<GitCommitFile> Files)
{
    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;

    /// <summary>
    /// Parses <c>git log --format=%x1e%H%x1f%an%x1f%at%x1f%s --name-status</c>: each commit starts
    /// at a record separator, its header fields split by unit separators, then one tab-separated
    /// name-status line per file ("M\tpath", "R100\told\tnew").
    /// </summary>
    public static IReadOnlyList<GitCommit> ParseLog(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var record in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Split('\n');
            var header = lines[0].TrimEnd('\r').Split('\x1f');
            if (header.Length < 4 || header[0].Length < 7)
                continue;
            long.TryParse(header[2], out var time);
            var files = new List<GitCommitFile>();
            foreach (var line in lines.Skip(1))
            {
                var fields = line.TrimEnd('\r').Split('\t');
                if (fields.Length < 2 || fields[0].Length == 0)
                    continue;
                files.Add(fields.Length >= 3 && fields[0][0] is 'R' or 'C'
                    ? new GitCommitFile(fields[0][0], fields[2], fields[1])
                    : new GitCommitFile(fields[0][0], fields[1]));
            }
            commits.Add(new GitCommit(header[0], header[1], DateTimeOffset.FromUnixTimeSeconds(time), header[3], files));
        }
        return commits;
    }
}
