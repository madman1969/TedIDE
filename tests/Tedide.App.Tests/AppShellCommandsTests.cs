using Tedide.App.Views;
using Tedide.Core;
using Terminal.Gui.Views;

namespace Tedide.App.Tests;

/// <summary>
/// The window's own project and file commands - New/Open Project, the recent list, new, added,
/// renamed and deleted files, saving and closing, and the solution's projects - through a real
/// <see cref="AppShell"/>, with its settings files in a scratch folder and its dialogs answered by
/// the test.
/// </summary>
public sealed class AppShellCommandsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-shell-").FullName;
    private readonly FakeDialogs _dialogs = new();
    private readonly AppShellSettings _settings;
    private AppShell? _shellInstance;

    /// <summary>Made on first use - inside the test, on its UI thread (see <see cref="UiThread"/>),
    /// which the window takes as the thread to report on.</summary>
    private AppShell _shell => _shellInstance ??= new AppShell(_settings, _dialogs);

    public AppShellCommandsTests()
    {
        var settings = Path.Combine(_dir, "settings");
        Directory.CreateDirectory(settings);
        // Whatever CC65_HOME is now, so a Project Settings save leaves this process's alone.
        new ToolchainSettings { Cc65Home = Environment.GetEnvironmentVariable("CC65_HOME") }.Save(Path.Combine(settings, "toolchain.json"));
        _settings = new AppShellSettings(
            Path.Combine(settings, "recent.json"), Path.Combine(settings, "layout.json"), Path.Combine(settings, "toolchain.json"),
            Path.Combine(settings, "editor.json"));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private string GameDir => Path.Combine(_dir, "Game");
    private string Solution => Path.Combine(GameDir, "Game.tsln");
    private string MainC => Path.Combine(GameDir, "src", "main.c");
    private TedideProject Game => _shell.Workspace.Projects.Single(p => p.Name == "Game");

    /// <summary>File > New Project, answered with an application called Game.</summary>
    private void NewGame()
    {
        _dialogs.Answer = dialog =>
        {
            var newProject = Assert.IsType<NewProjectDialog>(dialog);
            newProject._nameField.Text = "Game";
            newProject._directoryField.Text = GameDir;
            newProject.Target = Cc65Target.C64;
            newProject.OutputType = ProjectOutputType.Application;
        };
        _shell.Projects.NewProject();
        _dialogs.Answer = null;
    }

    private void AnswerWith<T>(Action<T> answer) where T : Dialog => _dialogs.Answer = dialog => answer(Assert.IsType<T>(dialog));

    [Fact]
    public Task NewProject_CreatesItInASolution_AndRemembersIt() => UiThread.Run(() =>
    {
        NewGame();

        Assert.Equal("Game", _shell.Workspace.ActiveProject!.Name);
        Assert.True(File.Exists(MainC));
        Assert.Equal([Solution], _shell.RecentProjects.Paths);

        _shell.Projects.NewProject();  // cancelled
        Assert.Single(_shell.Workspace.Projects);
        return Task.CompletedTask;
    });

    [Fact]
    public Task OpenFile_OpensStrayFiles_WithOrWithoutAProject() => UiThread.Run(async () =>
    {
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var stray = Path.Combine(elsewhere, "stray.c");
        var strayHeader = Path.Combine(elsewhere, "stray.h");
        File.WriteAllText(stray, "int x;\n");
        File.WriteAllText(strayHeader, "extern int x;\n");

        // With nothing loaded.
        _dialogs.FilePicks.Enqueue([stray, strayHeader]);
        _shell.Projects.OpenFiles();
        Assert.Equal([stray, strayHeader], _shell.EditorPane.OpenPaths);
        Assert.Empty(_shell.Workspace.Projects);

        _shell.Projects.OpenFiles();  // cancelled
        Assert.Equal(2, _shell.EditorPane.OpenPaths.Count);

        // With a project open, beside its own files - and not added to it.
        _shell.Projects.CloseAllFiles();
        NewGame();
        _dialogs.FilePicks.Enqueue([stray]);
        _shell.Projects.OpenFiles();
        Assert.Equal(stray, _shell.EditorPane.OpenPath);
        Assert.DoesNotContain(Game.SourceFiles, f => f.Contains("stray"));
        // Code completion indexes the project's files in the background, and a refresh asked for
        // during one runs straight after it: let them all finish before the folder is deleted.
        Task refresh;
        do
        {
            refresh = _shell.Completion.LastRefresh;
            await refresh;
            await Task.Yield();
        }
        while (refresh != _shell.Completion.LastRefresh);
    });

    [Fact]
    public Task Opening_ReopensTheTabsAndBreakpointsLeftLastTime() => UiThread.Run(() =>
    {
        NewGame();
        _shell.Projects.OpenFile(MainC);
        _shell.Projects.CloseSolution();
        Assert.Empty(_shell.Workspace.Projects);
        Assert.Empty(_shell.EditorPane.OpenPaths);
        new BreakpointsFile { Breakpoints = [new BreakpointEntry("src/main.c", 3)] }.Save(Path.Combine(GameDir, "Game.breakpoints.json"));

        _shell.Projects.OpenProjectOrSolution(Solution);

        Assert.Equal([MainC], _shell.EditorPane.OpenPaths);
        Assert.Single(_shell.Debug.Breakpoints.Breakpoints);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Opening_AMissingOrUnsupportedFile_IsReported() => UiThread.Run(() =>
    {
        NewGame();
        var gone = Path.Combine(_dir, "Gone.tsln");
        _shell.RecentProjects.Touch(gone);

        _shell.Projects.OpenProjectOrSolution(gone);
        _shell.Projects.OpenProjectOrSolution(MainC);

        Assert.Equal([$"File Not Found: '{gone}' no longer exists.", "Unsupported file: Expected a .tproj or .tsln file."], _dialogs.Errors);
        Assert.DoesNotContain(gone, _shell.RecentProjects.Paths);
        Assert.Equal("Game", _shell.Workspace.ActiveProject!.Name);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(2, false)]  // Cancel: stays on the open project
    [InlineData(1, true)]   // Discard
    [InlineData(0, true)]   // Save
    public Task Opening_AnotherProject_AsksAboutUnsavedEdits(int answer, bool switches) => UiThread.Run(() =>
    {
        NewGame();
        var other = new TedideProject { Name = "Other", Target = Cc65Target.Vic20 };
        Directory.CreateDirectory(Path.Combine(_dir, "Other"));
        other.Save(Path.Combine(_dir, "Other", "Other.tproj"));
        _shell.Projects.OpenFile(MainC);
        _shell.EditorPane.Editor.Document!.Insert(0, "// edited\n");
        _dialogs.QueryAnswers.Enqueue(answer);

        _shell.Projects.OpenProjectOrSolution(other.FilePath!);

        Assert.StartsWith("Unsaved Changes: Save changes to main.c?", _dialogs.Messages[^1]);
        Assert.Equal(switches ? "Other" : "Game", _shell.Workspace.ActiveProject!.Name);
        Assert.Equal(answer == 0, File.ReadAllText(MainC).StartsWith("// edited"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheRecentMenu_ListsWhatsThere_NumberedForAltKeys() => UiThread.Run(() =>
    {
        Assert.Equal("(No Recent Projects or Solutions)", Assert.Single(_shell.BuildRecentProjectsMenuItems()).Title);

        NewGame();
        _shell.RecentProjects.Touch(Path.Combine(_dir, "Moved.tsln"));

        Assert.Equal("_1 Game.tsln", Assert.Single(_shell.BuildRecentProjectsMenuItems()).Title);
        return Task.CompletedTask;
    });

    [Fact]
    public Task NewFile_InALinkedFolder_AddsItToTheProjectWithItsRelativePath() => UiThread.Run(() =>
    {
        NewGame();
        var shared = Path.Combine(_dir, "shared");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "util.c"), "");
        Game.SourceFiles.Add("../shared/util.c");
        Game.Save();

        // From the folder's node under Game, and with no project named (found from the folder).
        AnswerWith<NewFileDialog>(d => d.FileName = "more.c");
        _shell.Projects.NewFile(shared, Game);
        AnswerWith<NewFileDialog>(d => d.FileName = "most.c");
        _shell.Projects.NewFile(shared);

        Assert.Equal(["src/main.c", "../shared/util.c", "../shared/more.c", "../shared/most.c"], TedideProject.Load(Game.FilePath!).SourceFiles);
        return Task.CompletedTask;
    });

    [Fact]
    public Task NewFile_GivesAHeaderAGuard_AndAddsASourceFileToTheProject() => UiThread.Run(() =>
    {
        NewGame();

        AnswerWith<NewFileDialog>(d => d.FileName = "screen.h");
        _shell.Projects.NewFile(Path.Combine(GameDir, "include"));
        AnswerWith<NewFileDialog>(d => d.FileName = "screen.c");
        _shell.Projects.NewFile(Path.Combine(GameDir, "src"));
        AnswerWith<NewFileDialog>(d => d.FileName = "screen.c");
        _shell.Projects.NewFile(Path.Combine(GameDir, "src"));

        Assert.Equal("#ifndef SCREEN_H\n#define SCREEN_H\n\n#endif\n", File.ReadAllText(Path.Combine(GameDir, "include", "screen.h")));
        Assert.Equal(["src/main.c", "src/screen.c"], TedideProject.Load(Game.FilePath!).SourceFiles);
        Assert.Equal(Path.Combine(GameDir, "src", "screen.c"), _shell.EditorPane.OpenPath);
        Assert.Equal(["File Exists: 'screen.c' already exists."], _dialogs.Errors);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AddExistingItem_CopiesTheFilesIn_SkippingOnesAlreadyThere() => UiThread.Run(() =>
    {
        NewGame();
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        foreach (var name in (string[])["sound.c", "sound.h", "main.c"])
            File.WriteAllText(Path.Combine(elsewhere, name), "// " + name);
        _dialogs.FilePicks.Enqueue([.. new[] { "sound.c", "sound.h", "main.c" }.Select(n => Path.Combine(elsewhere, n))]);

        _shell.Projects.AddExistingItem(Path.Combine(GameDir, "src"));

        Assert.True(File.Exists(Path.Combine(GameDir, "src", "sound.h")));
        Assert.Equal(["src/main.c", "src/sound.c"], TedideProject.Load(Game.FilePath!).SourceFiles);
        Assert.Equal("Some Files Skipped: Already exists in this folder, skipped:\nmain.c", Assert.Single(_dialogs.Errors));
        Assert.NotEqual("// main.c", File.ReadAllText(MainC));
        return Task.CompletedTask;
    });

    [Fact]
    public Task RenameFile_MovesItsTab_ItsProjectEntry_AndItsBreakpoints() => UiThread.Run(() =>
    {
        NewGame();
        _shell.Projects.OpenFile(MainC);
        _shell.Debug.Breakpoints.Breakpoints.Add(new BreakpointEntry("src/main.c", 3));
        var renamed = Path.Combine(GameDir, "src", "game.c");

        AnswerWith<RenameFileDialog>(d => d.NewFileName = "game.c");
        _shell.Projects.RenameFile(MainC);

        Assert.True(File.Exists(renamed) && !File.Exists(MainC));
        Assert.Equal(["src/game.c"], TedideProject.Load(Game.FilePath!).SourceFiles);
        Assert.Equal(renamed, _shell.EditorPane.OpenPath);
        Assert.Equal("src/game.c", Assert.Single(_shell.Debug.Breakpoints.Breakpoints).SourceFile);

        File.WriteAllText(MainC, "");
        AnswerWith<RenameFileDialog>(d => d.NewFileName = "main.c");
        _shell.Projects.RenameFile(renamed);
        Assert.Equal(["File Exists: 'main.c' already exists."], _dialogs.Errors);
        return Task.CompletedTask;
    });

    [Fact]
    public Task DeleteFile_AsksFirst_ThenRemovesItEverywhere() => UiThread.Run(() =>
    {
        NewGame();
        _shell.Projects.OpenFile(MainC);
        _shell.Debug.Breakpoints.Breakpoints.Add(new BreakpointEntry("src/main.c", 3));

        _shell.Projects.DeleteFile(MainC);  // cancelled
        Assert.True(File.Exists(MainC));

        _dialogs.QueryAnswers.Enqueue(0);
        _shell.Projects.DeleteFile(MainC);

        Assert.False(File.Exists(MainC));
        Assert.Empty(TedideProject.Load(Game.FilePath!).SourceFiles);
        Assert.Empty(_shell.EditorPane.OpenPaths);
        Assert.Empty(_shell.Debug.Breakpoints.Breakpoints);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SaveAll_WritesTheEditsAndTheProject_AndCloseFileAsksFirst() => UiThread.Run(() =>
    {
        NewGame();
        _shell.Projects.OpenFile(MainC);
        _shell.EditorPane.Editor.Document!.Insert(0, "// one\n");
        Game.ExtraArguments = ["-v"];

        Assert.True(_shell.Projects.SaveAll());
        Assert.StartsWith("// one", File.ReadAllText(MainC));
        Assert.Equal(["-v"], TedideProject.Load(Game.FilePath!).ExtraArguments);

        _shell.EditorPane.Editor.Document!.Insert(0, "// two\n");
        Assert.False(_shell.Projects.CloseFile(MainC));  // the question was closed: nothing happens
        _dialogs.QueryAnswers.Enqueue(1);
        Assert.True(_shell.Projects.CloseFile(MainC));    // Discard
        Assert.Empty(_shell.EditorPane.OpenPaths);
        Assert.StartsWith("// one", File.ReadAllText(MainC));
        return Task.CompletedTask;
    });

    [Fact]
    public Task SavingAReadOnlyFile_IsReported_NotThrown() => UiThread.Run(() =>
    {
        NewGame();
        _shell.Projects.OpenFile(MainC);
        _shell.EditorPane.Editor.Document!.Insert(0, "// edited\n");
        File.SetAttributes(MainC, FileAttributes.ReadOnly);

        Assert.False(_shell.Projects.SaveActive());

        Assert.StartsWith("Error: Saving main.c failed:", Assert.Single(_dialogs.Errors));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheSolutionsProjects_CanBeAddedMadeStartupAndRemoved() => UiThread.Run(() =>
    {
        NewGame();
        AnswerWith<NewProjectDialog>(d =>
        {
            d._nameField.Text = "Gfx";
            d._directoryField.Text = Path.Combine(_dir, "Gfx");
            d.Target = Cc65Target.C64;
            d.OutputType = ProjectOutputType.Library;
        });
        _shell.Projects.AddNewProject();
        Assert.Contains("Added Gfx (library) to the solution.", _shell.OutputText);

        var tool = new TedideProject { Name = "Tool", Target = Cc65Target.C64, SourceFiles = ["main.c"] };
        Directory.CreateDirectory(Path.Combine(_dir, "Tool"));
        tool.Save(Path.Combine(_dir, "Tool", "Tool.tproj"));
        _dialogs.FilePicks.Enqueue([tool.FilePath!]);
        _shell.Projects.AddExistingProject();
        _dialogs.FilePicks.Enqueue([MainC]);
        _shell.Projects.AddExistingProject();
        Assert.Equal(["Game", "Gfx", "Tool"], _shell.Workspace.Projects.Select(p => p.Name));
        Assert.Equal("Unsupported file: Expected a .tproj file.", Assert.Single(_dialogs.Errors));

        _shell.Projects.SetStartupProject(_shell.Workspace.Projects.Single(p => p.Name == "Tool"));
        Assert.Equal("Tool", _shell.Workspace.ActiveProject!.Name);
        Assert.Contains("Tool is now the startup project.", _shell.OutputText);

        _dialogs.QueryAnswers.Enqueue(0);
        _shell.Projects.RemoveProject(_shell.Workspace.Projects.Single(p => p.Name == "Gfx"));
        Assert.Equal(["Game", "Tool"], _shell.Workspace.Projects.Select(p => p.Name));
        Assert.True(Directory.Exists(Path.Combine(_dir, "Gfx")));  // its files stay
        Assert.Equal(2, TedideSolution.Load(Solution).ProjectPaths.Count);
        return Task.CompletedTask;
    });

    [Fact]
    public Task DeletingTheProjectThatHoldsTheSolution_IsRefused() => UiThread.Run(() =>
    {
        NewGame();

        _shell.Projects.DeleteProject(Game);

        Assert.StartsWith("Can't Delete Project: Game's folder also holds the solution file", Assert.Single(_dialogs.Errors));
        Assert.True(Directory.Exists(GameDir));
        return Task.CompletedTask;
    });

    [Fact]
    public Task RenamingTheProject_InSettings_MovesItsFolderTabsAndRecentEntry() => UiThread.Run(() =>
    {
        NewGame();
        _shell.Projects.OpenFile(MainC);
        AnswerWith<ProjectSettingsDialog>(d =>
        {
            d._nameField.Text = "Shooter";
            Assert.Null(d.Save());
        });

        _shell.Projects.ShowProjectSettings(Game);

        var project = Assert.Single(_shell.Workspace.Projects);
        Assert.Equal("Shooter", project.Name);
        Assert.True(File.Exists(project.FilePath));
        Assert.Equal(Path.Combine(project.Directory, "src", "main.c"), _shell.EditorPane.OpenPath);
        Assert.Contains(_shell.RecentProjects.Paths, p => p.Contains("Shooter", StringComparison.Ordinal));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Esc_DoesntQuitFromTheMainWindow() => UiThread.Run(() =>
    {
        // Terminal.Gui quits on Esc by default; the window takes it first.
        Assert.True(_shell.NewKeyDownEvent(Terminal.Gui.Input.Key.Esc));
        return Task.CompletedTask;
    });

    [Fact]
    public Task CheckAsYouType_IsRemembered() => UiThread.Run(() =>
    {
        var editorFile = Path.Combine(_dir, "settings", "editor.json");
        Assert.True(EditorSettings.Load(editorFile).CheckAsYouType);  // on by default

        _shell.SetCheckAsYouType(false);

        Assert.False(EditorSettings.Load(editorFile).CheckAsYouType);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ProjectSettings_WithNoProject_SaysToOpenOne() => UiThread.Run(() =>
    {
        _shell.Projects.ShowProjectSettings(null);

        Assert.Contains("No project loaded.", _shell.OutputText);
        Assert.Empty(_dialogs.Shown);
        return Task.CompletedTask;
    });
}
