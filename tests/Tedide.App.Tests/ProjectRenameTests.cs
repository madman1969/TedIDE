using Tedide.Core;

namespace Tedide.App.Tests;

public class ProjectRenameTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory();

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void Apply_ToTheSampleLayout_MovesEverything_AndLeavesASolutionThatReopens()
    {
        // Foo/Foo.tsln beside Foo/Foo.tproj - every sample's and every New Project's layout. This
        // exact case used to crash (the solution was saved to its old, moved-away path) and leave a
        // .tsln pointing at a .tproj name that no longer existed.
        var (project, solution) = CreateProject("Foo", solutionBeside: true);
        File.WriteAllText(Path.Combine(_root.FullName, "Foo", "Foo.breakpoints.json"), "{}");
        File.WriteAllText(Path.Combine(_root.FullName, "Foo", "Foo.session.json"), "{}");
        var oldProjectFile = project.FilePath!;

        project.Name = "Bar";
        project.Save();
        var result = ProjectRename.Apply(project, solution, "Foo", oldProjectFile, renameFolder: true);

        var bar = Path.Combine(_root.FullName, "Bar");
        Assert.False(Directory.Exists(Path.Combine(_root.FullName, "Foo")));
        Assert.Null(result.FolderNotRenamedReason);
        Assert.Equal(Path.Combine(bar, "Bar.tproj"), result.NewProjectFile);
        Assert.Equal(Path.Combine(bar, "Bar.tsln"), result.NewSolutionFile);
        Assert.True(File.Exists(Path.Combine(bar, "Bar.breakpoints.json")));
        Assert.True(File.Exists(Path.Combine(bar, "Bar.session.json")));

        var reopened = TedideSolution.Load(result.NewSolutionFile!);
        Assert.Equal("Bar", reopened.Name);
        Assert.Equal(["Bar.tproj"], reopened.ProjectPaths);
        Assert.Equal("Bar", Assert.Single(reopened.LoadProjects()).Name);
    }

    [Fact]
    public void Apply_LeavesAFolderThatIsNotNamedAfterTheProject_WhereItIs()
    {
        var (project, solution) = CreateProject("Foo", solutionBeside: true, folderName: "MyStuff");
        var oldProjectFile = project.FilePath!;

        project.Name = "Bar";
        project.Save();
        var result = ProjectRename.Apply(project, solution, "Foo", oldProjectFile, renameFolder: true);

        var myStuff = Path.Combine(_root.FullName, "MyStuff");
        Assert.True(Directory.Exists(myStuff));
        Assert.Null(result.FolderNotRenamedReason); // Not a failure - it was never going to be renamed.
        Assert.Equal(Path.Combine(myStuff, "Bar.tproj"), result.NewProjectFile);
        Assert.Equal("Bar", Assert.Single(TedideSolution.Load(result.NewSolutionFile!).LoadProjects()).Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_RenamesFilesInPlace_AndSaysWhy_WhenTheFolderCannotBeRenamed(bool targetExists)
    {
        var (project, solution) = CreateProject("Foo", solutionBeside: true);
        if (targetExists)
            Directory.CreateDirectory(Path.Combine(_root.FullName, "Bar"));
        var oldProjectFile = project.FilePath!;

        project.Name = "Bar";
        project.Save();
        // renameFolder: false is how AppShell passes on "the open file has unsaved changes".
        var result = ProjectRename.Apply(project, solution, "Foo", oldProjectFile, renameFolder: targetExists);

        var foo = Path.Combine(_root.FullName, "Foo");
        Assert.NotNull(result.FolderNotRenamedReason);
        Assert.Equal(Path.Combine(foo, "Bar.tproj"), result.NewProjectFile);
        Assert.Equal("Bar", Assert.Single(TedideSolution.Load(result.NewSolutionFile!).LoadProjects()).Name);
    }

    [Fact]
    public void Apply_RepointsAMultiProjectSolutionInTheParentFolder_WithoutRenamingIt()
    {
        var (project, _) = CreateProject("Foo", solutionBeside: false);
        CreateProject("Other", solutionBeside: false);
        var solution = new TedideSolution { Name = "Games", ProjectPaths = ["Foo/Foo.tproj", "Other/Other.tproj"] };
        solution.Save(Path.Combine(_root.FullName, "Games.tsln"));
        var oldProjectFile = project.FilePath!;

        project.Name = "Bar";
        project.Save();
        var result = ProjectRename.Apply(project, solution, "Foo", oldProjectFile, renameFolder: true);

        Assert.Equal(Path.Combine(_root.FullName, "Games.tsln"), result.NewSolutionFile);
        var reopened = TedideSolution.Load(result.NewSolutionFile!);
        Assert.Equal("Games", reopened.Name);
        Assert.Equal(["Bar/Bar.tproj", "Other/Other.tproj"], reopened.ProjectPaths);
        Assert.Equal(["Bar", "Other"], reopened.LoadProjects().Select(p => p.Name));
    }

    [Fact]
    public void Apply_WorksForABareProjectWithNoSolution()
    {
        var (project, _) = CreateProject("Foo", solutionBeside: false);
        var oldProjectFile = project.FilePath!;

        project.Name = "Bar";
        project.Save();
        var result = ProjectRename.Apply(project, solution: null, "Foo", oldProjectFile, renameFolder: true);

        Assert.Null(result.NewSolutionFile);
        Assert.Equal("Bar", TedideProject.Load(result.NewProjectFile).Name);
    }

    [Theory]
    [InlineData(typeof(IOException), true)]
    [InlineData(typeof(UnauthorizedAccessException), true)]
    [InlineData(typeof(System.Text.Json.JsonException), true)]
    [InlineData(typeof(ArgumentException), true)]
    [InlineData(typeof(ArgumentNullException), false)]
    [InlineData(typeof(NullReferenceException), false)]
    public void IsFileError_CoversOutsideWorldFailures_ButNotProgrammingErrors(Type exceptionType, bool expected)
    {
        Assert.Equal(expected, AppShell.IsFileError((Exception)Activator.CreateInstance(exceptionType)!));
    }

    [Theory]
    [InlineData(@"C:\Games\Game", @"C:\Games\Game", true)]
    [InlineData(@"C:\Games\Game\src", @"C:\Games\Game", true)]
    [InlineData(@"C:\Games\game\SRC", @"C:\Games\Game\", true)]
    [InlineData(@"C:\Games\GameTools\src", @"C:\Games\Game", false)] // shares the name as a prefix - the old bug
    [InlineData(@"C:\Games", @"C:\Games\Game", false)]
    public void IsSameOrInsideDirectory_MatchesWholeFolderNames_NotPrefixes(string path, string directory, bool expected)
    {
        Assert.Equal(expected, ProjectCommands.IsSameOrInsideDirectory(path, directory));
    }

    private (TedideProject Project, TedideSolution? Solution) CreateProject(string name, bool solutionBeside, string? folderName = null)
    {
        var directory = Path.Combine(_root.FullName, folderName ?? name);
        Directory.CreateDirectory(directory);
        var project = new TedideProject { Name = name, SourceFiles = ["src/main.c"] };
        project.Save(Path.Combine(directory, name + TedideProject.FileExtension));
        if (!solutionBeside)
            return (project, null);

        var solution = new TedideSolution { Name = name, ProjectPaths = [name + TedideProject.FileExtension] };
        solution.Save(Path.Combine(directory, name + TedideSolution.FileExtension));
        return (project, solution);
    }
}
