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
public sealed record BuildDiagnostic(
    string FilePath,
    int Line,
    DiagnosticSeverity Severity,
    string Message)
{
    public override string ToString() => $"{FilePath}({Line}): {Severity}: {Message}";
}
