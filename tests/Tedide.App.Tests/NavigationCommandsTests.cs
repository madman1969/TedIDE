using Tedide.App.Views;
using Tedide.Build;
using Tedide.Core;
using Tedide.Core.Navigation;

namespace Tedide.App.Tests;

/// <summary>
/// Go To Definition, Find All References, Rename, Navigate Backward/Forward and opening files at a
/// line, through <see cref="NavigationCommands"/> - over a small project on disk, with a real
/// editor and the dialogs answered by the test.
/// </summary>
public sealed class NavigationCommandsTests : IDisposable
{
    private const string MainText = "#include \"screen.h\"\n\nint main(void)\n{\n    draw();\n    return LIMIT;\n}\n";

    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-nav-").FullName;
    private readonly Workspace _workspace = new();
    private readonly EditorPane _editorPane = new();
    private readonly NavigationHistory _history = new();
    private readonly ReferencesView _referencesView = new();
    private readonly FakeShell _shell;
    private readonly NavigationCommands _navigation;

    public NavigationCommandsTests()
    {
        _shell = new FakeShell(_editorPane);
        _navigation = new NavigationCommands(_shell, _workspace, _editorPane, _history, _referencesView);

        Write("main.c", MainText);
        Write("screen.h", "void draw(void);\n");
        Write("screen.c", "#include \"screen.h\"\n\nvoid draw(void)\n{\n}\n");
        // Two definitions main.c can't tell apart - neither header is included.
        Write("pal.h", "#define LIMIT 1\n");
        Write("ntsc.h", "#define LIMIT 2\n");
        var project = new TedideProject { Name = "Game", Target = Cc65Target.C64, SourceFiles = ["main.c", "screen.c"] };
        project.Save(Path.Combine(_dir, "Game.tproj"));
        _workspace.Projects.Add(project);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private void Write(string name, string text) => File.WriteAllText(PathOf(name), text);

    /// <summary>Opens main.c with the caret at the start of <paramref name="word"/>.</summary>
    private void OpenMainAt(string word)
    {
        _editorPane.Open(PathOf("main.c"));
        _editorPane.Editor.CaretOffset = MainText.IndexOf(word, StringComparison.Ordinal);
    }

    [Fact]
    public void GoToDefinition_JumpsStraightToTheOnlyOne_AndBackReturns()
    {
        OpenMainAt("draw");

        _navigation.GoToDefinition();

        Assert.True(_editorPane.IsShown(PathOf("screen.c")));
        Assert.Equal(3, _editorPane.CaretPosition.Line);
        Assert.Empty(_shell.Dialogs.Shown);

        _navigation.NavigateBackward();
        Assert.True(_editorPane.IsShown(PathOf("main.c")));
        Assert.Equal(5, _editorPane.CaretPosition.Line);

        _navigation.NavigateForward();
        Assert.True(_editorPane.IsShown(PathOf("screen.c")));
    }

    [Fact]
    public void GoToDefinition_WithSeveral_AsksWhichOne()
    {
        OpenMainAt("LIMIT");
        _shell.Dialogs.Answer = dialog =>
        {
            var picker = Assert.IsType<DefinitionPickerDialog>(dialog);
            picker.SelectedDefinition = new SymbolDefinition("LIMIT", SymbolKind.Macro, PathOf("ntsc.h"), 1, 9);
        };

        _navigation.GoToDefinition();

        Assert.Single(_shell.Dialogs.Shown);
        Assert.True(_editorPane.IsShown(PathOf("ntsc.h")));
    }

    [Fact]
    public void GoToDefinition_Cancelled_StaysPut()
    {
        OpenMainAt("LIMIT");

        _navigation.GoToDefinition();

        Assert.Single(_shell.Dialogs.Shown);
        Assert.True(_editorPane.IsShown(PathOf("main.c")));
    }

    [Fact]
    public void GoToDefinition_OnNothing_SaysSoInTheOutput()
    {
        OpenMainAt("(void)\n{");
        _editorPane.Editor.CaretOffset = MainText.IndexOf("\n{", StringComparison.Ordinal) + 1;

        _navigation.GoToDefinition();

        Assert.Single(_shell.Output);
        Assert.Equal(1, _shell.OutputShown);
        Assert.True(_editorPane.IsShown(PathOf("main.c")));
    }

    [Fact]
    public void FindAllReferences_FillsTheReferencesTab()
    {
        OpenMainAt("draw");

        _navigation.FindAllReferences();

        Assert.Equal("Find All References: 'draw' - 3 reference(s) in 3 file(s).", Assert.Single(_shell.Output));
        Assert.Same(_referencesView, _shell.PaneShown);
    }

    [Fact]
    public void Rename_ChangesClosedFilesOnDisk_AndOpenOnesInTheEditor()
    {
        OpenMainAt("draw");
        _shell.Dialogs.Answer = dialog =>
        {
            var rename = Assert.IsType<RenameSymbolDialog>(dialog);
            rename.Plan = rename.PlanFor("paint");
        };

        _navigation.RenameSymbol();

        Assert.Contains("void paint(void)", File.ReadAllText(PathOf("screen.c")));
        Assert.Equal("void paint(void);\n", File.ReadAllText(PathOf("screen.h")));
        Assert.Contains("paint();", _editorPane.Editor.Text);
        Assert.Equal(MainText, File.ReadAllText(PathOf("main.c")));  // open, so changed but unsaved
        Assert.True(_editorPane.IsModified);
        Assert.StartsWith("Renamed 'draw' to 'paint': 3 change(s) in 3 file(s) - 1 open file(s)", _shell.Output[^1]);
    }

    [Fact]
    public void Rename_Cancelled_ChangesNothing()
    {
        OpenMainAt("draw");

        _navigation.RenameSymbol();

        Assert.Single(_shell.Dialogs.Shown);
        Assert.Equal("void draw(void);\n", File.ReadAllText(PathOf("screen.h")));
        Assert.False(_editorPane.IsModified);
        Assert.Empty(_shell.Output);
    }

    [Fact]
    public void Navigating_WithNoHistory_SaysSo_AndSkipsDeletedFiles()
    {
        OpenMainAt("draw");
        _navigation.NavigateBackward();
        Assert.Equal("Nothing to navigate back to.", _shell.Output[^1]);
        _navigation.NavigateForward();
        Assert.Equal("Nothing to navigate forward to.", _shell.Output[^1]);

        Write("gone.c", "int gone;\n");
        _editorPane.Open(PathOf("gone.c"));
        _navigation.NavigateTo(PathOf("screen.c"), 1, 1);
        File.Delete(PathOf("gone.c"));

        _navigation.NavigateBackward();
        Assert.Equal("gone.c no longer exists.", _shell.Output[^1]);
    }

    [Fact]
    public void OpenDiagnostic_SelectsTheWholeLine_OfAPathRelativeToTheProject()
    {
        _navigation.OpenDiagnostic(new BuildDiagnostic("screen.c", 3, DiagnosticSeverity.Error, "oops"));

        Assert.True(_editorPane.IsShown(PathOf("screen.c")));
        Assert.Equal("void draw(void)", _editorPane.Editor.SelectedText);

        _navigation.OpenDiagnostic(new BuildDiagnostic("missing.c", 1, DiagnosticSeverity.Error, "oops"));
        Assert.True(_editorPane.IsShown(PathOf("screen.c")));
    }

    [Fact]
    public void OpenSymbol_PutsTheCaretOnTheLine()
    {
        _navigation.OpenSymbol((PathOf("screen.c"), 4));

        Assert.True(_editorPane.IsShown(PathOf("screen.c")));
        Assert.Equal((4, 1), _editorPane.CaretPosition);
    }

    [Fact]
    public void GoToLine_MovesTheCaret_AndCanBeUndoneWithBack()
    {
        OpenMainAt("draw");
        _shell.Dialogs.Answer = dialog => Assert.IsType<GoToLineDialog>(dialog).LineNumber = 7;

        _navigation.ShowGoToLine();
        Assert.Equal((7, 1), _editorPane.CaretPosition);

        _navigation.NavigateBackward();
        Assert.Equal(5, _editorPane.CaretPosition.Line);
    }

    [Fact]
    public void FindInFiles_OpensTheChosenMatch()
    {
        _shell.Dialogs.Answer = dialog =>
            Assert.IsType<FindInFilesDialog>(dialog).SelectedMatch = new FindInFilesDialog.Match(PathOf("screen.c"), "screen.c", 3, 6, "void draw(void)");

        _navigation.ShowFindInFiles();

        Assert.True(_editorPane.IsShown(PathOf("screen.c")));
        Assert.Equal((3, 6), _editorPane.CaretPosition);
    }

    [Fact]
    public void FindInFiles_WithNoProject_SaysToOpenOne()
    {
        _workspace.Projects.Clear();

        _navigation.ShowFindInFiles();

        Assert.Empty(_shell.Dialogs.Shown);
        Assert.StartsWith("No project loaded.", Assert.Single(_shell.Output));
    }

    [Fact]
    public void DisplayPath_IsRelativeToTheProject_OrLeftAlone()
    {
        Assert.Equal("screen.c", _navigation.DisplayPath(PathOf("screen.c")));
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere.h");
        Assert.Equal(outside, _navigation.DisplayPath(outside));
    }
}
