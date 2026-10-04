using Tedide.DocViewer;

namespace Tedide.DocViewer.Tests;

public class NavigationHistoryTests
{
    private static readonly NavigationEntry Intro = new("cc65/intro.md", null);
    private static readonly NavigationEntry Coding = new("cc65/coding.md", "constants");
    private static readonly NavigationEntry Sprites = new("c64/sprites.md", null);

    [Fact]
    public void ANewHistory_CantGoAnywhere()
    {
        var history = new NavigationHistory();

        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.GoBack());
        Assert.Null(history.GoForward());
    }

    [Fact]
    public void BackAndForward_WalkThePagesVisited()
    {
        var history = new NavigationHistory();
        history.Push(Intro);
        history.Push(Coding);
        history.Push(Sprites);

        Assert.Equal(Coding, history.GoBack());
        Assert.Equal(Intro, history.GoBack());
        Assert.False(history.CanGoBack);
        Assert.Equal(Coding, history.GoForward());
        Assert.True(history.CanGoForward);
    }

    [Fact]
    public void VisitingAPage_AfterGoingBack_DropsTheForwardPages()
    {
        var history = new NavigationHistory();
        history.Push(Intro);
        history.Push(Coding);
        history.GoBack();

        history.Push(Sprites);

        Assert.False(history.CanGoForward);
        Assert.Equal(Intro, history.GoBack());
    }

    [Fact]
    public void ThePageAlreadyShown_IsntANewVisit()
    {
        var history = new NavigationHistory();
        history.Push(Intro);
        history.Push(Intro);

        Assert.False(history.CanGoBack);
    }
}

public class DocBookmarksTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tedide-bookmarks-").FullName;
    private string File => Path.Combine(_directory, "bookmarks.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Toggle_AddsABookmark_AndSavesIt()
    {
        var bookmarks = DocBookmarks.Load(File);

        bookmarks.Toggle("cc65/coding.md", "constants", "Constants");

        Assert.True(bookmarks.Contains("cc65/coding.md", "constants"));
        Assert.Equal([new Bookmark("cc65/coding.md", "constants", "Constants")], DocBookmarks.Load(File).Items);
    }

    [Fact]
    public void Toggle_RemovesABookmarkThatsThere()
    {
        var bookmarks = DocBookmarks.Load(File);
        bookmarks.Toggle("cc65/coding.md", null, "Coding");

        bookmarks.Toggle("cc65/coding.md", null, "Coding");

        Assert.Empty(DocBookmarks.Load(File).Items);
    }

    [Fact]
    public void APageAndASectionOfIt_AreDifferentBookmarks()
    {
        var bookmarks = DocBookmarks.Load(File);
        bookmarks.Toggle("cc65/coding.md", null, "Coding");

        Assert.False(bookmarks.Contains("cc65/coding.md", "constants"));
    }
}
