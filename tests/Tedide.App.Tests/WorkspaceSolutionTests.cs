using Tedide.Core;

namespace Tedide.App.Tests;

public class WorkspaceSolutionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AddNewProject_ScaffoldsALibrary_AndAddsItToTheSolution()
    {
        var workspace = new Workspace();
        workspace.NewProject(Path.Combine(_directory, "Game"), "Game", Cc65Target.C64);

        var library = workspace.AddNewProject(Path.Combine(_directory, "My Gfx"), "My Gfx", Cc65Target.C64, ProjectOutputType.Library);

        Assert.True(library.IsLibrary);
        Assert.Equal(["src/my_gfx.c"], library.SourceFiles);
        Assert.Equal("bin/My Gfx.lib", library.OutputFile);
        Assert.Contains("my_gfx_add", File.ReadAllText(Path.Combine(library.Directory, "include", "my_gfx.h")));
        Assert.Contains("MY_GFX_H", File.ReadAllText(Path.Combine(library.Directory, "include", "my_gfx.h")));

        var reloaded = TedideSolution.Load(workspace.Solution!.FilePath!);
        Assert.Equal(2, reloaded.ProjectPaths.Count);
        Assert.Equal(2, reloaded.LoadProjects().Count);
    }

    [Fact]
    public void TheStartupProject_IsTheChosenOne_ElseTheFirstApplication()
    {
        var workspace = new Workspace();
        var game = workspace.NewProject(Path.Combine(_directory, "Game"), "Game", Cc65Target.C64);
        var library = workspace.AddNewProject(Path.Combine(_directory, "Gfx"), "Gfx", Cc65Target.C64, ProjectOutputType.Library);
        var tool = workspace.AddNewProject(Path.Combine(_directory, "Tool"), "Tool", Cc65Target.C64, ProjectOutputType.Application);
        Assert.Same(game, workspace.ActiveProject);

        workspace.SetStartupProject(tool);
        Assert.Same(tool, workspace.ActiveProject);

        // Remembered in the solution file.
        var reopened = new Workspace();
        reopened.OpenSolution(workspace.Solution!.FilePath!);
        Assert.Equal("Tool", reopened.ActiveProject!.Name);

        workspace.RemoveProject(tool);
        Assert.Same(game, workspace.ActiveProject);
        Assert.NotSame(library, workspace.ActiveProject);
    }

    [Fact]
    public void RemoveProject_DropsOtherProjectsReferencesToIt()
    {
        var workspace = new Workspace();
        var game = workspace.NewProject(Path.Combine(_directory, "Game"), "Game", Cc65Target.C64);
        var library = workspace.AddNewProject(Path.Combine(_directory, "Gfx"), "Gfx", Cc65Target.C64, ProjectOutputType.Library);
        game.ProjectReferences = [Path.GetRelativePath(game.Directory, library.FilePath!)];
        game.Save();

        workspace.RemoveProject(library);

        Assert.Empty(TedideProject.Load(game.FilePath!).ProjectReferences);
        Assert.True(File.Exists(library.FilePath)); // its files stay
    }

    [Fact]
    public void AddExistingProject_RefusesOneAlreadyThere()
    {
        var workspace = new Workspace();
        var game = workspace.NewProject(Path.Combine(_directory, "Game"), "Game", Cc65Target.C64);
        Assert.Throws<InvalidDataException>(() => workspace.AddExistingProject(game.FilePath!));
    }

    [Fact]
    public void ProjectFor_FindsTheProjectHoldingAFile()
    {
        var workspace = new Workspace();
        var game = workspace.NewProject(Path.Combine(_directory, "Game"), "Game", Cc65Target.C64);
        var library = workspace.AddNewProject(Path.Combine(_directory, "Gfx"), "Gfx", Cc65Target.C64, ProjectOutputType.Library);

        Assert.Same(library, workspace.ProjectFor(Path.Combine(library.Directory, "src", "gfx.c")));
        Assert.Same(game, workspace.ProjectFor(Path.Combine(game.Directory, "src", "main.c")));
        Assert.Null(workspace.ProjectFor(Path.Combine(_directory, "elsewhere.c")));
    }

    [Fact]
    public void ANewProject_DefaultsToBesideTheProjectWhoseFolderHoldsTheSolution()
    {
        // Like every sample: Game.tsln sits in Game's own folder.
        var workspace = new Workspace();
        workspace.NewProject(Path.Combine(_directory, "Game"), "Game", Cc65Target.C64);
        Assert.Equal(_directory, workspace.DefaultNewProjectParent());

        // A solution file in a folder of its own keeps new projects beside it.
        var solution = new TedideSolution { Name = "Suite" };
        solution.Save(Path.Combine(_directory, "Suite.tsln"));
        solution.AddProject(TedideProject.Load(Path.Combine(_directory, "Game", "Game.tproj")));
        solution.Save();
        var suite = new Workspace();
        suite.OpenSolution(solution.FilePath!);
        Assert.Equal(_directory, suite.DefaultNewProjectParent());
    }

    [Fact]
    public void AProjectInsideAnothersFolder_IsShownOnlyAsItsOwnProject()
    {
        var workspace = new Workspace();
        var game = workspace.NewProject(Path.Combine(_directory, "Game"), "Game", Cc65Target.C64);
        workspace.AddNewProject(Path.Combine(game.Directory, "Gfx"), "Gfx", Cc65Target.C64, ProjectOutputType.Library);
        var explorer = new Views.SolutionExplorerTree();

        explorer.Rebuild(workspace);

        var projects = Assert.Single(explorer.Objects!).Children.ToList();
        Assert.Equal(2, projects.Count);
        Assert.DoesNotContain(projects[0].Children, n => n.Text == "Gfx");
        Assert.DoesNotContain(Views.SolutionExplorerTree.EnumerateFiles(game.Directory), f => f.Contains(Path.Combine("Gfx", "src")));
    }

    [Theory]
    [InlineData("Gfx", "gfx")]
    [InlineData("My-Lib 2", "my_lib_2")]
    [InlineData("8bit", "_8bit")]
    public void CIdentifier_MakesANameUsableInC(string name, string expected)
    {
        Assert.Equal(expected, Workspace.CIdentifier(name));
    }
}
