using System.Text.RegularExpressions;
using Tedide.Core;

namespace Tedide.Build;

/// <summary>
/// Parses stdout/stderr lines produced by cl65, cc65, ca65 and ld65 into structured diagnostics.
/// Recognized shapes (all confirmed against real cl65 2.19 output):
///   file.c(12): Error: message
///   file.s:12: Warning: message
///   C:\libs\inc\bad.h:1: Error: message       - an absolute path, e.g. a header found via an
///                                               absolute include path
///   ld65: Error: C:\cc65\cfg/c64.cfg:15: msg   - a tool's own message, about a file...
///   ld65: Error: 1 unresolved external(s) ...  - ...or about nothing in particular (no file/line)
/// Anything else is not a diagnostic (build banners, progress, etc). The last three used to be
/// missed - a link that failed on a memory-area overflow reported "Build FAILED (0 error(s))"
/// with an empty Error List.
/// </summary>
public static partial class Cc65DiagnosticParser
{
    // "(?:[A-Za-z]:)?" lets a drive letter's colon through; the rest of a path can't contain one.
    [GeneratedRegex(@"^(?<file>(?:[A-Za-z]:)?[^():]+)[(:](?<line>\d+)\)?:\s*(?<severity>Error|Warning|Note)\s*:\s*(?<message>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticRegex();

    [GeneratedRegex(@"^(?<tool>cl65|cc65|ca65|ld65|co65|ar65|sp65):\s*(?<severity>Error|Warning|Note)\s*:\s*(?:(?<file>(?:[A-Za-z]:)?[^():]+):(?<line>\d+):\s*)?(?<message>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ToolMessageRegex();

    public static BuildDiagnostic? TryParse(string line)
    {
        line = line.Trim();
        var match = DiagnosticRegex().Match(line);
        if (!match.Success)
            match = ToolMessageRegex().Match(line);
        if (!match.Success)
            return null;

        var severity = match.Groups["severity"].Value.ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Info,
        };

        // A tool message about nothing in particular has no file/line: an empty path and line 0,
        // which the Error List shows as-is and simply doesn't navigate anywhere on activation.
        var lineGroup = match.Groups["line"];
        return new BuildDiagnostic(
            FilePath: match.Groups["file"].Value.Trim(),
            Line: lineGroup.Success ? int.Parse(lineGroup.Value) : 0,
            Severity: severity,
            Message: match.Groups["message"].Value.Trim());
    }

    public static IReadOnlyList<BuildDiagnostic> ParseAll(IEnumerable<string> lines) =>
        lines.Select(TryParse).Where(d => d is not null).Select(d => d!).ToList();
}
