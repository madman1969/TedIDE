namespace Tedide.Git;

/// <summary>
/// A branch: a local one ("main"), or a remote one nothing local tracks yet ("origin/topic").
/// <see cref="Upstream"/> is the remote branch a local one tracks, if any.
/// </summary>
public sealed record GitBranch(string Name, bool IsRemote, bool IsCurrent, string? Upstream)
{
    /// <summary>The local branch a remote one is checked out as - "origin/topic" gives "topic".</summary>
    public string LocalName => IsRemote && Name.IndexOf('/') is var slash and >= 0 ? Name[(slash + 1)..] : Name;

    /// <summary>
    /// Parses <c>git for-each-ref</c> lines of refname, short name, upstream, HEAD marker ("*") and
    /// symref, tab-separated. Local branches come first, in git's order (by name); then remote
    /// branches no local branch tracks or shares a name with; symbolic refs (origin/HEAD) are left out.
    /// </summary>
    public static IReadOnlyList<GitBranch> Parse(string output)
    {
        var local = new List<GitBranch>();
        var remote = new List<GitBranch>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 5 || fields[4].Length > 0)
                continue;
            var (refName, name, upstream, head) = (fields[0], fields[1], fields[2], fields[3]);
            if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
                local.Add(new GitBranch(name, IsRemote: false, IsCurrent: head == "*", upstream.Length > 0 ? upstream : null));
            else if (refName.StartsWith("refs/remotes/", StringComparison.Ordinal))
                remote.Add(new GitBranch(name, IsRemote: true, IsCurrent: false, Upstream: null));
        }
        remote.RemoveAll(r => local.Any(l => l.Upstream == r.Name || l.Name == r.LocalName));
        return [.. local, .. remote];
    }
}
