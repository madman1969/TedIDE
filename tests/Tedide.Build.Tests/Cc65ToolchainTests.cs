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
    public void BuildArguments_OmitsOptimizationFlag_WhenLevelIsNone()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            OptimizationLevel = Cc65OptimizationLevel.None,
        };

        var args = Cc65Toolchain.BuildArguments(project);

        Assert.DoesNotContain(args, a => a.StartsWith("-O", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildArguments_PlacesOptimizationFlagBeforeExtraArgumentsAndSourceFiles()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            ExtraArguments = ["-I", "include"],
            OptimizationLevel = Cc65OptimizationLevel.Extended,
        };

        var args = Cc65Toolchain.BuildArguments(project);

        var optimizationIndex = args.IndexOf("-Ox");
        var extraArgIndex = args.IndexOf("-I");
        var sourceFileIndex = args.IndexOf("src/main.c");
        Assert.True(optimizationIndex >= 0 && optimizationIndex < extraArgIndex && extraArgIndex < sourceFileIndex,
            $"Expected -Ox (at {optimizationIndex}) before ExtraArguments (at {extraArgIndex}) before SourceFiles (at {sourceFileIndex}): {string.Join(" ", args)}");
    }

    [Fact]
    public void BuildArguments_OmitsListingFlag_WhenGenerateAssemblyListingIsFalse()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            GenerateAssemblyListing = false,
        };

        var args = Cc65Toolchain.BuildArguments(project);

        Assert.DoesNotContain("-l", args);
    }

    [Fact]
    public void BuildArguments_IncludesListingFlag_WithResolvedListingPath_WhenGenerateAssemblyListingIsTrue()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            OutputFile = "bin/Test.prg",
            GenerateAssemblyListing = true,
        };

        var args = Cc65Toolchain.BuildArguments(project);

        var listingFlagIndex = args.IndexOf("-l");
        Assert.True(listingFlagIndex >= 0, $"Expected -l in arguments: {string.Join(" ", args)}");
        Assert.Equal(project.ResolvedListingFile, args[listingFlagIndex + 1]);
    }

    [Fact]
    public void BuildArguments_OmitsSourceCommentFlag_WhenAddSourceAsCommentIsFalse()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            AddSourceAsComment = false,
        };

        var args = Cc65Toolchain.BuildArguments(project);

        Assert.DoesNotContain("-T", args);
    }

    [Fact]
    public void BuildArguments_IncludesSourceCommentFlag_WhenAddSourceAsCommentIsTrue()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            AddSourceAsComment = true,
        };

        var args = Cc65Toolchain.BuildArguments(project);

        Assert.Contains("-T", args);
    }

    [Fact]
    public void Clean_RemovesObjectFilesOutputBinaryAndListingFile()
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
                GenerateAssemblyListing = true,
            };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var objectFile = Path.Combine(dir.FullName, "main.o");
            Directory.CreateDirectory(Path.GetDirectoryName(project.ResolvedOutputFile)!);
            File.WriteAllText(objectFile, "");
            File.WriteAllText(project.ResolvedOutputFile, "");
            File.WriteAllText(project.ResolvedListingFile, "");

            var removed = new Cc65Toolchain().Clean(project);

            Assert.Equal([objectFile, project.ResolvedOutputFile, project.ResolvedListingFile], removed);
            Assert.False(File.Exists(objectFile));
            Assert.False(File.Exists(project.ResolvedOutputFile));
            Assert.False(File.Exists(project.ResolvedListingFile));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Clean_LeavesListingFileAlone_WhenItDoesNotExist()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject
            {
                Name = "Test",
                Target = Cc65Target.C64,
                SourceFiles = ["main.c"],
                GenerateAssemblyListing = true,
            };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var removed = new Cc65Toolchain().Clean(project);

            Assert.Empty(removed);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
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
