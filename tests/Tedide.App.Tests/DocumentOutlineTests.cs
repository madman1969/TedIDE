using Tedide.App.Views;
using Tedide.Core;
using Tedide.Core.Navigation;
using Terminal.Gui.Views;

namespace Tedide.App.Tests;

/// <summary>
/// The Document Outline: <see cref="DocumentOutlineView"/> showing a file's structure, and
/// <see cref="DocumentOutlineTracking"/> keeping it on the shown file - with the timers run by hand.
/// </summary>
public sealed class DocumentOutlineTests : IDisposable
{
    private const string MainText = """
        struct point { int x, y; };

        void draw(void)
        {
            int local;
        }

        int main(void)
        {
            return 0;
        }

        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-outline-").FullName;
    private readonly List<Func<bool>> _timers = [];
    private bool _shown = true;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private (EditorPane Editor, DocumentOutlineView View, DocumentOutlineTracking Tracking) Make()
    {
        var editor = new EditorPane();
        var view = new DocumentOutlineView();
        var tracking = new DocumentOutlineTracking(new Workspace(), editor, view, () => _shown, (_, callback) => _timers.Add(callback));
        editor.ActiveDocumentChanged += tracking.ActiveDocumentChanged;
        return (editor, view, tracking);
    }

    private void FireTimers()
    {
        var due = _timers.ToList();
        _timers.Clear();
        foreach (var callback in due)
            callback();
    }

    private static List<string> Rows(DocumentOutlineView view) =>
        Flatten(view.Tree.Objects!).Select(n => n.Text).ToList();

    private static IEnumerable<ITreeNode> Flatten(IEnumerable<ITreeNode> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    private static ITreeNode Node(DocumentOutlineView view, string text) =>
        Flatten(view.Tree.Objects!).Single(n => n.Text.EndsWith(text, StringComparison.Ordinal));

    [Fact]
    public void OpeningAFile_ShowsItsOutline_WithAMarkForEachKind()
    {
        var (editor, view, _) = Make();

        editor.Open(Write("main.c", MainText));

        Assert.Equal(["T struct point", "· x : int", "· y : int", "ƒ draw(void) : void", "ƒ main(void) : int"], Rows(view));
    }

    [Fact]
    public void Edits_ShowAfterThePause_AndOnlyWhileTheOutlineIsShown()
    {
        var (editor, view, tracking) = Make();
        editor.Open(Write("main.c", MainText));

        editor.Editor.Document!.Insert(0, "void extra(void);\n");
        Assert.DoesNotContain("ƒ extra(void) : void", Rows(view));
        FireTimers();
        Assert.Contains("ƒ extra(void) : void", Rows(view));

        // Hidden: nothing is scanned until it's shown again.
        _shown = false;
        editor.Editor.Document.Insert(0, "void hidden(void);\n");
        Assert.Empty(_timers);
        _shown = true;
        tracking.Shown();
        Assert.Contains("ƒ hidden(void) : void", Rows(view));
    }

    [Fact]
    public void TheSelection_FollowsTheCaret_IntoCollapsedNodesOnlyAsFarAsTheyShow()
    {
        var (editor, view, _) = Make();
        editor.Open(Write("main.c", MainText));

        editor.Editor.CaretOffset = editor.Editor.Document!.GetLineByNumber(10).Offset;
        Assert.Equal("ƒ main(void) : int", view.Tree.SelectedObject!.Text);

        editor.Editor.CaretOffset = editor.Editor.Document.GetLineByNumber(1).Offset + 18;
        Assert.Equal("· x : int", view.Tree.SelectedObject!.Text);

        view.CollapseAll();
        editor.Editor.CaretOffset = editor.Editor.Document.GetLineByNumber(4).Offset;
        editor.Editor.CaretOffset = editor.Editor.Document.GetLineByNumber(1).Offset + 18;
        Assert.Equal("T struct point", view.Tree.SelectedObject!.Text);
    }

    [Fact]
    public void CollapsedNodes_StayCollapsed_AsTheFileIsEdited()
    {
        var (editor, view, _) = Make();
        editor.Open(Write("main.c", MainText));

        view.CollapseAll();
        editor.Editor.Document!.Insert(0, "int score;\n");
        FireTimers();

        Assert.False(view.Tree.IsExpanded(Node(view, "struct point")));
        view.ExpandAll();
        Assert.True(view.Tree.IsExpanded(Node(view, "struct point")));
    }

    [Fact]
    public void FilterAndSort_ReshapeTheTree()
    {
        var (editor, view, _) = Make();
        editor.Open(Write("main.c", MainText));

        view.FilterField.Text = "main";
        Assert.Equal(["ƒ main(void) : int"], Rows(view));

        view.FilterField.Text = "";
        view.SortByName = true;
        Assert.Equal(["ƒ draw(void) : void", "ƒ main(void) : int", "T struct point", "· x : int", "· y : int"], Rows(view));
    }

    [Fact]
    public void AFileWithNoOutline_SaysSo()
    {
        var (editor, view, _) = Make();

        editor.Open(Write("game.cfg", "MEMORY { }\n"));

        Assert.Empty(view.Outline);
        Assert.Empty(view.Tree.Objects!);
    }

    [Theory]
    [InlineData(SymbolKind.Function, "ƒ")]
    [InlineData(SymbolKind.Macro, "#")]
    [InlineData(SymbolKind.Tag, "T")]
    [InlineData(SymbolKind.Label, "›")]
    [InlineData(SymbolKind.Variable, "•")]
    public void EachKind_HasItsMark(SymbolKind kind, string mark) => Assert.Equal(mark, DocumentOutlineView.Mark(kind));
}
