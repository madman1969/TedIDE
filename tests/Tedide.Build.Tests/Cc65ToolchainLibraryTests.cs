using Tedide.Core;

namespace Tedide.Build.Tests;

public class Cc65ToolchainLibraryTests
{
    private static TedideProject Library(string name) => new()
    {
        Name = name,
        OutputType = ProjectOutputType.Library,
        SourceFiles = ["src/gfx.c"],
        OutputFile = $@"bin\{name}.lib",
        IncludePaths = ["include"],
        FilePath = $@"C:\sln\{name}\{name}.tproj",
    };

    private static TedideProject Game() => new()
    {
        Name = "Game",
        SourceFiles = ["src/main.c"],
        OutputFile = "bin/Game.prg",
        IncludePaths = ["include"],
        FilePath = @"C:\sln\Game\Game.tproj",
    };

    [Fact]
    public void AnApplication_GetsItsLibrariesIncludePathsAfterItsOwn()
    {
        var args = Cc65Toolchain.BuildCompileSteps(Game(), "src/main.c", [@"C:\sln\Gfx\include"])[0].Arguments;

        var own = args.IndexOf("include");
        var library = args.IndexOf(@"C:\sln\Gfx\include");
        Assert.True(own > 0 && library > own, string.Join(" ", args));
        Assert.Equal("-I", args[library - 1]);
        Assert.Contains("src/main.c", args); // its own paths stay relative
    }

    [Fact]
    public void ALibrary_IsCompiledWithFullPaths_SoAProgramsDebugInfoFindsItsSources()
    {
        var steps = Cc65Toolchain.BuildCompileSteps(Library("Gfx"), "src/gfx.c");

        Assert.Contains(@"C:\sln\Gfx\src\gfx.c", steps[0].Arguments);
        Assert.Contains(@"C:\sln\Gfx\obj\src\gfx.c.s", steps[0].Arguments);
        Assert.Contains(@"C:\sln\Gfx\obj\src\gfx.c.o", steps[^1].Arguments);
    }

    [Fact]
    public void TheLink_TakesReferencedLibrariesBeforeLibFolderOnes()
    {
        var args = Cc65Toolchain.BuildLinkArguments(Game(), [@"obj\src\main.c.o"], [Library("Gfx"), Library("Core")]);

        Assert.Equal([@"obj\src\main.c.o", @"C:\sln\Gfx\bin\Gfx.lib", @"C:\sln\Core\bin\Core.lib"], args.TakeLast(3));
    }

    [Fact]
    public void ALibrary_IsArchivedByAr65()
    {
        Assert.Equal(["r", @"C:\sln\Gfx\bin\Gfx.lib", "a.o", "b.o"], Cc65Toolchain.BuildArchiveArguments(Library("Gfx"), ["a.o", "b.o"]));
    }

    [Theory]
    [InlineData("cl65", "ar65")]
    [InlineData(@"C:\CC65\bin\cl65.exe", @"C:\CC65\bin\ar65.exe")]
    public void Ar65_IsFoundWhereCl65Is(string cl65, string expected)
    {
        Assert.Equal(expected, Cc65Toolchain.Ar65PathFor(cl65));
    }
}
