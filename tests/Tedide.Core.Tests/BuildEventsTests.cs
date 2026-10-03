namespace Tedide.Core.Tests;

public class BuildEventsTests
{
    private static TedideProject Project()
    {
        var project = new TedideProject { Name = "Game", Target = Cc65Target.C64, OutputFile = "bin/game.prg" };
        project.FilePath = Path.Combine(Path.GetTempPath(), "proj", "Game.tproj");
        return project;
    }

    [Fact]
    public void Expand_SubstitutesEveryMacro_CaseInsensitively()
    {
        var project = Project();
        var directory = project.Directory;

        Assert.Equal($"{directory} Game {Path.Combine(directory, "bin/game.prg")} {Path.Combine(directory, "bin")} game c64",
            BuildEvents.Expand(project, "$(ProjectDir) $(ProjectName) $(OutputFile) $(outputdir) $(OUTPUTNAME) $(Target)"));
    }

    [Theory]
    [InlineData("echo $(Unknown) stays", "echo $(Unknown) stays")]
    [InlineData("echo $(unclosed", "echo $(unclosed")]
    [InlineData("plain $ and ( text", "plain $ and ( text")]
    [InlineData("", "")]
    public void Expand_LeavesAnythingElseAlone(string command, string expected)
    {
        Assert.Equal(expected, BuildEvents.Expand(Project(), command));
    }

    [Fact]
    public void Macros_AreAllDocumentedAndExpand()
    {
        Assert.All(BuildEvents.Macros, m => Assert.DoesNotContain("$(", BuildEvents.Expand(Project(), m.Macro)));
    }

    [Fact]
    public void Commands_RoundTripThroughTheProjectFile()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "Game.tproj");
            new TedideProject { Name = "Game", PreBuildCommands = ["a"], PostBuildCommands = ["b \"c d\"", "e"] }.Save(path);

            var loaded = TedideProject.Load(path);

            Assert.Equal(["a"], loaded.PreBuildCommands);
            Assert.Equal(["b \"c d\"", "e"], loaded.PostBuildCommands);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
