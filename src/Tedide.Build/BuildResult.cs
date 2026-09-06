using Tedide.Core;

namespace Tedide.Build;

public sealed record BuildResult(
    bool Succeeded,
    int ExitCode,
    IReadOnlyList<string> RawOutputLines,
    IReadOnlyList<BuildDiagnostic> Diagnostics,
    TimeSpan Duration)
{
    public IEnumerable<BuildDiagnostic> Errors => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    public IEnumerable<BuildDiagnostic> Warnings => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning);
}
