using Tedide.Core;

namespace Tedide.Core.Tests;

public class TedideProjectTests
{
    [Fact]
    public void LinkedDirectories_AreTheFoldersOutsideTheProjectItUses_OutermostOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "linked-test");
        var project = new TedideProject
        {
            FilePath = Path.Combine(root, "Lib", "Lib.tproj"),
            SourceFiles = ["src/own.c", "../src/a.c", "../src/b.s", "../src/deep/c.c", "../test/t.c"],
            IncludePaths = ["include", "../include", "../src"],
        };

        Assert.Equal(
            [Path.Combine(root, "src"), Path.Combine(root, "test"), Path.Combine(root, "include")],
            project.LinkedDirectories);
        Assert.Empty(new TedideProject { FilePath = project.FilePath, SourceFiles = ["src/own.c"], IncludePaths = ["include"] }.LinkedDirectories);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAllFields()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "MyGame.tproj");
            var project = new TedideProject
            {
                Name = "MyGame",
                Target = Cc65Target.Nes,
                SourceFiles = ["main.c", "sprites.s"],
                ExtraArguments = ["-Oi"],
                IncludePaths = ["include", "../shared/include"],
                PreprocessorDefines = ["DEBUG", "VERSION=3"],
                LinkerConfigPath = "custom.cfg",
                GenerateDebugInfo = true,
                OptimizationLevel = Cc65OptimizationLevel.Extended,
                GenerateAssemblyListing = true,
                AddSourceAsComment = true,
                EnableSuperCpu = true,
            };
            project.Save(path);

            var loaded = TedideProject.Load(path);

            Assert.Equal("MyGame", loaded.Name);
            Assert.Equal(Cc65Target.Nes, loaded.Target);
            Assert.Equal(["main.c", "sprites.s"], loaded.SourceFiles);
            Assert.Equal(["-Oi"], loaded.ExtraArguments);
            Assert.Equal(["include", "../shared/include"], loaded.IncludePaths);
            Assert.Equal(["DEBUG", "VERSION=3"], loaded.PreprocessorDefines);
            Assert.Equal("custom.cfg", loaded.LinkerConfigPath);
            Assert.True(loaded.GenerateDebugInfo);
            Assert.Equal(Cc65OptimizationLevel.Extended, loaded.OptimizationLevel);
            Assert.True(loaded.GenerateAssemblyListing);
            Assert.True(loaded.AddSourceAsComment);
            Assert.True(loaded.EnableSuperCpu);
            Assert.Equal(Path.GetFullPath(path), loaded.FilePath);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Save_DoesNotWriteComputedProperties()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "MyGame.tproj");
            var project = new TedideProject { Name = "MyGame", SourceFiles = ["main.c"] };
            project.Save(path);

            var json = File.ReadAllText(path);

            Assert.DoesNotContain("ResolvedOutputFile", json);
            Assert.DoesNotContain("ResolvedSourceFiles", json);
            Assert.DoesNotContain("FilePath", json);
            Assert.DoesNotContain("Directory", json);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolvedOutputFile_DefaultsToNamePlusTargetExtension()
    {
        var project = new TedideProject { Name = "MyGame", Target = Cc65Target.C64 };
        var path = Path.Combine(Path.GetTempPath(), "MyGame.tproj");
        project.Save(path);
        try
        {
            Assert.Equal(Path.Combine(Path.GetTempPath(), "MyGame.prg"), project.ResolvedOutputFile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolvedListingFiles_MirrorEachSourceFileUnderObj_WithLstAppended()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "MyGame.tproj");
            var project = new TedideProject { SourceFiles = ["main.c", "sub/b.s"] };
            project.Save(path);

            var resolved = project.ResolvedListingFiles.ToList();

            Assert.Equal(Path.Combine(dir.FullName, "obj", "main.c.lst"), resolved[0]);
            Assert.Equal(Path.Combine(dir.FullName, "obj", "sub", "b.s.lst"), resolved[1]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IntermediateFiles_KeepTheSourceExtension_SoACAndAnAssemblyFileWithTheSameBaseNameDontCollide()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject { SourceFiles = ["src/foo.c", "src/foo.s"] };
            project.Save(Path.Combine(dir.FullName, "MyGame.tproj"));

            Assert.Equal(Path.Combine(dir.FullName, "obj", "src", "foo.c.o"), project.ResolvedObjectFileFor("src/foo.c"));
            Assert.Equal(Path.Combine(dir.FullName, "obj", "src", "foo.s.o"), project.ResolvedObjectFileFor("src/foo.s"));
            Assert.Equal(Path.Combine(dir.FullName, "obj", "src", "foo.c.s"), project.ResolvedGeneratedAssemblyFileFor("src/foo.c"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IntermediateFiles_StayInsideObj_EvenForASourceOutsideTheProjectDirectory()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var projectDir = Path.Combine(dir.FullName, "game");
            Directory.CreateDirectory(projectDir);
            var project = new TedideProject { SourceFiles = ["../shared/util.c"] };
            project.Save(Path.Combine(projectDir, "MyGame.tproj"));

            var objectFile = project.ResolvedObjectFileFor("../shared/util.c");

            Assert.Equal(Path.Combine(projectDir, "obj", "__", "shared", "util.c.o"), objectFile);
            Assert.StartsWith(project.ResolvedObjectDirectory + Path.DirectorySeparatorChar, Path.GetFullPath(objectFile));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolvedSourceFiles_AreRelativeToProjectDirectory()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "sub.tproj");
            var project = new TedideProject { SourceFiles = ["a.c", Path.Combine("sub", "b.c")] };
            project.Save(path);

            var resolved = project.ResolvedSourceFiles.ToList();

            Assert.Equal(Path.Combine(dir.FullName, "a.c"), resolved[0]);
            Assert.Equal(Path.Combine(dir.FullName, "sub", "b.c"), resolved[1]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolvedLibFiles_IsEmpty_WhenLibFolderDoesNotExist()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var project = new TedideProject();
            project.Save(Path.Combine(dir.FullName, "NoLib.tproj"));

            Assert.Empty(project.ResolvedLibFiles);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolvedLibFiles_IsEmpty_WhenLibFolderExistsButHasNoLibFiles()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "lib"));
            var project = new TedideProject();
            project.Save(Path.Combine(dir.FullName, "EmptyLib.tproj"));

            Assert.Empty(project.ResolvedLibFiles);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolvedLibFiles_ReturnsEveryLibFile_InTheLibFolder()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var libDir = Path.Combine(dir.FullName, "lib");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "b.lib"), "");
            File.WriteAllText(Path.Combine(libDir, "a.lib"), "");
            // Not a .lib file - shouldn't show up alongside the two above.
            File.WriteAllText(Path.Combine(libDir, "readme.txt"), "");

            var project = new TedideProject();
            project.Save(Path.Combine(dir.FullName, "WithLibs.tproj"));

            var resolved = project.ResolvedLibFiles.ToList();

            Assert.Equal(
                [Path.Combine(libDir, "a.lib"), Path.Combine(libDir, "b.lib")],
                resolved);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void UseOpt6502_DefaultsToOff_AndRoundTrips()
    {
        Assert.False(new TedideProject().UseOpt6502);

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tproj");
        try
        {
            new TedideProject { Name = "Opt", UseOpt6502 = true }.Save(path);
            Assert.True(TedideProject.Load(path).UseOpt6502);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Opt6502Mode_DefaultsToSize_AndRoundTrips()
    {
        Assert.Equal(Opt6502Mode.Size, new TedideProject().Opt6502Mode);

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tproj");
        try
        {
            new TedideProject { Name = "Opt", Opt6502Mode = Opt6502Mode.Speed }.Save(path);
            Assert.Contains("\"Opt6502Mode\": \"Speed\"", File.ReadAllText(path));
            Assert.Equal(Opt6502Mode.Speed, TedideProject.Load(path).Opt6502Mode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolvedCc65Cpu_FollowsTargetAndSuperCpu()
    {
        Assert.Equal("6502", new TedideProject { Target = Cc65Target.C64 }.ResolvedCc65Cpu);
        Assert.Equal("65816", new TedideProject { Target = Cc65Target.C64, EnableSuperCpu = true }.ResolvedCc65Cpu);
        Assert.Equal("6502", new TedideProject { Target = Cc65Target.Vic20, EnableSuperCpu = true }.ResolvedCc65Cpu);
    }

    [Fact]
    public void ResolvedUnoptimizedAssemblyFileFor_SitsBesideTheGeneratedAssembly_InObj()
    {
        var project = new TedideProject { FilePath = Path.Combine(Path.GetTempPath(), "P", "P.tproj") };

        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "P", "obj", "src", "main.c.cc65.s"),
            project.ResolvedUnoptimizedAssemblyFileFor("src/main.c"));
    }
}
