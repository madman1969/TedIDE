using Tedide.App.Views;
using Tedide.Git;

namespace Tedide.App.Tests;

public class StashesDialogTests
{
    [Fact]
    public void Row_ShowsNameDescriptionAndAge()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1759500000);
        Assert.Equal("stash@{0}  On main: tidy up  (2 hours ago)",
            StashesDialog.Row(new GitStash("stash@{0}", "On main: tidy up", now.AddHours(-2)), now));
    }
}
