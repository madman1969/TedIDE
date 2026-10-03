namespace Tedide.Core.Tests;

/// <summary>
/// Save All (every build, and File > Save) only writes project and solution files that changed. It
/// used to rewrite them all, which added any fields newer than the file - so a project from an older
/// Tedide showed up as changed in git after every build, with nothing changed by the user.
/// </summary>
public sealed class SaveIfChangedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void AnOlderProjectFile_IsLeftAlone_UntilSomethingChanges()
    {
        var path = Path.Combine(_dir, "Old.tproj");
        const string original = """{ "Name": "Old", "Target": "C64", "SourceFiles": ["src/main.c"] }""";
        File.WriteAllText(path, original);
        var project = TedideProject.Load(path);

        Assert.False(project.SaveIfChanged());
        Assert.Equal(original, File.ReadAllText(path));

        project.GenerateDebugInfo = true;
        Assert.True(project.SaveIfChanged());
        Assert.True(TedideProject.Load(path).GenerateDebugInfo);

        // Saved, so unchanged again.
        Assert.False(project.SaveIfChanged());
    }

    [Fact]
    public void ASolutionFile_IsLeftAlone_UntilSomethingChanges()
    {
        var path = Path.Combine(_dir, "Old.tsln");
        const string original = """{ "Name": "Old", "ProjectPaths": ["Old.tproj"] }""";
        File.WriteAllText(path, original);
        var solution = TedideSolution.Load(path);

        Assert.False(solution.SaveIfChanged());
        Assert.Equal(original, File.ReadAllText(path));

        solution.StartupProject = "Old.tproj";
        Assert.True(solution.SaveIfChanged());
        Assert.Equal("Old.tproj", TedideSolution.Load(path).StartupProject);
    }

    [Fact]
    public void AFileDeletedFromDisk_IsWrittenAgain()
    {
        var path = Path.Combine(_dir, "Gone.tproj");
        new TedideProject { Name = "Gone" }.Save(path);
        var project = TedideProject.Load(path);
        File.Delete(path);

        Assert.True(project.SaveIfChanged());
        Assert.True(File.Exists(path));
    }
}
