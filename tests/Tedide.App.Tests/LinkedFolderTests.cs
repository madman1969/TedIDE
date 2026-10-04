using Tedide.App.Views;
using Tedide.Core;

namespace Tedide.App.Tests;

/// <summary>
/// Projects that build sources from folders outside their own, as samples/FarMem's do: the
/// Solution Explorer lists those folders under each project, and a file in one belongs to a
/// project that uses it.
/// </summary>
public sealed class LinkedFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tedide-linked-").FullName;
    private readonly Workspace _workspace = new();

    public LinkedFolderTests()
    {
        // Two libraries share ../src and ../include; App builds ../test/main.c and links LibB.
        foreach (var folder in new[] { "src", "include", "test", "LibA", "LibB", "App" })
            Directory.CreateDirectory(Path.Combine(_root, folder));
        File.WriteAllText(SharedC, "int shared;\n");
        File.WriteAllText(Path.Combine(_root, "include", "shared.h"), "extern int shared;\n");
        File.WriteAllText(MainC, "int main(void) { return 0; }\n");

        Library("LibA", Cc65Target.C64);
        Library("LibB", Cc65Target.C128);
        new TedideProject
        {
            Name = "App", Target = Cc65Target.C128, SourceFiles = ["../test/main.c"],
            ProjectReferences = ["../LibB/LibB.tproj"],
        }.Save(Path.Combine(_root, "App", "App.tproj"));

        var solution = new TedideSolution
        {
            Name = "Shared", ProjectPaths = ["LibA/LibA.tproj", "LibB/LibB.tproj", "App/App.tproj"], StartupProject = "App/App.tproj",
        };
        solution.Save(Path.Combine(_root, "Shared.tsln"));
        _workspace.OpenSolution(solution.FilePath!);
    }

    private void Library(string name, Cc65Target target) =>
        new TedideProject
        {
            Name = name, Target = target, OutputType = ProjectOutputType.Library,
            SourceFiles = ["../src/shared.c"], IncludePaths = ["../include"],
        }.Save(Path.Combine(_root, name, name + ".tproj"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string SharedC => Path.Combine(_root, "src", "shared.c");
    private string MainC => Path.Combine(_root, "test", "main.c");
    private TedideProject Project(string name) => _workspace.Projects.Single(p => p.Name == name);

    [Fact]
    public void TheSolutionExplorer_ListsEachProjectsLinkedFolders()
    {
        var explorer = new SolutionExplorerTree();

        explorer.Rebuild(_workspace);

        var projects = Assert.Single(explorer.Objects!).Children.ToList();
        var libA = projects.Single(p => p.Text.StartsWith("LibA"));
        Assert.Equal([Path.Combine("..", "include"), Path.Combine("..", "src")], libA.Children.Select(c => c.Text));
        Assert.Equal("shared.c", Assert.Single(libA.Children[1].Children).Text);
        Assert.Equal(SharedC, libA.Children[1].Children[0].Tag);
        var app = projects.Single(p => p.Text.StartsWith("App"));
        Assert.Equal("main.c", Assert.Single(Assert.Single(app.Children).Children).Text);
    }

    [Fact]
    public void ProjectFiles_IncludeTheLinkedFolders()
    {
        var files = SolutionExplorerTree.EnumerateProjectFiles(Project("LibA")).ToList();

        Assert.Contains(SharedC, files);
        Assert.Contains(Path.Combine(_root, "include", "shared.h"), files);
    }

    [Fact]
    public void AFileInASharedFolder_BelongsToTheStartupProject_ElseALibraryItLinks()
    {
        Assert.Same(Project("App"), _workspace.ProjectFor(MainC));
        // App doesn't build shared.c itself, but links LibB, which does.
        Assert.Same(Project("LibB"), _workspace.ProjectFor(SharedC));
        Assert.Same(Project("LibB"), _workspace.ProjectFor(Path.Combine(_root, "src")));

        _workspace.SetStartupProject(Project("LibA"));
        Assert.Same(Project("LibA"), _workspace.ProjectFor(SharedC));

        Assert.Null(_workspace.ProjectFor(Path.Combine(_root, "elsewhere.c")));
    }
}
