using Tedide.Core;

namespace Tedide.Core.Tests;

public class TedideProjectTests
{
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
    public void ResolvedListingFiles_AreSourceFilesWithLstExtension()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "MyGame.tproj");
            var project = new TedideProject { SourceFiles = ["main.c", Path.Combine("sub", "b.c")] };
            project.Save(path);

            var resolved = project.ResolvedListingFiles.ToList();

            Assert.Equal(Path.Combine(dir.FullName, "main.lst"), resolved[0]);
            Assert.Equal(Path.Combine(dir.FullName, "sub", "b.lst"), resolved[1]);
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
}
