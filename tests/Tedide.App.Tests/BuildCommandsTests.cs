using Tedide.App.Views;
using Tedide.Build;
using Tedide.Core;
using Tedide.Core.Navigation;

namespace Tedide.App.Tests;

/// <summary>
/// Build, Clean and Run through <see cref="BuildCommands"/>, with .cmd stand-ins for cl65 and ar65
/// that log each call - and fail any compile of a file called broken.c - so no cc65 is needed.
/// </summary>
public sealed class BuildCommandsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-build-").FullName;
    private readonly Workspace _workspace = new();
    private readonly EditorPane _editorPane = new();
    private readonly FakeShell _shell;
    private readonly BuildCommands _build;

    public BuildCommandsTests()
    {
        _shell = new FakeShell(_editorPane);
        var tools = Path.Combine(_dir, "tools");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "cl65.cmd"), $"""
            @echo off
            >>"{Log}" echo cl65 %*
            if exist "{WaitFile}" goto wait
            :check
            echo %* | findstr /i "broken" >nul
            if errorlevel 1 exit /b 0
            echo broken.c(2): Error: Bad thing
            exit /b 1
            :wait
            ping -n 2 127.0.0.1 >nul
            if exist "{WaitFile}" goto wait
            goto check
            """.ReplaceLineEndings("\r\n"));
        File.WriteAllText(Path.Combine(tools, "ar65.cmd"), $"@>>\"{Log}\" echo ar65 %*\r\n@exit /b 0\r\n");

        var navigation = new NavigationCommands(_shell, _workspace, _editorPane, new NavigationHistory(), new ReferencesView());
        _build = new BuildCommands(_shell, _workspace, navigation, new ViceEmulator(Path.Combine(_dir, "no-vice")),
            new OutputView(), new ErrorListView(), new SolutionExplorerTree(), new SymbolPanelView(),
            new Cc65Toolchain(Path.Combine(tools, "cl65.cmd")));
    }

    public void Dispose()
    {
        File.Delete(WaitFile);
        Directory.Delete(_dir, recursive: true);
    }

    private string Log => Path.Combine(_dir, "tools.log");

    /// <summary>While this file exists, the fake cl65 waits - a build that's still running.</summary>
    private string WaitFile => Path.Combine(_dir, "wait");

    private string[] ToolCalls() => File.Exists(Log) ? File.ReadAllLines(Log) : [];

    private TedideProject AddProject(string name, string[] sources, bool library = false, params string[] references)
    {
        var directory = Path.Combine(_dir, name);
        Directory.CreateDirectory(directory);
        foreach (var source in sources)
            File.WriteAllText(Path.Combine(directory, source), "int x;\n");
        var project = new TedideProject
        {
            Name = name,
            Target = Cc65Target.C64,
            SourceFiles = [.. sources],
            OutputType = library ? ProjectOutputType.Library : ProjectOutputType.Application,
            ProjectReferences = [.. references.Select(r => $"../{r}/{r}.tproj")],
        };
        project.Save(Path.Combine(directory, name + ".tproj"));
        _workspace.Projects.Add(project);
        return project;
    }

    [Fact]
    public async Task Build_WithNoProject_SaysToOpenOne()
    {
        Assert.Null(await _build.BuildActiveProjectAsync());
        Assert.StartsWith("No project loaded.", Assert.Single(_shell.Output));
    }

    [Fact]
    public async Task Build_StopsBeforeBuilding_WhenSavingFails()
    {
        AddProject("Game", ["main.c"]);
        _shell.SaveAllSucceeds = false;

        Assert.Null(await _build.BuildActiveProjectAsync());
        Assert.Empty(ToolCalls());
    }

    [Fact]
    public async Task Build_CompilesAndLinks_AndReportsSuccess()
    {
        AddProject("Game", ["main.c"]);

        var result = await _build.BuildActiveProjectAsync();

        Assert.True(result!.Succeeded);
        Assert.Contains(ToolCalls(), call => call.StartsWith("cl65 ") && call.Contains("main.c"));
        Assert.Contains(_shell.Output, line => line.StartsWith("------ Game: build succeeded"));
    }

    [Fact]
    public async Task BuildSolution_BuildsALibraryFirst_AndLinksItIntoTheApplication()
    {
        AddProject("Game", ["main.c"], references: "Gfx");
        AddProject("Gfx", ["sprites.c"], library: true);

        var result = await _build.BuildSolutionAsync();

        Assert.True(result!.Succeeded);
        var calls = ToolCalls();
        var archive = Array.FindIndex(calls, call => call.StartsWith("ar65 "));
        var link = Array.FindLastIndex(calls, call => call.Contains("Gfx.lib"));
        Assert.True(archive >= 0 && link > archive, string.Join("\n", calls));
        Assert.Contains("========== Build: 2 succeeded, 0 failed, 0 skipped ==========", _shell.Output);
    }

    [Fact]
    public async Task AFailedLibrary_SkipsWhatUsesIt_WithItsErrorsInFullPaths()
    {
        var game = AddProject("Game", ["main.c"], references: "Gfx");
        var gfx = AddProject("Gfx", ["broken.c"], library: true);

        var result = await _build.BuildSolutionAsync();

        Assert.False(result!.Succeeded);
        Assert.Contains("------ Skipped Game: Gfx didn't build ------", _shell.Output);
        Assert.DoesNotContain(ToolCalls(), call => call.Contains("main.c"));
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(Path.Combine(gfx.Directory, "broken.c"), error.FilePath, ignoreCase: true);
        Assert.Equal("Gfx", error.Project);  // with several projects, the Error List says whose it is
        Assert.Contains("========== Build: 0 succeeded, 1 failed, 1 skipped ==========", _shell.Output);
        Assert.NotNull(game);
    }

    [Fact]
    public async Task ABadReference_BuildsNothing()
    {
        AddProject("Game", ["main.c"], references: "Missing");

        var result = await _build.BuildActiveProjectAsync();

        Assert.False(result!.Succeeded);
        Assert.Single(result.Diagnostics);
        Assert.Contains(_shell.Output, line => line.StartsWith("------ Build FAILED:"));
        Assert.Empty(ToolCalls());
    }

    [Fact]
    public async Task ASecondBuild_WaitsForTheFirst_AndCancelStopsIt()
    {
        AddProject("Game", ["main.c"]);
        File.WriteAllText(WaitFile, "");
        var first = _build.BuildActiveProjectAsync();
        while (ToolCalls().Length == 0)
            await Task.Delay(50);

        Assert.Null(await _build.BuildActiveProjectAsync());
        Assert.Contains("A build is already running - wait for it, or use Build > Cancel Build.", _shell.Output);

        _build.CancelBuild();
        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("------ Build cancelled ------", _shell.Output);

        _build.CancelBuild();
        Assert.Equal("No build is running.", _shell.Output[^1]);
    }

    [Fact]
    public async Task Run_DoesntBuildALibrary_OrLaunchAfterAFailedBuild()
    {
        var gfx = AddProject("Gfx", ["sprites.c"], library: true);

        await _build.RunActiveProjectAsync();
        Assert.StartsWith("Gfx is a library, so there's nothing to run.", _shell.Output[^1]);
        Assert.Empty(ToolCalls());

        _workspace.Projects.Remove(gfx);
        AddProject("Game", ["broken.c"]);
        await _build.RunActiveProjectAsync();
        Assert.Equal("Not launching emulator - build failed.", _shell.Output[^1]);
    }

    [Fact]
    public void Clean_DeletesBuildOutputs_AndSaysWhenThereWereNone()
    {
        var game = AddProject("Game", ["main.c"]);
        Directory.CreateDirectory(Path.GetDirectoryName(game.ResolvedObjectFileFor("main.c"))!);
        File.WriteAllText(game.ResolvedObjectFileFor("main.c"), "");

        _build.CleanSolution();
        Assert.Contains($"Deleted {Path.GetFileName(game.ResolvedObjectFileFor("main.c"))}", _shell.Output);
        Assert.False(File.Exists(game.ResolvedObjectFileFor("main.c")));

        _shell.Output.Clear();
        _build.CleanActiveProject();
        Assert.Contains("Nothing to clean.", _shell.Output);
        Assert.Equal("------ Clean complete: 0 file(s) removed ------", _shell.Output[^1]);
    }
}
