using Tedide.Core;

namespace Tedide.Build.Tests;

public class Cc65ToolchainTests
{
    [Fact]
    public void CompileSteps_PlacesExtraArgumentsBeforeTheSourceFile()
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

        var args = GenerateStep(project, "src/main.c");

        var extraArgIndex = args.IndexOf("-I");
        var sourceFileIndex = args.IndexOf("src/main.c");
        Assert.True(extraArgIndex >= 0 && sourceFileIndex >= 0);
        Assert.True(extraArgIndex < sourceFileIndex,
            $"Expected ExtraArguments (at {extraArgIndex}) before the source file (at {sourceFileIndex}): {string.Join(" ", args)}");
    }

    [Fact]
    public void CompileSteps_CompilesOnlyOneSourceFile()
    {
        // BuildAsync calls this once per source file, not once for the project's whole
        // SourceFiles list - only the one source file passed in should appear.
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c", "src/screen.c"],
        };

        var args = Cc65Toolchain.BuildCompileSteps(project, "src/main.c").SelectMany(s => s.Arguments).ToList();

        Assert.Contains("src/main.c", args);
        Assert.DoesNotContain("src/screen.c", args);
    }

    [Fact]
    public void CompileSteps_CompilesACFileToAssemblyInObj_ThenAssemblesThatWithDashC()
    {
        // Never a single "cl65 -c src/foo.c": cl65 writes that compile's intermediate foo.s
        // beside the source and deletes it afterward - destroying a hand-written src/foo.s.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, SourceFiles = ["src/foo.c"] };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var steps = Cc65Toolchain.BuildCompileSteps(project, "src/foo.c").Select(s => s.Arguments).ToList();

            Assert.Equal(2, steps.Count);
            var generated = Path.Combine("obj", "src", "foo.c.s");
            Assert.Contains("-S", steps[0]);
            Assert.Equal(generated, steps[0][steps[0].IndexOf("-o") + 1]);
            Assert.Equal("src/foo.c", steps[0][^1]);

            Assert.Contains("-c", steps[1]);
            Assert.Equal(Path.Combine("obj", "src", "foo.c.o"), steps[1][steps[1].IndexOf("-o") + 1]);
            Assert.Equal(generated, steps[1][^1]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CompileSteps_AssemblesAnAssemblyFileDirectly_InOneStep()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, SourceFiles = ["src/border.s"] };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var steps = Cc65Toolchain.BuildCompileSteps(project, "src/border.s").Select(s => s.Arguments).ToList();

            var step = Assert.Single(steps);
            Assert.Contains("-c", step);
            Assert.DoesNotContain("-S", step);
            Assert.Equal(Path.Combine("obj", "src", "border.s.o"), step[step.IndexOf("-o") + 1]);
            Assert.Equal("src/border.s", step[^1]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CompileSteps_GiveACAndAnAssemblyFileWithTheSameBaseName_DistinctOutputs()
    {
        // foo.c and foo.s used to both compile to foo.o/foo.lst, one silently overwriting the other.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, SourceFiles = ["src/foo.c", "src/foo.s"] };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var cOutputs = Cc65Toolchain.BuildCompileSteps(project, "src/foo.c").Select(s => s.Arguments).SelectMany(OutputsOf).ToList();
            var sOutputs = Cc65Toolchain.BuildCompileSteps(project, "src/foo.s").Select(s => s.Arguments).SelectMany(OutputsOf).ToList();

            Assert.Empty(cOutputs.Intersect(sOutputs));
            // And nothing is ever written back into src/, where a hand-written file could live.
            Assert.All(cOutputs.Concat(sOutputs), path => Assert.StartsWith("obj", path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }

        static IEnumerable<string> OutputsOf(List<string> step)
        {
            foreach (var flag in new[] { "-o", "-l" })
            {
                var index = step.IndexOf(flag);
                if (index >= 0)
                    yield return step[index + 1];
            }
        }
    }

    [Fact]
    public void CompileSteps_OmitsOptimizationFlag_WhenLevelIsNone()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            OptimizationLevel = Cc65OptimizationLevel.None,
        };

        var args = GenerateStep(project, "src/main.c");

        Assert.DoesNotContain(args, a => a.StartsWith("-O", StringComparison.Ordinal));
    }

    [Fact]
    public void CompileSteps_PlacesOptimizationFlagBeforeExtraArgumentsAndSourceFile()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            ExtraArguments = ["-I", "include"],
            OptimizationLevel = Cc65OptimizationLevel.Extended,
        };

        var args = GenerateStep(project, "src/main.c");

        var optimizationIndex = args.IndexOf("-Ox");
        var extraArgIndex = args.IndexOf("-I");
        var sourceFileIndex = args.IndexOf("src/main.c");
        Assert.True(optimizationIndex >= 0 && optimizationIndex < extraArgIndex && extraArgIndex < sourceFileIndex,
            $"Expected -Ox (at {optimizationIndex}) before ExtraArguments (at {extraArgIndex}) before the source file (at {sourceFileIndex}): {string.Join(" ", args)}");
    }

    [Fact]
    public void CompileSteps_OmitsIncludePathFlags_WhenIncludePathsIsEmpty()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
        };

        var args = GenerateStep(project, "src/main.c");

        Assert.DoesNotContain("-I", args);
    }

    [Fact]
    public void CompileSteps_AddsAnIncludeFlagPair_ForEachIncludePath_BeforeTheSourceFile()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            IncludePaths = ["include", "../shared/include"],
        };

        var args = GenerateStep(project, "src/main.c");

        var firstIncludeIndex = args.IndexOf("-I");
        Assert.True(firstIncludeIndex >= 0, $"Expected -I in arguments: {string.Join(" ", args)}");
        Assert.Equal("include", args[firstIncludeIndex + 1]);
        var secondIncludeIndex = args.IndexOf("-I", firstIncludeIndex + 1);
        Assert.True(secondIncludeIndex >= 0, $"Expected a second -I in arguments: {string.Join(" ", args)}");
        Assert.Equal("../shared/include", args[secondIncludeIndex + 1]);
        Assert.True(secondIncludeIndex < args.IndexOf("src/main.c"));
    }

    [Fact]
    public void CompileSteps_OmitsDefineFlags_WhenPreprocessorDefinesIsEmpty()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
        };

        var args = GenerateStep(project, "src/main.c");

        Assert.DoesNotContain("-D", args);
    }

    [Fact]
    public void CompileSteps_AddsADefineFlagPair_ForEachPreprocessorDefine_BeforeTheSourceFile()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            PreprocessorDefines = ["DEBUG", "VERSION=3"],
        };

        var args = GenerateStep(project, "src/main.c");

        var firstDefineIndex = args.IndexOf("-D");
        Assert.True(firstDefineIndex >= 0, $"Expected -D in arguments: {string.Join(" ", args)}");
        Assert.Equal("DEBUG", args[firstDefineIndex + 1]);
        var secondDefineIndex = args.IndexOf("-D", firstDefineIndex + 1);
        Assert.True(secondDefineIndex >= 0, $"Expected a second -D in arguments: {string.Join(" ", args)}");
        Assert.Equal("VERSION=3", args[secondDefineIndex + 1]);
        Assert.True(secondDefineIndex < args.IndexOf("src/main.c"));
    }

    [Fact]
    public void BuildLinkArguments_NeverEmitsIncludePathOrDefineFlags_EvenWhenProjectHasThem()
    {
        // -I/-D are compile-time-only cl65 flags - ld65 has no use for them, so the link step
        // must never emit them regardless of what's set on the project.
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            IncludePaths = ["include"],
            PreprocessorDefines = ["DEBUG"],
        };

        var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o"]);

        Assert.DoesNotContain("-I", args);
        Assert.DoesNotContain("-D", args);
    }

    [Fact]
    public void CompileSteps_OmitsListingFlag_WhenGenerateAssemblyListingIsFalse()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            GenerateAssemblyListing = false,
        };

        var args = Cc65Toolchain.BuildCompileSteps(project, "src/main.c").SelectMany(s => s.Arguments).ToList();

        Assert.DoesNotContain("-l", args);
    }

    [Fact]
    public void CompileSteps_IncludesListingFlag_OnTheAssembleStep_WithThatSourceFilesResolvedListingPath_WhenGenerateAssemblyListingIsTrue()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c", "src/screen.c"],
            GenerateAssemblyListing = true,
        };

        var args = AssembleStep(project, "src/screen.c");

        var listingFlagIndex = args.IndexOf("-l");
        Assert.True(listingFlagIndex >= 0, $"Expected -l in arguments: {string.Join(" ", args)}");
        Assert.Equal(Path.GetRelativePath(project.Directory, project.ResolvedListingFiles.Last()), args[listingFlagIndex + 1]);
    }

    [Fact]
    public void CompileSteps_OmitsSourceCommentFlag_WhenAddSourceAsCommentIsFalse()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            AddSourceAsComment = false,
        };

        var args = GenerateStep(project, "src/main.c");

        Assert.DoesNotContain("-T", args);
    }

    [Fact]
    public void CompileSteps_IncludesSourceCommentFlag_WhenAddSourceAsCommentIsTrue()
    {
        var project = new TedideProject
        {
            Name = "Test",
            Target = Cc65Target.C64,
            SourceFiles = ["src/main.c"],
            AddSourceAsComment = true,
        };

        var args = GenerateStep(project, "src/main.c");

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
    public void CompileSteps_OmitsDebugFlag_WhenGenerateDebugInfoIsFalse()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, SourceFiles = ["src/main.c"] };

        var args = Cc65Toolchain.BuildCompileSteps(project, "src/main.c").SelectMany(s => s.Arguments).ToList();

        Assert.DoesNotContain("-g", args);
    }

    [Fact]
    public void CompileSteps_IncludesDebugFlag_OnBothSteps_WhenGenerateDebugInfoIsTrue()
    {
        // Both halves need it: -S so cc65 emits C line info into the generated assembly, -c so
        // ca65 carries that into the object file for ld65's .dbg output.
        var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, SourceFiles = ["src/main.c"], GenerateDebugInfo = true };

        Assert.All(Cc65Toolchain.BuildCompileSteps(project, "src/main.c"), step => Assert.Contains("-g", step.Arguments));
    }

    [Fact]
    public void BuildLinkArguments_OmitsWlDbgfile_WhenGenerateDebugInfoIsFalse()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.C64 };

        var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o"]);

        Assert.DoesNotContain("-Wl", args);
    }

    [Fact]
    public void BuildLinkArguments_IncludesWlDbgfile_WithTheResolvedDebugInfoPath_WhenGenerateDebugInfoIsTrue()
    {
        // cl65 has no top-level flag for ld65's --dbgfile - it has to go through -Wl's linker-
        // option passthrough, comma-joined ("--dbgfile,<path>") the way cc65's own -Wl syntax
        // expects, confirmed against a real cl65/ld65 build (see DbgFileTests).
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, GenerateDebugInfo = true };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o"]);

            var wlIndex = args.IndexOf("-Wl");
            Assert.True(wlIndex >= 0, $"Expected -Wl in arguments: {string.Join(" ", args)}");
            Assert.Equal($"--dbgfile,{project.ResolvedDebugInfoFile}", args[wlIndex + 1]);
            Assert.True(wlIndex < args.IndexOf("src/main.o"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Clean_RemovesDebugInfoFile_WhenItExists()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, SourceFiles = ["main.c"], GenerateDebugInfo = true };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            File.WriteAllText(project.ResolvedDebugInfoFile, "");

            var removed = new Cc65Toolchain().Clean(project);

            Assert.Contains(project.ResolvedDebugInfoFile, removed);
            Assert.False(File.Exists(project.ResolvedDebugInfoFile));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void BuildLinkArguments_OmitsConfigFlag_WhenLinkerConfigPathIsUnset()
    {
        var project = new TedideProject { Name = "Test", Target = Cc65Target.C64 };

        var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o"]);

        Assert.DoesNotContain("-C", args);
    }

    [Fact]
    public void BuildLinkArguments_IncludesConfigFlag_WithTheResolvedConfigPath_WhenLinkerConfigPathIsSet()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, LinkerConfigPath = "custom.cfg" };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var args = Cc65Toolchain.BuildLinkArguments(project, ["src/main.o"]);

            var configFlagIndex = args.IndexOf("-C");
            Assert.True(configFlagIndex >= 0, $"Expected -C in arguments: {string.Join(" ", args)}");
            Assert.Equal(Path.Combine(dir.FullName, "custom.cfg"), args[configFlagIndex + 1]);
            Assert.True(configFlagIndex < args.IndexOf("src/main.o"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
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
    public void Clean_RemovesTheBuildOutputsInObj_AndTheNowEmptyObjDirectory_AndTheOutputBinary()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject
            {
                Name = "Test",
                Target = Cc65Target.C64,
                SourceFiles = ["src/main.c"],
                OutputFile = "bin/Test.prg",
                GenerateAssemblyListing = true,
            };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var objectFile = project.ResolvedObjectFileFor("src/main.c");
            var listingFile = project.ResolvedListingFileFor("src/main.c");
            var generatedAssembly = project.ResolvedGeneratedAssemblyFileFor("src/main.c");
            Directory.CreateDirectory(Path.GetDirectoryName(objectFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(project.ResolvedOutputFile)!);
            foreach (var file in new[] { objectFile, listingFile, generatedAssembly, project.ResolvedOutputFile })
                File.WriteAllText(file, "");

            var removed = new Cc65Toolchain().Clean(project);

            Assert.Equal(
                new[] { objectFile, listingFile, generatedAssembly, project.ResolvedOutputFile }.Order(),
                removed.Order());
            Assert.False(Directory.Exists(project.ResolvedObjectDirectory));
            Assert.False(File.Exists(project.ResolvedOutputFile));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Clean_LeavesAnythingInObjThatTheBuildDidNotWrite()
    {
        // obj/ may have existed before Tedide used it - Clean must not destroy what's in it.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, SourceFiles = ["src/main.c"] };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var objectFile = project.ResolvedObjectFileFor("src/main.c");
            var notes = Path.Combine(project.ResolvedObjectDirectory, "notes.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(objectFile)!);
            File.WriteAllText(objectFile, "");
            File.WriteAllText(notes, "mine");

            var removed = new Cc65Toolchain().Clean(project);

            Assert.Equal([objectFile], removed);
            Assert.True(File.Exists(notes));
            // The src/ subfolder only held build output, so it's gone; obj/ itself still holds notes.txt.
            Assert.False(Directory.Exists(Path.GetDirectoryName(objectFile)));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Clean_AlsoRemovesObjectAndListingFilesLeftBesideSourcesByOlderBuilds_ButNeverAHandWrittenAssemblyFile()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, SourceFiles = ["src/foo.c", "src/foo.s"] };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            var src = Path.Combine(dir.FullName, "src");
            Directory.CreateDirectory(src);
            var legacyObject = Path.Combine(src, "foo.o");
            var legacyListing = Path.Combine(src, "foo.lst");
            var handWritten = Path.Combine(src, "foo.s");
            File.WriteAllText(legacyObject, "");
            File.WriteAllText(legacyListing, "");
            File.WriteAllText(handWritten, "; hand-written");

            var removed = new Cc65Toolchain().Clean(project);

            Assert.Equal(new[] { legacyObject, legacyListing }.Order(), removed.Order());
            Assert.True(File.Exists(handWritten));
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

            var mainListing = project.ResolvedListingFileFor("main.c");
            Directory.CreateDirectory(Path.GetDirectoryName(mainListing)!);
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

    [Fact]
    public async Task BuildAsync_KillsTheWholeCl65ProcessTree_WhenCancelled()
    {
        // A stand-in "cl65" that, like the real one, hands the work to a child process - here a
        // PowerShell that records its own PID and then just sleeps - so the test can check that
        // cancelling kills the child too, not only the process BuildAsync started directly.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var pidFile = Path.Combine(dir.FullName, "child.pid");
            var fakeCl65 = Path.Combine(dir.FullName, "fake-cl65.cmd");
            File.WriteAllText(fakeCl65,
                $"@powershell -NoProfile -Command \"$PID | Set-Content -LiteralPath '{pidFile}'; Start-Sleep -Seconds 60\"\r\n");

            var project = new TedideProject { Name = "Test", Target = Cc65Target.C64, SourceFiles = ["main.c"] };
            project.Save(Path.Combine(dir.FullName, "Test.tproj"));

            using var cts = new CancellationTokenSource();
            var build = new Cc65Toolchain(fakeCl65).BuildAsync(project, cancellationToken: cts.Token);

            // Generous waits throughout: with every test project running at once (and coverage on),
            // starting PowerShell alone can take several seconds.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            int childPid;
            // Set-Content ends the PID with a newline, so a file without one is still being written.
            while (!File.Exists(pidFile) || !TryReadPid(pidFile, out childPid))
            {
                Assert.True(DateTime.UtcNow < deadline, "The fake cl65's child process never started.");
                await Task.Delay(100);
            }

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);

            Assert.True(await HasExitedAsync(childPid, TimeSpan.FromSeconds(20)),
                $"cl65's child process (PID {childPid}) was still running after the build was cancelled.");
        }
        finally
        {
            await DeleteWhenReleasedAsync(dir);
        }
    }

    private static bool TryReadPid(string pidFile, out int pid)
    {
        pid = 0;
        try
        {
            var text = File.ReadAllText(pidFile);
            return text.EndsWith('\n') && int.TryParse(text.Trim(), out pid);
        }
        catch (IOException)
        {
            return false; // Still open for writing.
        }
    }

    /// <summary>Deletes a folder whose killed processes may still, briefly, hold it as their
    /// working directory.</summary>
    private static async Task DeleteWhenReleasedAsync(DirectoryInfo dir)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                dir.Delete(recursive: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(250);
            }
        }
    }

    /// <summary>The compile-to-assembly step for a C source (the first of its two steps).</summary>
    private static List<string> GenerateStep(TedideProject project, string sourceFile) =>
        Cc65Toolchain.BuildCompileSteps(project, sourceFile)[0].Arguments;

    /// <summary>The assemble step - a C source's second step, or an assembly source's only one.</summary>
    private static List<string> AssembleStep(TedideProject project, string sourceFile) =>
        Cc65Toolchain.BuildCompileSteps(project, sourceFile)[^1].Arguments;

    private static async Task<bool> HasExitedAsync(int pid, TimeSpan timeout)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (ArgumentException)
        {
            return true; // No such process any more.
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
