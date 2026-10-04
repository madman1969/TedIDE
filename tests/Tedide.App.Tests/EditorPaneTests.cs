using Tedide.App.Views;

namespace Tedide.App.Tests;

/// <summary>Several files open at once behind the one shared editor - see EditorPane.</summary>
public sealed class EditorPaneTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();

    public void Dispose() => _dir.Delete(recursive: true);

    private string File(string name, string text)
    {
        var path = Path.Combine(_dir.FullName, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Open_AddsATabPerFile_AndReopeningSwitchesToIt()
    {
        var pane = new EditorPane();
        var main = File("main.c", "int main(void) { return 0; }\n");
        var screen = File("screen.c", "void draw(void) {}\n");
        var changes = 0;
        pane.ActiveDocumentChanged += () => changes++;

        pane.Open(main);
        pane.Open(screen);
        pane.Open(main);

        Assert.Equal([main, screen], pane.OpenPaths);
        Assert.Equal(main, pane.OpenPath);
        Assert.Equal("int main(void) { return 0; }\n", pane.Editor.Text);
        Assert.Equal(3, changes);
        Assert.True(pane.IsOpen(screen));
    }

    [Fact]
    public void CollapseAndExpandAll_FoldEveryRegion_NestedOnesToo()
    {
        var pane = new EditorPane();
        Assert.Equal(0, pane.SetAllFolded(true));  // nothing open

        pane.Open(File("main.c", """
            void a(void)
            {
                if (1)
                {
                    x();
                }
            }

            void b(void)
            {
                y();
            }
            """));
        var folding = pane.Editor.FoldingManager!;
        Assert.Equal(3, folding.AllFoldings.Count());

        Assert.Equal(3, pane.SetAllFolded(true));
        Assert.All(folding.AllFoldings, f => Assert.True(f.IsFolded));
        Assert.Equal(0, pane.SetAllFolded(true));  // already folded

        Assert.Equal(3, pane.SetAllFolded(false));
        Assert.All(folding.AllFoldings, f => Assert.False(f.IsFolded));
    }

    [Fact]
    public void Reload_ReplacesTheTextFromDisk_AndClearsUnsavedEdits()
    {
        var pane = new EditorPane();
        var main = File("main.c", "one\n");
        pane.Open(main);
        pane.Editor.Document!.Insert(0, "edited ");
        Assert.True(pane.IsModified);

        System.IO.File.WriteAllText(main, "restored\n");
        pane.Reload(main);

        Assert.Equal("restored\n", pane.Editor.Text);
        Assert.False(pane.IsModified);
        pane.Reload(Path.Combine(_dir.FullName, "not-open.c")); // nothing happens
    }

    [Theory]
    [InlineData("main · Ln 3: Tester", 40, "main · Ln 3: Tester")]
    [InlineData("main · Ln 3: Tester, 3 days ago", 12, "main · Ln 3…")]
    [InlineData("main", 5, "")]
    public void TabStripAnnotation_IsCutToTheRoomTheTabsLeave(string annotation, int room, string expected)
    {
        Assert.Equal(expected, DocumentTabStrip.Fit(annotation, room));
    }

    [Fact]
    public void Open_TreatsDifferentSpellingsOfOnePathAsTheSameFile()
    {
        var pane = new EditorPane();
        var main = File(Path.Combine("Game", "src", "main.c"), "int main(void) { return 0; }\n");

        pane.Open(main);
        // As the debugger hands them over: a "/" from the debug info, or a "..".
        pane.Open(Path.Combine(_dir.FullName, "Game") + "\\src/main.c");
        pane.Open(Path.Combine(_dir.FullName, "Gfx", "..", "Game", "src", "main.c"));

        Assert.Equal([main], pane.OpenPaths);
        Assert.True(pane.IsShown(Path.Combine(_dir.FullName, "Game") + "\\src/main.c"));
        Assert.False(pane.IsShown(Path.Combine(_dir.FullName, "Game", "src", "other.c")));
    }

    [Fact]
    public void EachFile_KeepsItsOwnEditsUndoHistoryAndCaret()
    {
        var pane = new EditorPane();
        var main = File("main.c", "abc\n");
        var screen = File("screen.c", "xyz\n");
        pane.Open(main);
        pane.Editor.CaretOffset = 2;
        pane.DocumentOf(main)!.Insert(0, "// ");

        pane.Open(screen);
        Assert.False(pane.IsModified);
        Assert.Equal("xyz\n", pane.Editor.Text);

        pane.Open(main);
        Assert.True(pane.IsModified);
        Assert.Equal("// abc\n", pane.Editor.Text);
        Assert.True(pane.Editor.Document!.UndoStack.CanUndo);
        Assert.Equal([main], pane.ModifiedPaths);
        Assert.Equal("// abc\n", pane.TextOf(main));
        Assert.Null(pane.TextOf(Path.Combine(_dir.FullName, "other.c")));
    }

    [Fact]
    public void Save_WritesOneFile_AndClearsItsModifiedFlag()
    {
        var pane = new EditorPane();
        var main = File("main.c", "abc\n");
        var screen = File("screen.c", "xyz\n");
        pane.Open(main);
        pane.Open(screen);
        pane.DocumentOf(main)!.Insert(0, "1");
        pane.DocumentOf(screen)!.Insert(0, "2");

        Assert.Null(pane.Save(main));

        Assert.Equal("1abc\n", System.IO.File.ReadAllText(main));
        Assert.Equal("xyz\n", System.IO.File.ReadAllText(screen));
        Assert.False(pane.IsModifiedFile(main));
        Assert.True(pane.IsModifiedFile(screen));
        Assert.Null(pane.Save(Path.Combine(_dir.FullName, "not-open.c")));
    }

    [Fact]
    public void Close_ShowsTheNeighbouringTab_AndClosingTheLastLeavesTheEditorReadOnly()
    {
        var pane = new EditorPane();
        var a = File("a.c", "a");
        var b = File("b.c", "b");
        var c = File("c.c", "c");
        pane.Open(a);
        pane.Open(b);
        pane.Open(c);
        pane.Open(b);

        pane.Close();
        Assert.Equal(c, pane.OpenPath);

        pane.Close(a);
        Assert.Equal(c, pane.OpenPath);
        Assert.Equal([c], pane.OpenPaths);

        pane.CloseAll();
        Assert.Null(pane.OpenPath);
        Assert.Empty(pane.OpenPaths);
        Assert.True(pane.Editor.ReadOnly);
        Assert.Equal(string.Empty, pane.Editor.Text);
    }

    [Fact]
    public void Rename_KeepsTheBufferUnderTheNewPath()
    {
        var pane = new EditorPane();
        var oldPath = File("old.c", "abc");
        pane.Open(oldPath);
        pane.DocumentOf(oldPath)!.Insert(0, "edited ");
        var newPath = Path.Combine(_dir.FullName, "new.c");
        System.IO.File.Move(oldPath, newPath);

        pane.Rename(oldPath, newPath);

        Assert.Equal(newPath, pane.OpenPath);
        Assert.Equal("edited abc", pane.Editor.Text);
        Assert.True(pane.IsModified);
        pane.Save();
        Assert.Equal("edited abc", System.IO.File.ReadAllText(newPath));
    }

    [Fact]
    public void ReadOnly_AppliesToEveryTabSwitchedTo()
    {
        var pane = new EditorPane();
        var a = File("a.c", "a");
        var b = File("b.c", "b");
        pane.Open(a);
        pane.Open(b);

        pane.ReadOnly = true;
        pane.Open(a);
        Assert.True(pane.Editor.ReadOnly);

        pane.ReadOnly = false;
        Assert.False(pane.Editor.ReadOnly);
    }

    [Fact]
    public void CycleDocument_WrapsRound()
    {
        var pane = new EditorPane();
        var a = File("a.c", "a");
        var b = File("b.c", "b");
        Assert.False(pane.CycleDocument(1));
        pane.Open(a);
        pane.Open(b);

        Assert.True(pane.CycleDocument(1));
        Assert.Equal(a, pane.OpenPath);
        Assert.True(pane.CycleDocument(-1));
        Assert.Equal(b, pane.OpenPath);
    }

    [Fact]
    public void Open_InsertsTheNewTabAfterTheCurrentOne()
    {
        var pane = new EditorPane();
        var a = File("a.c", "a");
        var b = File("b.c", "b");
        var c = File("c.c", "c");
        pane.Open(a);
        pane.Open(b);
        pane.Open(a);

        pane.Open(c);

        Assert.Equal([a, c, b], pane.OpenPaths);
    }

    [Fact]
    public void Open_OfAnUnreadableFile_LeavesEverythingAsItWas()
    {
        var pane = new EditorPane();
        var a = File("a.c", "a");
        pane.Open(a);
        var locked = File("locked.c", "x");

        using (System.IO.File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => pane.Open(locked));

        Assert.Equal([a], pane.OpenPaths);
        Assert.Equal(a, pane.OpenPath);
    }
}

public class DocumentTabStripTests
{
    private static DocumentTabStrip.Tab Tab(string title, bool modified = false) => new(title, modified);

    [Fact]
    public void Label_MarksUnsavedFilesAndHasACloseButton()
    {
        Assert.Equal(" main.c x ", DocumentTabStrip.Label(Tab("main.c")));
        Assert.Equal(" main.c* x ", DocumentTabStrip.Label(Tab("main.c", modified: true)));
    }

    [Fact]
    public void FirstVisible_ScrollsOnlyAsFarAsNeededToShowTheActiveTab()
    {
        var tabs = Enumerable.Range(0, 10).Select(i => Tab($"file{i}.c")).ToList(); // 12 columns each with the separator

        Assert.Equal(0, DocumentTabStrip.FirstVisible(tabs, 2, 80));
        Assert.Equal(0, DocumentTabStrip.FirstVisible(tabs, -1, 80));
        var first = DocumentTabStrip.FirstVisible(tabs, 9, 40);
        Assert.True(first > 0);
        Assert.True(tabs.Skip(first).Take(10 - first).Sum(t => DocumentTabStrip.Label(t).Length + 1) <= 38);
    }
}
