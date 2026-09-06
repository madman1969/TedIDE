using System.Text.RegularExpressions;
using Tedide.Core;

namespace Tedide.Build;

/// <summary>
/// Parses stdout/stderr lines produced by cl65, cc65, ca65 and ld65 into structured diagnostics.
/// Recognized shapes:
///   file.c(12): Error: message
///   file.c(12): Warning: message
///   file.s:12: Error: message
/// Anything else is not a diagnostic (build banners, progress, etc).
/// </summary>
public static partial class Cc65DiagnosticParser
{
    [GeneratedRegex(@"^(?<file>[^():]+)[(:](?<line>\d+)\)?:\s*(?<severity>Error|Warning|Note)\s*:\s*(?<message>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticRegex();

    public static BuildDiagnostic? TryParse(string line)
    {
        var match = DiagnosticRegex().Match(line.Trim());
        if (!match.Success)
            return null;

        var severity = match.Groups["severity"].Value.ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Info,
        };

        return new BuildDiagnostic(
            FilePath: match.Groups["file"].Value.Trim(),
            Line: int.Parse(match.Groups["line"].Value),
            Severity: severity,
            Message: match.Groups["message"].Value.Trim());
    }

    public static IReadOnlyList<BuildDiagnostic> ParseAll(IEnumerable<string> lines) =>
        lines.Select(TryParse).Where(d => d is not null).Select(d => d!).ToList();
}
