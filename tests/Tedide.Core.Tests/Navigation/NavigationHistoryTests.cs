using Tedide.Core.Navigation;

namespace Tedide.Core.Tests.Navigation;

public class NavigationHistoryTests
{
    private static CaretLocation At(string file, int line, int column = 1) => new(file, line, column);

    [Fact]
    public void BackAndForward_RetraceTheJumps()
    {
        var history = new NavigationHistory();
        history.RecordJump(At(@"C:\p\main.c", 10));
        history.RecordJump(At(@"C:\p\screen.c", 20));

        Assert.Equal(At(@"C:\p\screen.c", 20), history.GoBack(At(@"C:\p\input.c", 30)));
        Assert.Equal(At(@"C:\p\main.c", 10), history.GoBack(At(@"C:\p\screen.c", 20)));
        Assert.Null(history.GoBack(At(@"C:\p\main.c", 10)));

        Assert.Equal(At(@"C:\p\screen.c", 20), history.GoForward(At(@"C:\p\main.c", 10)));
        Assert.Equal(At(@"C:\p\input.c", 30), history.GoForward(At(@"C:\p\screen.c", 20)));
        Assert.Null(history.GoForward(At(@"C:\p\input.c", 30)));
    }

    [Fact]
    public void ANewJump_ClearsForward_AndARepeatedPlaceIsKeptOnce()
    {
        var history = new NavigationHistory();
        history.RecordJump(At("a.c", 1));
        history.RecordJump(At("A.C", 1));
        history.GoBack(At("b.c", 2));
        Assert.True(history.CanGoForward);
        Assert.False(history.CanGoBack); // "A.C" was the same place as "a.c"

        history.RecordJump(At("c.c", 3));
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void GoBack_WithNoFileOpen_KeepsNothingForForward()
    {
        var history = new NavigationHistory();
        history.RecordJump(At("a.c", 1));
        Assert.Equal(At("a.c", 1), history.GoBack(null));
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void OnlyTheNewestEntriesAreKept()
    {
        var history = new NavigationHistory();
        for (var i = 1; i <= NavigationHistory.MaxEntries + 5; i++)
            history.RecordJump(At("a.c", i));

        var count = 0;
        CaretLocation? last = null;
        while (history.GoBack(null) is { } location)
        {
            last = location;
            count++;
        }
        Assert.Equal(NavigationHistory.MaxEntries, count);
        Assert.Equal(6, last!.Value.Line);
    }

    [Fact]
    public void MovePath_FollowsAFileOrAFolderRename_AndRemoveFileDropsIt()
    {
        var history = new NavigationHistory();
        history.RecordJump(At(@"C:\old\src\a.c", 1));
        history.RecordJump(At(@"C:\other\b.c", 2));
        history.RecordJump(At(@"C:\old\c.c", 3));

        history.MovePath(@"C:\old", @"C:\new");
        history.MovePath(@"C:\other\b.c", @"C:\other\renamed.c");
        history.RemoveFile(@"C:\new\c.c");

        Assert.Equal(At(@"C:\other\renamed.c", 2), history.GoBack(null));
        Assert.Equal(At(@"C:\new\src\a.c", 1), history.GoBack(null));
        Assert.Null(history.GoBack(null));
    }
}
