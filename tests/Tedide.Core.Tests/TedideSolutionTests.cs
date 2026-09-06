using Tedide.Core;

namespace Tedide.Core.Tests;

public class TedideSolutionTests
{
    [Fact]
    public void AddProject_StoresPathRelativeToSolutionDirectory()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var solutionPath = Path.Combine(dir.FullName, "Game.tsln");
            var solution = new TedideSolution { Name = "Game" };
            solution.Save(solutionPath);

            var projectDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "MyGame"));
            var project = new TedideProject { Name = "MyGame" };
            project.Save(Path.Combine(projectDir.FullName, "MyGame.tproj"));

            solution.AddProject(project);

            Assert.Contains(Path.Combine("MyGame", "MyGame.tproj"), solution.ProjectPaths);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadProjects_ResolvesEachReferencedProject()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var solution = new TedideSolution { Name = "Game" };
            solution.Save(Path.Combine(dir.FullName, "Game.tsln"));

            var project = new TedideProject { Name = "MyGame", Target = Cc65Target.Apple2 };
            project.Save(Path.Combine(dir.FullName, "MyGame.tproj"));
            solution.AddProject(project);
            solution.Save();

            var reloaded = TedideSolution.Load(solution.FilePath!);
            var projects = reloaded.LoadProjects();

            Assert.Single(projects);
            Assert.Equal("MyGame", projects[0].Name);
            Assert.Equal(Cc65Target.Apple2, projects[0].Target);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
