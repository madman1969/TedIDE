using Tedide.App.Views;
using Tedide.Git;

namespace Tedide.App.Tests;

public class BranchesDialogTests
{
    [Fact]
    public void Row_MarksTheCurrentBranchAndSaysWhatEachTracks()
    {
        Assert.Equal("● main     origin/main", BranchesDialog.Row(new GitBranch("main", false, true, "origin/main"), 6));
        Assert.Equal("  feature", BranchesDialog.Row(new GitBranch("feature", false, false, null), 6));
        Assert.Equal("  origin/topic   (remote - switching makes a local copy)",
            BranchesDialog.Row(new GitBranch("origin/topic", true, false, null), 12));
    }
}
