namespace Tedide.Core;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A single error/warning line parsed from cc65 toolchain output (cl65, cc65, ca65, ld65).
/// </summary>
/// <param name="Project">The project being built when it was reported - set by the IDE, which
/// also makes <paramref name="FilePath"/> absolute, since a solution build's diagnostics come from
/// several project folders.</param>
public sealed record BuildDiagnostic(
    string FilePath,
    int Line,
    DiagnosticSeverity Severity,
    string Message,
    string? Project = null)
{
    public override string ToString() => $"{FilePath}({Line}): {Severity}: {Message}";
}
