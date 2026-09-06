using Tedide.Core;

namespace Tedide.Build.Tests;

public class Cc65ToolchainTests
{
    [Fact]
    public void BuildArguments_PlacesExtraArgumentsBeforeSourceFiles()
    {
        // cl65 applies flags left-to-right as it encounters them on the command line, so an
        // "-I" include path (or any other ExtraArguments flag) only affects source files listed
        // after it - putting ExtraArguments after SourceFiles silently makes them no-ops.
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            ExtraArguments = ["-I", "include"],
        };

        var args = Cc65Toolchain.BuildArguments(project);

        var extraArgIndex = args.IndexOf("-I");
        var sourceFileIndex = args.IndexOf("src/main.c");
        Assert.True(extraArgIndex >= 0 && sourceFileIndex >= 0);
        Assert.True(extraArgIndex < sourceFileIndex,
            $"Expected ExtraArguments (at {extraArgIndex}) before SourceFiles (at {sourceFileIndex}): {string.Join(" ", args)}");
    }

    [Fact]
    public async Task BuildAsync_CreatesOutputDirectoryEvenIfToolchainIsMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject
            {
                Name = "Test",
                Target = Cc65Target.C64,
                SourceFiles = ["main.c"],
                OutputFile = "bin/Test.prg",
            };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            // ld65 fails outright if the output directory doesn't already exist rather than
            // creating it, so Cc65Toolchain must create it before invoking the toolchain. Pointing
            // at a nonexistent executable keeps this test independent of cc65 actually being
            // installed on the machine running it.
            var toolchain = new Cc65Toolchain("this-executable-definitely-does-not-exist-12345");
            var result = await toolchain.BuildAsync(project);

            Assert.False(result.Succeeded);
            Assert.True(Directory.Exists(Path.Combine(dir.FullName, "bin")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
