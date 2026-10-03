namespace Tedide.Git;

/// <summary>Who last changed one line, from <c>git blame --porcelain</c>. <see cref="IsCommitted"/>
/// is false for a line changed since the last commit, which git reports against an all-zero hash.</summary>
public sealed record GitBlameLine(string Commit, string Author, DateTimeOffset When, string Summary)
{
    public bool IsCommitted => Commit.Any(c => c != '0');

    /// <summary>"aross, 3 days ago: Add tabbed editing" - or "Not committed yet" for a changed line.</summary>
    public string Describe(DateTimeOffset now) =>
        IsCommitted ? $"{Author}, {Ago(now - When)}: {Summary}" : "Not committed yet";

    internal static string Ago(TimeSpan age) => age.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => Plural((int)age.TotalMinutes, "minute"),
        < 60 * 24 => Plural((int)age.TotalHours, "hour"),
        < 60 * 24 * 30 => Plural((int)age.TotalDays, "day"),
        < 60 * 24 * 365 => Plural((int)(age.TotalDays / 30), "month"),
        _ => Plural((int)(age.TotalDays / 365), "year"),
    };

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";

    /// <summary>Parses the porcelain blame of a single line (<c>-L n,n</c>); null if there's none.</summary>
    public static GitBlameLine? Parse(string output)
    {
        var lines = output.Split('\n');
        if (lines.Length == 0 || lines[0].Split(' ') is not [{ Length: 40 } commit, ..])
            return null;

        string author = "", summary = "";
        long time = 0;
        var offset = TimeSpan.Zero;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith('\t'))
                break; // the line's own text ends the header
            var space = line.IndexOf(' ');
            var (key, value) = space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
            switch (key)
            {
                case "author": author = value; break;
                case "author-time": long.TryParse(value, out time); break;
                case "author-tz": offset = ParseZone(value); break;
                case "summary": summary = value; break;
            }
        }
        return new GitBlameLine(commit, author, DateTimeOffset.FromUnixTimeSeconds(time).ToOffset(offset), summary);
    }

    /// <summary>"+0100" - git's time zone.</summary>
    private static TimeSpan ParseZone(string zone) =>
        zone.Length == 5 && int.TryParse(zone[1..3], out var hours) && int.TryParse(zone[3..], out var minutes)
            ? new TimeSpan(hours, minutes, 0) * (zone[0] == '-' ? -1 : 1)
            : TimeSpan.Zero;
}
