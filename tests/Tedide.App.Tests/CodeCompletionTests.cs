using Tedide.App.Views;
using Tedide.Core;
using Terminal.Gui.Input;

namespace Tedide.App.Tests;

/// <summary>
/// <see cref="CodeCompletion"/> as the editor uses it: suggestions from the index, the signature on
/// the tab row, "." asking for members, and the off switch - over a small project in a temporary
/// folder, with timers and posted work run by hand.
/// </summary>
public sealed class CodeCompletionTests : IDisposable
{
    private const string MainText = "#include \"game.h\"\n\nvoid main(void)\n{\n    player_t *p;\n    \n}\n";

    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-completion-").FullName;
    private readonly List<Func<bool>> _timers = [];
    private readonly List<Action> _posted = [];

    public CodeCompletionTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "include"));
        File.WriteAllText(MainC, MainText);
        File.WriteAllText(Path.Combine(_dir, "include", "game.h"),
            "typedef struct { unsigned char lives, score; } player_t;\nvoid draw_player(player_t *p, unsigned char colour);\n");
        File.WriteAllText(Path.Combine(_dir, "src", "score.c"), "int high_score;\n");
        new TedideProject { Name = "Game", Target = Cc65Target.C64, SourceFiles = ["src/main.c", "src/score.c"], IncludePaths = ["include"] }
            .Save(Path.Combine(_dir, "Game.tproj"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MainC => Path.Combine(_dir, "src", "main.c");

    private sealed record Setup(EditorPane Editor, CodeCompletion Completion, Workspace Workspace);

    /// <summary>The project open, main.c shown, and the index brought up to date.</summary>
    private async Task<Setup> OpenAsync()
    {
        var workspace = new Workspace();
        var editor = new EditorPane();
        var completion = new CodeCompletion(new FakeShell(editor), workspace, editor,
            (_, callback) => _timers.Add(callback), _posted.Add, () => []);
        editor.ActiveDocumentChanged += completion.ActiveDocumentChanged;
        workspace.OpenProject(Path.Combine(_dir, "Game.tproj"));
        await completion.LastRefresh;
        editor.Open(MainC);
        await completion.LastRefresh;
        return new Setup(editor, completion, workspace);
    }

    /// <summary>Types <paramref name="text"/> at <paramref name="offset"/> and asks for suggestions
    /// the way the editor does after a keystroke.</summary>
    private static List<string> TypeAndComplete(EditorPane editor, CodeCompletion completion, int offset, string text)
    {
        editor.Editor.Document!.Insert(offset, text);
        editor.Editor.CaretOffset = offset + text.Length;
        var prefixStart = editor.Editor.CaretOffset;
        while (prefixStart > 0 && (char.IsAsciiLetterOrDigit(editor.Editor.Text[prefixStart - 1]) || editor.Editor.Text[prefixStart - 1] == '_'))
            prefixStart--;
        return completion.GetCompletions(editor.Editor.Document, editor.Editor.CaretOffset,
            editor.Editor.Text[prefixStart..editor.Editor.CaretOffset]).Select(i => i.InsertText!).ToList();
    }

    private static int BlankLine => MainText.IndexOf("    \n}", StringComparison.Ordinal) + 4;

    [Fact]
    public Task Names_AreSuggestedFromTheFileItsHeadersAndTheProject() => UiThread.Run(async () =>
    {
        var (editor, completion, _) = await OpenAsync();

        Assert.Equal(["draw_player"], TypeAndComplete(editor, completion, BlankLine, "dr"));
        editor.Editor.Document!.Remove(BlankLine, 2);
        Assert.Contains("high_score", TypeAndComplete(editor, completion, BlankLine, "hi"));
    });

    [Fact]
    public Task OneLetter_SuggestsNothing_UnlessCtrlSpaceAsks() => UiThread.Run(async () =>
    {
        var (editor, completion, _) = await OpenAsync();

        Assert.Empty(TypeAndComplete(editor, completion, BlankLine, "d"));

        Assert.True(completion.ShouldTrigger(Key.Space.WithCtrl));
        Assert.Contains("draw_player", completion.GetCompletions(editor.Editor.Document!, editor.Editor.CaretOffset, "d").Select(i => i.InsertText));
        Assert.False(completion.ShouldTrigger(Key.A));
    });

    [Fact]
    public Task TypingADot_AsksForTheMembers_OnceTheKeyIsHandled() => UiThread.Run(async () =>
    {
        var (editor, completion, _) = await OpenAsync();
        // The shown file's own "p" has to be indexed: that happens once typing pauses.
        foreach (var timer in _timers.ToList())
            timer();

        editor.Editor.Document!.Insert(BlankLine, "p-");
        Assert.Empty(_posted);
        editor.Editor.Document.Insert(BlankLine + 2, ">");
        editor.Editor.CaretOffset = BlankLine + 3;

        Assert.Single(_posted);
        Assert.Equal(["lives", "score"], completion.GetCompletions(editor.Editor.Document, editor.Editor.CaretOffset, "").Select(i => i.InsertText));
    });

    [Fact]
    public Task TheCallBeingTyped_ShowsItsSignature() => UiThread.Run(async () =>
    {
        var (editor, _, _) = await OpenAsync();

        editor.Editor.Document!.Insert(BlankLine, "draw_player(p, ");
        editor.Editor.CaretOffset = BlankLine + "draw_player(p, ".Length;

        Assert.Equal("void draw_player(player_t *p, unsigned char colour)", editor.SignatureHint);
        editor.Editor.CaretOffset = 0;
        Assert.Equal("", editor.SignatureHint);
    });

    [Fact]
    public Task TurningItOff_StopsSuggestionsAndSignatures() => UiThread.Run(async () =>
    {
        var (editor, completion, _) = await OpenAsync();

        completion.Enabled = false;

        Assert.Null(editor.Editor.CompletionProvider);
        Assert.Empty(TypeAndComplete(editor, completion, BlankLine, "dr"));
        editor.Editor.Document!.Insert(BlankLine, "draw_player(");
        editor.Editor.CaretOffset = BlankLine + "draw_player(".Length;
        Assert.Equal("", editor.SignatureHint);
        Assert.False(completion.ShouldTrigger(Key.Space.WithCtrl));

        completion.Enabled = true;
        Assert.Same(completion, editor.Editor.CompletionProvider);
    });

    [Fact]
    public void Suggestions_LineUpTheirKinds()
    {
        var items = CodeCompletion.Format([new("x", "local", 0), new("draw_player", "function", 2)]);

        Assert.Equal(["x            local", "draw_player  function"], items.Select(i => i.Label));
        Assert.Equal(["x", "draw_player"], items.Select(i => i.InsertText));
    }
}
