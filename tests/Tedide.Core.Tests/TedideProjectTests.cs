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
            };
            project.Save(path);

            var loaded = TedideProject.Load(path);

            Assert.Equal("MyGame", loaded.Name);
            Assert.Equal(Cc65Target.Nes, loaded.Target);
            Assert.Equal(["main.c", "sprites.s"], loaded.SourceFiles);
            Assert.Equal(["-Oi"], loaded.ExtraArguments);
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
}
