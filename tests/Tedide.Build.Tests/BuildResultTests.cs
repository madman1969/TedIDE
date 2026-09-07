using Tedide.Core;

namespace Tedide.Build.Tests;

public class BuildResultTests
{
    private static BuildDiagnostic Diagnostic(DiagnosticSeverity severity) =>
        new("src/main.c", 1, severity, "message");

    [Fact]
    public void Errors_ReturnsOnlyErrorSeverityDiagnostics()
    {
        var diagnostics = new[]
        {
            Diagnostic(DiagnosticSeverity.Error),
            Diagnostic(DiagnosticSeverity.Warning),
        };
        var result = new BuildResult(false, 1, [], diagnostics, TimeSpan.Zero);

        Assert.Single(result.Errors);
        Assert.All(result.Errors, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void Warnings_ReturnsOnlyWarningSeverityDiagnostics()
    {
        var diagnostics = new[]
        {
            Diagnostic(DiagnosticSeverity.Error),
            Diagnostic(DiagnosticSeverity.Warning),
            Diagnostic(DiagnosticSeverity.Warning),
        };
        var result = new BuildResult(false, 1, [], diagnostics, TimeSpan.Zero);

        Assert.Equal(2, result.Warnings.Count());
        Assert.All(result.Warnings, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
    }

    [Fact]
    public void Errors_AndWarnings_AreBothEmpty_WhenThereAreNoDiagnostics()
    {
        var result = new BuildResult(true, 0, [], [], TimeSpan.Zero);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ExposesEveryConstructorValue_ThroughItsOwnProperty()
    {
        var rawOutputLines = new[] { "line 1", "line 2" };
        var diagnostics = new[] { Diagnostic(DiagnosticSeverity.Error) };
        var duration = TimeSpan.FromSeconds(3);

        var result = new BuildResult(true, 42, rawOutputLines, diagnostics, duration);

        Assert.True(result.Succeeded);
        Assert.Equal(42, result.ExitCode);
        Assert.Same(rawOutputLines, result.RawOutputLines);
        Assert.Same(diagnostics, result.Diagnostics);
        Assert.Equal(duration, result.Duration);
    }
}
