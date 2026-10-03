using System.Diagnostics;

namespace Tedide.Core;

/// <summary>
/// Help > Context Help (F1): the word at the caret, and how to open Tedide.DocViewer at it. The
/// Doc Viewer is a separate program (it ships its own Terminal.Gui version and Docs.db, so it can't
/// share Tedide's folder or process); it takes the word as <c>--topic word</c>.
/// </summary>
public static class ContextHelp
{
    public const string DocViewerFileName = "Tedide.DocViewer.exe";

    /// <summary>Overrides where the Doc Viewer is looked for - a full path to its exe.</summary>
    public const string DocViewerPathVariable = "TEDIDE_DOCVIEWER";

    /// <summary>
    /// The identifier at (or just before) 1-based <paramref name="column"/> in <paramref name="line"/>:
    /// letters, digits and underscores, plus a leading "." for a ca65 directive (".byte", ".proc").
    /// Null if the caret isn't touching one.
    /// </summary>
    public static string? WordAt(string line, int column)
    {
        var index = Math.Clamp(column - 1, 0, line.Length);
        // On the character after a word (the caret just past its end) still counts.
        if ((index == line.Length || !IsWordChar(line[index])) && index > 0 && IsWordChar(line[index - 1]))
            index--;
        if (index >= line.Length || !IsWordChar(line[index]))
            return null;

        var start = index;
        while (start > 0 && IsWordChar(line[start - 1]))
            start--;
        var end = index;
        while (end < line.Length && IsWordChar(line[end]))
            end++;
        // ".byte" - but not "s.field", where the dot is C member access.
        if (start > 0 && line[start - 1] == '.' && (start == 1 || char.IsWhiteSpace(line[start - 2])))
            start--;
        return line[start..end];
    }

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Where to look for the Doc Viewer, most specific first: the <see cref="DocViewerPathVariable"/>
    /// override; beside Tedide itself; the repository's sibling publish-docviewer folder (beside
    /// publish/); and, from a development build, the Doc Viewer project's own Debug/Release output.
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths(string baseDirectory, string? overridePath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(overridePath))
            candidates.Add(overridePath.Trim().Trim('"'));

        var directory = Path.GetFullPath(baseDirectory);
        candidates.Add(Path.Combine(directory, DocViewerFileName));
        candidates.Add(Path.GetFullPath(Path.Combine(directory, "..", "publish-docviewer", DocViewerFileName)));

        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
        {
            var project = Path.Combine(dir.FullName, "src", "Tedide.DocViewer");
            if (!File.Exists(Path.Combine(project, "Tedide.DocViewer.csproj")))
                continue;
            candidates.Add(Path.Combine(project, "bin", "Debug", "net10.0", DocViewerFileName));
            candidates.Add(Path.Combine(project, "bin", "Release", "net10.0", DocViewerFileName));
            candidates.Add(Path.Combine(dir.FullName, "publish-docviewer", DocViewerFileName));
            break;
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The first candidate that exists (see <see cref="CandidatePaths"/>), or null.</summary>
    public static string? FindDocViewer(IEnumerable<string> candidates) => candidates.FirstOrDefault(File.Exists);

    /// <summary>
    /// How to start the Doc Viewer at <paramref name="topic"/> (or at its usual first page if null).
    /// It's a full-screen terminal app, so it needs a terminal of its own: inside Windows Terminal,
    /// a new tab in Tedide's own window; anywhere else, a new console window.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string docViewerPath, string? topic, bool insideWindowsTerminal)
    {
        var info = insideWindowsTerminal
            ? new ProcessStartInfo("wt.exe") { UseShellExecute = false }
            : new ProcessStartInfo(docViewerPath) { UseShellExecute = true };
        info.WorkingDirectory = Path.GetDirectoryName(docViewerPath) ?? string.Empty;

        if (insideWindowsTerminal)
        {
            // -w 0: the current window. wt reads ";" as its own command separator, so a topic can't
            // carry one - WordAt never returns one anyway.
            foreach (var argument in new[] { "-w", "0", "new-tab", "--title", "Tedide Docs", "-d", info.WorkingDirectory, docViewerPath })
                info.ArgumentList.Add(argument);
        }
        if (!string.IsNullOrEmpty(topic))
        {
            info.ArgumentList.Add("--topic");
            info.ArgumentList.Add(topic);
        }
        return info;
    }
}
