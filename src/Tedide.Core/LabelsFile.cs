using System.Text.RegularExpressions;

namespace Tedide.Core;

/// <summary>One symbol from a VICE-format label file (see <see cref="TedideProject.ResolvedLabelsFile"/>).</summary>
public sealed record LabelEntry(string Name, long Address, int LabelFileLineNumber);

/// <summary>
/// Parses a VICE-format label file (.lbl, ld65's -Ln output - see
/// <see cref="TedideProject.ResolvedLabelsFile"/>) into its symbol table. Every line is a VICE
/// monitor "add label" command, "al &lt;6-hex-digit-addr&gt; .&lt;symbol&gt;" (see
/// Tedide.App.Highlighting.Cc65LabelsHighlighting's own doc comment for how this was confirmed -
/// no other command letter has ever been observed in ld65's own output), so this is a simple flat
/// line format, not a real grammar. Feeds Tedide's symbol panel the same way
/// <see cref="LinkerMapFile"/> does for lnk.map.
/// </summary>
public static class LabelsFile
{
    private static readonly Regex LabelLineRegex = new(@"^al ([0-9A-Fa-f]{6}) \.(.+)$", RegexOptions.Compiled);

    public static IReadOnlyList<LabelEntry> Parse(string text)
    {
        var labels = new List<LabelEntry>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = LabelLineRegex.Match(lines[i].TrimEnd('\r'));
            if (match.Success)
                labels.Add(new LabelEntry(match.Groups[2].Value, Convert.ToInt64(match.Groups[1].Value, 16), i + 1));
        }
        return labels;
    }
}
