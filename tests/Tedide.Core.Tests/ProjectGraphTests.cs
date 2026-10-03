namespace Tedide.Core.Tests;

public class ProjectGraphTests
{
    /// <summary>A project at C:\sln\{name}\{name}.tproj - nothing is written to disk.</summary>
    private static TedideProject Project(string name, bool library = false, params string[] references) => new()
    {
        Name = name,
        OutputType = library ? ProjectOutputType.Library : ProjectOutputType.Application,
        ProjectReferences = references.Select(r => $@"..\{r}\{r}.tproj").ToList(),
        FilePath = $@"C:\sln\{name}\{name}.tproj",
    };

    [Fact]
    public void BuildOrder_PutsEachLibraryBeforeWhatUsesIt_AndOnlyWhatTheRootsNeed()
    {
        var game = Project("Game", false, "Gfx", "Sound");
        var gfx = Project("Gfx", true, "Core");
        var sound = Project("Sound", true, "Core");
        var core = Project("Core", true);
        var tool = Project("Tool");
        TedideProject[] solution = [game, tool, gfx, sound, core];

        Assert.Equal([core, gfx, sound, game], ProjectGraph.BuildOrder(solution, [game]));
        Assert.Equal([core, gfx, sound, game, tool], ProjectGraph.BuildOrder(solution, solution));
    }

    [Fact]
    public void LinkedLibraries_ListEachLibraryBeforeTheOnesItUses()
    {
        // Game lists Core first, but Gfx uses Core - so ld65 must see Gfx before Core.
        var game = Project("Game", false, "Core", "Gfx");
        var gfx = Project("Gfx", true, "Core");
        var core = Project("Core", true);
        TedideProject[] solution = [game, gfx, core];

        Assert.Equal([gfx, core], ProjectGraph.LinkedLibraries(solution, game));
        Assert.Empty(ProjectGraph.LinkedLibraries(solution, core));
    }

    [Fact]
    public void BadReferences_AreReportedByName()
    {
        var game = Project("Game", false, "Missing");
        Assert.Contains("Game references Missing.tproj, which isn't in the solution",
            Assert.Throws<InvalidDataException>(() => ProjectGraph.BuildOrder([game], [game])).Message);

        var tool = Project("Tool");
        var user = Project("User", false, "Tool");
        Assert.Contains("User references Tool, which isn't a library project",
            Assert.Throws<InvalidDataException>(() => ProjectGraph.BuildOrder([user, tool], [user])).Message);

        var a = Project("A", true, "B");
        var b = Project("B", true, "A");
        Assert.Contains("cycle: A -> B -> A",
            Assert.Throws<InvalidDataException>(() => ProjectGraph.BuildOrder([a, b], [a])).Message);
    }

    [Fact]
    public void ReferenceCandidates_AreOtherLibraries_ThatWouldNotMakeACycle()
    {
        var game = Project("Game", false, "Gfx");
        var gfx = Project("Gfx", true, "Core");
        var core = Project("Core", true);
        var sound = Project("Sound", true);
        TedideProject[] solution = [game, gfx, core, sound];

        Assert.Equal([gfx, core, sound], ProjectGraph.ReferenceCandidates(solution, game));
        // Core can't reference Gfx, which already uses it.
        Assert.Equal([sound], ProjectGraph.ReferenceCandidates(solution, core));
        Assert.True(ProjectGraph.DependsOn(solution, game, core));
        Assert.False(ProjectGraph.DependsOn(solution, core, game));
    }

    [Fact]
    public void RetargetReference_FollowsARename_OrDropsTheReference()
    {
        var game = Project("Game", false, "Gfx");
        Assert.True(game.References(@"C:\sln\Gfx\Gfx.tproj"));

        Assert.True(game.RetargetReference(@"C:\sln\Gfx\Gfx.tproj", @"C:\sln\Graphics\Graphics.tproj"));
        Assert.Equal([@"..\Graphics\Graphics.tproj"], game.ProjectReferences);

        Assert.False(game.RetargetReference(@"C:\sln\Other\Other.tproj", null));
        Assert.True(game.RetargetReference(@"C:\sln\Graphics\Graphics.tproj", null));
        Assert.Empty(game.ProjectReferences);
    }

    [Fact]
    public void ALibrary_DefaultsToADotLibOutput()
    {
        var library = Project("Gfx", true);
        Assert.Equal(@"C:\sln\Gfx\Gfx.lib", library.ResolvedOutputFile);
        Assert.Equal(@"C:\sln\Game\Game.prg", Project("Game").ResolvedOutputFile);
    }

    [Fact]
    public void AProjectFileFromBeforeLibraries_LoadsAsAnApplicationWithNoReferences()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "Old.tproj");
            File.WriteAllText(path, """{ "Name": "Old", "Target": "C64", "SourceFiles": ["src/main.c"] }""");
            var project = TedideProject.Load(path);
            Assert.Equal(ProjectOutputType.Application, project.OutputType);
            Assert.Empty(project.ProjectReferences);

            project.OutputType = ProjectOutputType.Library;
            project.ProjectReferences = [@"..\Core\Core.tproj"];
            project.Save();
            var reloaded = TedideProject.Load(path);
            Assert.True(reloaded.IsLibrary);
            Assert.Equal([@"..\Core\Core.tproj"], reloaded.ProjectReferences);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RemovingTheStartupProject_ClearsIt()
    {
        var solution = new TedideSolution { FilePath = @"C:\sln\Demo.tsln", ProjectPaths = [@"Game\Game.tproj", @"Gfx\Gfx.tproj"], StartupProject = @"Game\Game.tproj" };
        solution.RemoveProject(Project("Gfx", true));
        Assert.Equal([@"Game\Game.tproj"], solution.ProjectPaths);
        Assert.Equal(@"Game\Game.tproj", solution.StartupProject);

        solution.RemoveProject(Project("Game"));
        Assert.Empty(solution.ProjectPaths);
        Assert.Null(solution.StartupProject);
    }
}
