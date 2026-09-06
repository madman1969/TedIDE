using Tedide.Build;
using Tedide.Core;

namespace Tedide.Build.Tests;

public class Cc65DiagnosticParserTests
{
    [Theory]
    [InlineData("main.c(12): Error: ';' expected", "main.c", 12, DiagnosticSeverity.Error, "';' expected")]
    [InlineData("main.c(3): Warning: Unknown pragma ignored", "main.c", 3, DiagnosticSeverity.Warning, "Unknown pragma ignored")]
    [InlineData("sprites.s:45: Error: Unexpected end of line", "sprites.s", 45, DiagnosticSeverity.Error, "Unexpected end of line")]
    public void TryParse_RecognizesStandardDiagnosticShapes(
        string line, string expectedFile, int expectedLine, DiagnosticSeverity expectedSeverity, string expectedMessage)
    {
        var diagnostic = Cc65DiagnosticParser.TryParse(line);

        Assert.NotNull(diagnostic);
        Assert.Equal(expectedFile, diagnostic!.FilePath);
        Assert.Equal(expectedLine, diagnostic.Line);
        Assert.Equal(expectedSeverity, diagnostic.Severity);
        Assert.Equal(expectedMessage, diagnostic.Message);
    }

    [Theory]
    [InlineData("cl65: creating output.prg")]
    [InlineData("")]
    [InlineData("1 error(s), 0 warning(s)")]
    public void TryParse_ReturnsNullForNonDiagnosticLines(string line)
    {
        Assert.Null(Cc65DiagnosticParser.TryParse(line));
    }

    [Fact]
    public void ParseAll_FiltersOutNonDiagnosticLines()
    {
        var lines = new[]
        {
            "cl65: compiling main.c",
            "main.c(5): Error: undeclared identifier 'foo'",
            "main.c(9): Warning: unused variable 'bar'",
            "1 error(s), 1 warning(s)",
        };

        var diagnostics = Cc65DiagnosticParser.ParseAll(lines);

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[0].Severity);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostics[1].Severity);
    }
}
