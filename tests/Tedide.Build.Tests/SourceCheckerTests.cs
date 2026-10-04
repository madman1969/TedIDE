using Tedide.Core;

namespace Tedide.Build.Tests;

/// <summary>
/// <see cref="SourceChecker"/>: the cc65/ca65 command lines it builds, and a check run against a
/// .cmd stand-in for cc65 that reports an error in the file it was given and a warning in a header.
/// </summary>
public sealed class SourceCheckerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-checker-").FullName;
    private readonly TedideProject _project;

    public SourceCheckerTests()
    {
        _project = new TedideProject
        {
            Name = "Game",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            IncludePaths = ["include"],
            PreprocessorDefines = ["DEBUG=1"],
            ExtraArguments = ["-O", "-Cl", "-D", "PAL", "-C", "game.cfg", "--asm-define", "FAST=1", "-Iextra"],
        };
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        _project.Save(Path.Combine(_dir, "Game.tproj"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MainC => Path.Combine(_dir, "src", "main.c");

    [Theory]
    [InlineData("src/main.c", true)]
    [InlineData("src/border.s", true)]
    [InlineData("src/border.ASM", true)]
    [InlineData("include/main.h", false)]
    [InlineData("game.cfg", false)]
    public void CanCheck_CAndAssemblySources(string path, bool expected) => Assert.Equal(expected, SourceChecker.CanCheck(path));

    [Fact]
    public void CompileArguments_AreTheBuildsCompilerSettings_WithTheSourcesFolderFirst()
    {
        var args = SourceChecker.CompileArguments(_project, "src/main.c", @"T:\check\main.c", @"T:\check\out.s", [@"C:\gfx\include"]);

        Assert.Equal(
            ["-t", "c64", "--cpu", "6502", "-I", Path.Combine(_dir, "src"), "-I", "include", "-I", @"C:\gfx\include",
             "-D", "DEBUG=1", "-Cl", "-D", "PAL", "-Iextra", "-o", @"T:\check\out.s", @"T:\check\main.c"],
            args);
    }

    [Fact]
    public void AssembleArguments_TranslateCl65sAssemblerOptions()
    {
        var args = SourceChecker.AssembleArguments(_project, "src/border.s", @"T:\check\border.s", @"T:\check\out.o");

        Assert.Equal(["-t", "c64", "--cpu", "6502", "-I", Path.Combine(_dir, "src"), "-D", "FAST=1", "-o", @"T:\check\out.o", @"T:\check\border.s"], args);
    }

    [Fact]
    public void CompilerOptions_KeepOnlyWhatCc65Understands()
    {
        Assert.Equal(["-W", "error", "--standard", "c99", "-j"],
            SourceChecker.CompilerOptions(["-W", "error", "-Oirs", "--standard", "c99", "-j", "-l", "listing.lst", "-W"]));
    }

    [Fact]
    public async Task CheckAsync_ReportsTheFilesOwnProblems_AndItsHeadersByFullPath()
    {
        var log = Path.Combine(_dir, "copy.txt");
        var fakeCl65 = Path.Combine(_dir, "cl65.cmd");
        // The checker runs cc65 from beside cl65 - its last argument is the copy it checks.
        File.WriteAllText(Path.Combine(_dir, "cc65.cmd"), string.Join("\r\n",
            "@echo off",
            "set last=",
            ":next",
            "if \"%~1\"==\"\" goto done",
            "set last=%~1",
            "shift",
            "goto next",
            ":done",
            $"type \"%last%\" > \"{log}\"",
            "echo In file included from %last%:1:",
            "echo include/screen.h:2: Warning: Implicit 'int' type specifier is an obsolete feature",
            "echo %last%:3: Error: ';' expected",
            "echo 1 errors and 1 warnings generated.",
            "exit /b 1",
            ""));
        var checker = new SourceChecker(fakeCl65);

        var found = await checker.CheckAsync(_project, MainC, "int main(void)\n{\n    return 0\n}\n", []);

        Assert.Equal(
            [
                new BuildDiagnostic(Path.Combine(_dir, "include", "screen.h"), 2, DiagnosticSeverity.Warning, "Implicit 'int' type specifier is an obsolete feature"),
                new BuildDiagnostic(MainC, 3, DiagnosticSeverity.Error, "';' expected"),
            ],
            found);
        Assert.Equal("int main(void)\n{\n    return 0\n}\n", File.ReadAllText(log));  // the unsaved text, not the file
        Assert.False(File.Exists(MainC));
    }

    [Fact]
    public async Task CheckAsync_IsNull_WhenTheCompilerIsntThere()
    {
        var checker = new SourceChecker(Path.Combine(_dir, "nowhere", "cl65.exe"));

        Assert.Null(await checker.CheckAsync(_project, MainC, "int x;\n", []));
    }
}
