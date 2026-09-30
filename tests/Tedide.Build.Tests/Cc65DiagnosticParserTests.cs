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

    // Real cl65 2.19 output that used to be missed entirely - a link failing on a memory-area
    // overflow reported "Build FAILED (0 error(s))" with an empty Error List.
    [Theory]
    [InlineData(@"C:\Libs\inc\bad.h:1: Error: Undeclared identifier 'undefined_thing'", @"C:\Libs\inc\bad.h", 1, DiagnosticSeverity.Error, "Undeclared identifier 'undefined_thing'")]
    [InlineData(@"C:\Libs\inc/bad.h(7): Warning: Unused variable 'x'", @"C:\Libs\inc/bad.h", 7, DiagnosticSeverity.Warning, "Unused variable 'x'")]
    [InlineData(@"ld65: Error: C:\CC65\CFG/c64.cfg:15: Size of memory area 'BSS' is negative: -182005", @"C:\CC65\CFG/c64.cfg", 15, DiagnosticSeverity.Error, "Size of memory area 'BSS' is negative: -182005")]
    [InlineData(@"ld65: Warning: C:\CC65\CFG/c64.cfg:14: Segment 'CODE' overflows memory area 'MAIN' by 179888 bytes", @"C:\CC65\CFG/c64.cfg", 14, DiagnosticSeverity.Warning, "Segment 'CODE' overflows memory area 'MAIN' by 179888 bytes")]
    [InlineData("ld65: Error: 1 unresolved external(s) found - cannot create output file", "", 0, DiagnosticSeverity.Error, "1 unresolved external(s) found - cannot create output file")]
    public void TryParse_RecognisesAbsolutePaths_AndToolsOwnMessages(string line, string expectedFile, int expectedLine, DiagnosticSeverity expectedSeverity, string expectedMessage)
    {
        var diagnostic = Cc65DiagnosticParser.TryParse(line);

        Assert.NotNull(diagnostic);
        Assert.Equal(expectedFile, diagnostic.FilePath);
        Assert.Equal(expectedLine, diagnostic.Line);
        Assert.Equal(expectedSeverity, diagnostic.Severity);
        Assert.Equal(expectedMessage, diagnostic.Message);
    }

    [Theory]
    [InlineData("In file included from src/main2.c:1:")]
    [InlineData("1 errors and 0 warnings generated.")]
    [InlineData("ld65: creating output file")]
    public void TryParse_StillIgnoresNonDiagnosticLines_ThatLookSimilar(string line)
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

    [Fact]
    public void TryParse_ReadsAnOpt6502Error()
    {
        var diagnostic = Cc65DiagnosticParser.TryParse("opt6502: Error: Cannot open obj/main.c.cc65.s");

        Assert.NotNull(diagnostic);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("Cannot open obj/main.c.cc65.s", diagnostic.Message);
    }

    [Theory]
    [InlineData("opt6502: src/main.c: 6 optimizations (5 jump to next line, 1 STZ rewrite), ~17 bytes and ~19 cycles saved")]
    [InlineData("opt6502: total for 2 C files: no optimizations found")]
    public void TryParse_IgnoresOpt6502Metrics(string line)
    {
        Assert.Null(Cc65DiagnosticParser.TryParse(line));
    }
}
