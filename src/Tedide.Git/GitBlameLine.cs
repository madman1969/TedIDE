namespace Tedide.Git;

/// <summary>Who last changed one line, from <c>git blame --porcelain</c>. <see cref="IsCommitted"/>
/// is false for a line changed since the last commit, which git reports against an all-zero hash.</summary>
public sealed record GitBlameLine(string Commit, string Author, DateTimeOffset When, string Summary)
{
    public bool IsCommitted => Commit.Any(c => c != '0');

    /// <summary>"aross, 3 days ago: Add tabbed editing" - or "Not committed yet" for a changed line.</summary>
    public string Describe(DateTimeOffset now) =>
        IsCommitted ? $"{Author}, {Ago(now - When)}: {Summary}" : "Not committed yet";

    /// <summary>"3 days ago" - the largest whole unit, rounded down.</summary>
    public static string Ago(TimeSpan age) => age.TotalMinutes switch
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
    public static GitBlameLine? Parse(string output) => Parse(output.Split('\n'), 0, out _, out _);

    /// <summary>
    /// Parses a whole file's blame from <c>git blame --line-porcelain</c>, which repeats each
    /// commit's details on every line: who last changed each line, its 1-based number and its text.
    /// </summary>
    public static IReadOnlyList<GitBlameFileLine> ParseFile(string output)
    {
        var lines = output.Split('\n');
        var result = new List<GitBlameFileLine>();
        var start = 0;
        while (start < lines.Length)
        {
            if (Parse(lines, start, out var end, out var lineNumber) is not { } blame)
                break;
            var text = end < lines.Length ? lines[end][1..].TrimEnd('\r') : "";
            result.Add(new GitBlameFileLine(lineNumber, text, blame));
            start = end + 1;
        }
        return result;
    }

    /// <summary>One line's record, from its "hash orig final" header at <paramref name="start"/> to
    /// the tab-prefixed text line, whose index is <paramref name="textLine"/>.</summary>
    private static GitBlameLine? Parse(string[] lines, int start, out int textLine, out int lineNumber)
    {
        textLine = lines.Length;
        lineNumber = 0;
        if (start >= lines.Length || lines[start].Split(' ') is not [{ Length: 40 } commit, _, var final, ..])
            return null;
        int.TryParse(final, out lineNumber);

        string author = "", summary = "";
        long time = 0;
        var offset = TimeSpan.Zero;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith('\t'))
            {
                textLine = i; // the line's own text ends the header
                break;
            }
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

/// <summary>One line of a whole file's blame: its 1-based number, its text and who last changed it.</summary>
public sealed record GitBlameFileLine(int Line, string Text, GitBlameLine Blame);
