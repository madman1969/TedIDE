using Tedide.App.Views;
using Tedide.Core;

namespace Tedide.App.Tests;

public class ProjectSettingsDialogTests
{
    [Theory]
    [InlineData(Cc65Target.Vic20, "vic20.cfg")]
    [InlineData(Cc65Target.C64, "c64.cfg")]
    [InlineData(Cc65Target.C128, "c128.cfg")]
    public void DefaultLinkerConfigPath_PointsAtTheTargetsOwnCfgFile_UnderCc65HomesCfgFolder(Cc65Target target, string expectedFileName)
    {
        var path = ProjectSettingsDialog.DefaultLinkerConfigPath(target, @"C:\CC65");

        Assert.Equal(Path.Combine(@"C:\CC65", "cfg", expectedFileName), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DefaultLinkerConfigPath_ReturnsNull_WhenCc65HomeIsNullOrBlank(string? cc65Home)
    {
        Assert.Null(ProjectSettingsDialog.DefaultLinkerConfigPath(Cc65Target.Vic20, cc65Home));
    }
}
