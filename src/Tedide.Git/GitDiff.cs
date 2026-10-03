using System.Text.RegularExpressions;

namespace Tedide.Git;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

/// <summary>One line of a diff, with its 1-based number on each side it's on: a removed line has
/// only <see cref="OldLine"/>, an added one only <see cref="NewLine"/>, a context line both.</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLine, int? NewLine);

/// <summary>One "@@ -12,7 +12,9 @@ void main(void)" block: where it starts on each side, how many
/// lines it covers there, the function git names after the second "@@" (empty if none), and its lines.</summary>
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, string Section, IReadOnlyList<DiffLine> Lines)
{
    /// <summary>The hunk's header as git writes it.</summary>
    public string Header =>
        $"@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@{(Section.Length > 0 ? " " + Section : "")}";
}

/// <summary>How a line of the current file differs from the last commit, for the editor's change
/// markers: new, changed, or the place where lines were removed - just above it, or below it at
/// the end of the file.</summary>
public enum LineChangeKind
{
    Added,
    Modified,
    RemovedAbove,
    RemovedBelow,
}

/// <summary>A file's changes, parsed from git's unified diff. No hunks means no changes.</summary>
public sealed partial record GitDiff(IReadOnlyList<DiffHunk> Hunks, bool IsBinary = false)
{
    public static GitDiff Empty { get; } = new([]);

    public int Added => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));

    public int Removed => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Removed));

    /// <summary>
    /// Each changed line of the current file, by 1-based number, as VS Code's gutter shows them: a
    /// hunk that only adds lines is Added, one that replaces lines is Modified throughout, and one
    /// that only removes lines marks the line now after the gap (or, at the end of the file, the
    /// last line). <paramref name="lineCount"/> is the current file's line count.
    /// </summary>
    public IReadOnlyDictionary<int, LineChangeKind> LineChanges(int lineCount)
    {
        var changes = new Dictionary<int, LineChangeKind>();
        foreach (var hunk in Hunks)
        {
            if (hunk.NewCount == 0)
            {
                // "+19,0": removed after line 19 (0 for the top of the file).
                if (hunk.NewStart + 1 <= lineCount)
                    changes.TryAdd(hunk.NewStart + 1, LineChangeKind.RemovedAbove);
                else if (lineCount > 0)
                    changes.TryAdd(Math.Clamp(hunk.NewStart, 1, lineCount), LineChangeKind.RemovedBelow);
                continue;
            }
            var kind = hunk.OldCount == 0 ? LineChangeKind.Added : LineChangeKind.Modified;
            for (var line = hunk.NewStart; line < hunk.NewStart + hunk.NewCount; line++)
                changes[line] = kind;
        }
        return changes;
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$")]
    private static partial Regex HunkHeader();

    /// <summary>
    /// Parses one file's unified diff (<c>git diff</c> output). Everything before the first hunk -
    /// the "diff --git", index and ---/+++ lines - is skipped, as is "\ No newline at end of file".
    /// </summary>
    public static GitDiff Parse(string output)
    {
        var hunks = new List<DiffHunk>();
        var isBinary = false;
        List<DiffLine>? lines = null;
        int oldStart = 0, oldCount = 0, newStart = 0, newCount = 0, oldLine = 0, newLine = 0;
        var section = "";

        void Finish()
        {
            if (lines is not null)
                hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount, section, lines));
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (HunkHeader().Match(line) is { Success: true } header)
            {
                Finish();
                oldStart = int.Parse(header.Groups[1].Value);
                oldCount = header.Groups[2].Success ? int.Parse(header.Groups[2].Value) : 1;
                newStart = int.Parse(header.Groups[3].Value);
                newCount = header.Groups[4].Success ? int.Parse(header.Groups[4].Value) : 1;
                section = header.Groups[5].Value.Trim();
                oldLine = oldStart;
                newLine = newStart;
                lines = [];
                continue;
            }
            if (lines is null)
            {
                if (line.StartsWith("Binary files ", StringComparison.Ordinal))
                    isBinary = true;
                continue;
            }
            if (line.Length == 0)
            {
                // The trailing newline of the output - a context line always has at least its " ".
                continue;
            }
            switch (line[0])
            {
                case ' ':
                    lines.Add(new DiffLine(DiffLineKind.Context, line[1..], oldLine++, newLine++));
                    break;
                case '-':
                    lines.Add(new DiffLine(DiffLineKind.Removed, line[1..], oldLine++, null));
                    break;
                case '+':
                    lines.Add(new DiffLine(DiffLineKind.Added, line[1..], null, newLine++));
                    break;
                // '\' - "\ No newline at end of file"; anything else ends the file's diff.
            }
        }
        Finish();
        return new GitDiff(hunks, isBinary);
    }
}
