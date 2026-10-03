using Tedide.Core;

namespace Tedide.App.Tests;

public class ProjectDeletionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AProjectBesideTheOthers_CanBeDeleted()
    {
        var workspace = new Workspace();
        var game = workspace.NewProject(Path.Combine(_directory, "Game"), "Game", Cc65Target.C64);
        var library = workspace.AddNewProject(Path.Combine(_directory, "Gfx"), "Gfx", Cc65Target.C64, ProjectOutputType.Library);

        Assert.Null(ProjectDeletion.WhyNot(workspace, library));
        // Game's folder holds the solution file (as every sample's does), so deleting it would take that too.
        Assert.Contains("also holds the solution file (Game.tsln)", ProjectDeletion.WhyNot(workspace, game));
    }

    [Fact]
    public void AProjectHoldingAnotherProject_CannotBeDeleted()
    {
        var solution = new TedideSolution { Name = "Suite" };
        solution.Save(Path.Combine(_directory, "Suite.tsln"));
        var workspace = new Workspace();
        workspace.OpenSolution(solution.FilePath!);
        var outer = workspace.AddNewProject(Path.Combine(_directory, "Outer"), "Outer", Cc65Target.C64, ProjectOutputType.Application);
        workspace.AddNewProject(Path.Combine(outer.Directory, "Inner"), "Inner", Cc65Target.C64, ProjectOutputType.Library);

        Assert.Contains("also holds the Inner project", ProjectDeletion.WhyNot(workspace, outer));
    }

    [Fact]
    public void ABareProject_CannotBeDeleted()
    {
        var workspace = new Workspace();
        var created = workspace.NewProject(Path.Combine(_directory, "Game"), "Game", Cc65Target.C64);
        var bare = new Workspace();
        var project = bare.OpenProject(created.FilePath!);

        Assert.Contains("Only a project in a solution", ProjectDeletion.WhyNot(bare, project));
    }
}
