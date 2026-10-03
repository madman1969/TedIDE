namespace Tedide.Git;

/// <summary>A stash: its name ("stash@{0}", the newest), git's description of it ("On main: tidy
/// up" for one given a message, "WIP on main: a1b2c3d Subject" otherwise), and when it was made.</summary>
public sealed record GitStash(string Name, string Description, DateTimeOffset When)
{
    /// <summary>Parses <c>git stash list --format=%gd%x1f%gs%x1f%ct</c>.</summary>
    public static IReadOnlyList<GitStash> Parse(string output)
    {
        var stashes = new List<GitStash>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\x1f');
            if (fields.Length < 3 || !fields[0].StartsWith("stash@{", StringComparison.Ordinal))
                continue;
            long.TryParse(fields[2], out var time);
            stashes.Add(new GitStash(fields[0], fields[1], DateTimeOffset.FromUnixTimeSeconds(time)));
        }
        return stashes;
    }
}
