using Tedide.App.Views;
using Tedide.Git;

namespace Tedide.App.Tests;

public class HistoryAndConflictDialogTests
{
    [Fact]
    public void HistoryRow_ShowsStatusPathAndWhereARenameCameFrom()
    {
        Assert.Equal("M  src/main.c", HistoryDialog.Row(new GitCommitFile('M', "src/main.c")));
        Assert.Equal("R  src/game.c  (from src/main.c)", HistoryDialog.Row(new GitCommitFile('R', "src/game.c", "src/main.c")));
    }

    [Fact]
    public void CountSections_CountsConflictStartMarkers()
    {
        var text = "a\n<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> other\nb\n<<<<<<< HEAD\nx\n=======\ny\n>>>>>>> other\n";
        Assert.Equal(2, ConflictDialog.CountSections(text));
        Assert.Equal(0, ConflictDialog.CountSections("no markers\n  <<<<<<< not at line start\n"));
    }
}
