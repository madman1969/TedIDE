using Tedide.Core;

namespace Tedide.Build.Tests;

public class Cc65ToolchainTests
{
    [Fact]
    public void BuildCompileArguments_PlacesExtraArgumentsBeforeTheSourceFile()
    {
        // cl65 applies flags left-to-right as it encounters them on the command line, so an
        // "-I" include path (or any other ExtraArguments flag) only affects source files listed
        // after it - putting ExtraArguments after the source file silently makes them no-ops.
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            ExtraArguments = ["-I", "include"],
        };

        var args = Cc65Toolchain.BuildCompileArguments(project, "src/main.c");

        var extraArgIndex = args.IndexOf("-I");
        var sourceFileIndex = args.IndexOf("src/main.c");
        Assert.True(extraArgIndex >= 0 && sourceFileIndex >= 0);
        Assert.True(extraArgIndex < sourceFileIndex,
            $"Expected ExtraArguments (at {extraArgIndex}) before the source file (at {sourceFileIndex}): {string.Join(" ", args)}");
    }

    [Fact]
    public void BuildCompileArguments_CompilesOnlyOneSourceFile_WithDashC()
    {
        // -c stops cl65 after assembling (no link step), and only the one source file passed in
        // should appear - BuildAsync calls this once per source file, not once for the project's
        // whole SourceFiles list.
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c", "src/screen.c"],
        };

        var args = Cc65Toolchain.BuildCompileArguments(project, "src/main.c");

        Assert.Contains("-c", args);
        Assert.Contains("src/main.c", args);
        Assert.DoesNotContain("src/screen.c", args);
    }

    [Fact]
    public void BuildCompileArguments_OmitsOptimizationFlag_WhenLevelIsNone()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            OptimizationLevel = Cc65OptimizationLevel.None,
        };

        var args = Cc65Toolchain.BuildCompileArguments(project, "src/main.c");

        Assert.DoesNotContain(args, a => a.StartsWith("-O", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildCompileArguments_PlacesOptimizationFlagBeforeExtraArgumentsAndSourceFile()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            ExtraArguments = ["-I", "include"],
            OptimizationLevel = Cc65OptimizationLevel.Extended,
        };

        var args = Cc65Toolchain.BuildCompileArguments(project, "src/main.c");

        var optimizationIndex = args.IndexOf("-Ox");
        var extraArgIndex = args.IndexOf("-I");
        var sourceFileIndex = args.IndexOf("src/main.c");
        Assert.True(optimizationIndex >= 0 && optimizationIndex < extraArgIndex && extraArgIndex < sourceFileIndex,
            $"Expected -Ox (at {optimizationIndex}) before ExtraArguments (at {extraArgIndex}) before the source file (at {sourceFileIndex}): {string.Join(" ", args)}");
    }

    [Fact]
    public void BuildCompileArguments_OmitsListingFlag_WhenGenerateAssemblyListingIsFalse()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            GenerateAssemblyListing = false,
        };

        var args = Cc65Toolchain.BuildCompileArguments(project, "src/main.c");

        Assert.DoesNotContain("-l", args);
    }

    [Fact]
    public void BuildCompileArguments_IncludesListingFlag_WithThatSourceFilesResolvedListingPath_WhenGenerateAssemblyListingIsTrue()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c", "src/screen.c"],
            GenerateAssemblyListing = true,
        };

        var args = Cc65Toolchain.BuildCompileArguments(project, "src/screen.c");

        var listingFlagIndex = args.IndexOf("-l");
        Assert.True(listingFlagIndex >= 0, $"Expected -l in arguments: {string.Join(" ", args)}");
        Assert.Equal(project.ResolvedListingFiles.Last(), args[listingFlagIndex + 1]);
    }

    [Fact]
    public void BuildCompileArguments_OmitsSourceCommentFlag_WhenAddSourceAsCommentIsFalse()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            AddSourceAsComment = false,
        };

        var args = Cc65Toolchain.BuildCompileArguments(project, "src/main.c");

        Assert.DoesNotContain("-T", args);
    }

    [Fact]
    public void BuildCompileArguments_IncludesSourceCommentFlag_WhenAddSourceAsCommentIsTrue()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            AddSourceAsComment = true,
        };

        var args = Cc65Toolchain.BuildCompileArguments(project, "src/main.c");

        Assert.Contains("-T", args);
    }

    [Fact]
    public void BuildLinkArguments_PlacesExtraArgumentsBeforeObjectFiles()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            ExtraArguments = ["-C", "custom.cfg"],
        };

        var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o", "src/screen.o"]);

        var extraArgIndex = args.IndexOf("-C");
        var objectFileIndex = args.IndexOf("src/main.o");
        Assert.True(extraArgIndex >= 0 && objectFileIndex >= 0);
        Assert.True(extraArgIndex < objectFileIndex,
            $"Expected ExtraArguments (at {extraArgIndex}) before object files (at {objectFileIndex}): {string.Join(" ", args)}");
    }

    [Fact]
    public void BuildLinkArguments_IncludesEveryObjectFile_AndTheResolvedOutputPath()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            OutputFile = "bin/Test.prg",
        };

        var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o", "src/screen.o"]);

        Assert.Contains("src/main.o", args);
        Assert.Contains("src/screen.o", args);
        var outputFlagIndex = args.IndexOf("-o");
        Assert.True(outputFlagIndex >= 0);
        Assert.Equal(project.ResolvedOutputFile, args[outputFlagIndex + 1]);
    }

    [Fact]
    public void BuildLinkArguments_OmitsMapFlag_WhenGenerateLinkerMapIsFalse()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            GenerateLinkerMap = false,
        };

        var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o"]);

        Assert.DoesNotContain("-m", args);
    }

    [Fact]
    public void BuildLinkArguments_IncludesMapFlag_WithTheResolvedMapPath_WhenGenerateLinkerMapIsTrue()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            GenerateLinkerMap = true,
        };

        var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o"]);

        var mapFlagIndex = args.IndexOf("-m");
        Assert.True(mapFlagIndex >= 0, $"Expected -m in arguments: {string.Join(" ", args)}");
        Assert.Equal(project.ResolvedMapFile, args[mapFlagIndex + 1]);
    }

    [Fact]
    public void BuildLinkArguments_OmitsLabelsFlag_WhenExportLabelsIsFalse()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            ExportLabels = false,
        };

        var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o"]);

        Assert.DoesNotContain("-Ln", args);
    }

    [Fact]
    public void BuildLinkArguments_IncludesLabelsFlag_WithTheResolvedLabelsPath_WhenExportLabelsIsTrue()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            ExportLabels = true,
        };

        var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o"]);

        var labelsFlagIndex = args.IndexOf("-Ln");
        Assert.True(labelsFlagIndex >= 0, $"Expected -Ln in arguments: {string.Join(" ", args)}");
        Assert.Equal(project.ResolvedLabelsFile, args[labelsFlagIndex + 1]);
    }

    [Fact]
    public void BuildLinkArguments_OmitsLibFiles_WhenLibFolderIsAbsentOrEmpty()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.C64 };

        var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o"]);

        Assert.DoesNotContain(args, a => a.EndsWith(".lib"));
    }

    [Fact]
    public void BuildLinkArguments_AppendsEveryLibFile_AfterTheObjectFiles()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var libDir = Path.Combine(dir.FullName, "lib");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "vendor.lib"), "");

            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64 };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o"]);

            var objectFileIndex = args.IndexOf("src/main.o");
            var libFileIndex = args.IndexOf(Path.Combine(libDir, "vendor.lib"));
            Assert.True(objectFileIndex >= 0 && libFileIndex >= 0);
            Assert.True(objectFileIndex < libFileIndex,
                $"Expected the object file (at {objectFileIndex}) before the .lib file (at {libFileIndex}): {string.Join(" ", args)}");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Clean_RemovesObjectFilesOutputBinaryAndEachSourceFilesListingFile()
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
            var listingFile = Path.Combine(dir.FullName, "main.lst");
            Directory.CreateDirectory(Path.GetDirectoryName(project.ResolvedOutputFile)!);
            File.WriteAllText(objectFile, "");
            File.WriteAllText(project.ResolvedOutputFile, "");
            File.WriteAllText(listingFile, "");

            var removed = new Cc65Toolchain().Clean(project);

            Assert.Equal([objectFile, project.ResolvedOutputFile, listingFile], removed);
            Assert.False(File.Exists(objectFile));
            Assert.False(File.Exists(project.ResolvedOutputFile));
            Assert.False(File.Exists(listingFile));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Clean_RemovesMapAndLabelsFiles_WhenTheyExist()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject
            {
                Name = "Test",
                Target = Cc65Target.C64,
                SourceFiles = ["main.c"],
                GenerateLinkerMap = true,
                ExportLabels = true,
            };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            File.WriteAllText(project.ResolvedMapFile, "");
            File.WriteAllText(project.ResolvedLabelsFile, "");

            var removed = new Cc65Toolchain().Clean(project);

            Assert.Contains(project.ResolvedMapFile, removed);
            Assert.Contains(project.ResolvedLabelsFile, removed);
            Assert.False(File.Exists(project.ResolvedMapFile));
            Assert.False(File.Exists(project.ResolvedLabelsFile));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Clean_RemovesOnlyTheListingFilesThatExist_WhenSomeSourceFilesWereNeverBuilt()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject
            {
                Name = "Test",
                Target = Cc65Target.C64,
                SourceFiles = ["main.c", "screen.c"],
                GenerateAssemblyListing = true,
            };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var mainListing = Path.Combine(dir.FullName, "main.lst");
            File.WriteAllText(mainListing, "");
            // screen.lst deliberately left absent, as if screen.c was never (re)compiled.

            var removed = new Cc65Toolchain().Clean(project);

            Assert.Equal([mainListing], removed);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Clean_LeavesListingFilesAlone_WhenNoneExist()
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

    [Fact]
    public async Task BuildAsync_StopsAfterFirstMissingToolchainFailure_RatherThanRetryingEverySourceFile()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject
            {
                Name = "Test",
                Target = Cc65Target.C64,
                SourceFiles = ["main.c", "screen.c", "input.c"],
                OutputFile = "bin/Test.prg",
            };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var toolchain = new Cc65Toolchain("this-executable-definitely-does-not-exist-12345");
            var result = await toolchain.BuildAsync(project);

            Assert.False(result.Succeeded);
            // One "Could not launch" line, not one per source file - BuildAsync gives up
            // immediately rather than repeating a failure that's about the toolchain itself, not
            // any particular source file.
            Assert.Single(result.RawOutputLines, l => l.Contains("Could not launch", StringComparison.Ordinal));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
