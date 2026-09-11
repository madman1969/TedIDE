using Tedide.App.Views;
using Tedide.Core;
using Terminal.Gui.Views;

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

    [Theory]
    [InlineData(Cc65Target.Vic20)]
    [InlineData(Cc65Target.C64)]
    [InlineData(Cc65Target.C128)]
    [InlineData(Cc65Target.C16)]
    [InlineData(Cc65Target.Plus4)]
    [InlineData(Cc65Target.Pet)]
    [InlineData(Cc65Target.Cbm510)]
    [InlineData(Cc65Target.Cbm610)]
    [InlineData(Cc65Target.Geos_Cbm)]
    public void SupportedLinkerConfigFileNames_ListsAtLeastTheTargetsOwnDefaultCfgFile(Cc65Target target)
    {
        // The default config DefaultLinkerConfigPath itself points the Browse button at must always
        // be one of the files its own filter actually shows, or the two would disagree about what
        // "this target's config" means.
        Assert.Contains($"{target.ToCl65Id()}.cfg", ProjectSettingsDialog.SupportedLinkerConfigFileNames[target], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SupportedLinkerConfigFileNames_ListsEveryCc65BundledVic20Config()
    {
        // Cross-checked directly against a real cc65 install's cfg/ folder - see
        // ViceEmulator.Vic20MemorySpecFor's own survey of these same six files.
        Assert.Equivalent(
            new[] { "vic20.cfg", "vic20-32k.cfg", "vic20-asm.cfg", "vic20-asm-32k.cfg", "vic20-asm-3k.cfg", "vic20-tgi.cfg" },
            ProjectSettingsDialog.SupportedLinkerConfigFileNames[Cc65Target.Vic20]);
    }

    [Fact]
    public void LinkerConfigAllowedTypes_TheDefaultFilter_AllowsOnlyTheTargetsOwnConfigs()
    {
        var defaultFilter = ProjectSettingsDialog.LinkerConfigAllowedTypes(Cc65Target.Vic20)[0];

        Assert.True(defaultFilter.IsAllowed(@"C:\CC65\cfg\vic20-32k.cfg"));
        Assert.False(defaultFilter.IsAllowed(@"C:\CC65\cfg\c64.cfg"));
        Assert.False(defaultFilter.IsAllowed(@"C:\Somewhere\my-custom-vic20-config.cfg"));
    }

    [Fact]
    public void LinkerConfigAllowedTypes_AlwaysIncludesAFilterThatAllowsAnyCfgFile()
    {
        // The whole point of "Custom linker config" is that it isn't limited to cc65's own bundled
        // configs - a genuinely custom-named one must still be reachable by switching filters.
        var allowedTypes = ProjectSettingsDialog.LinkerConfigAllowedTypes(Cc65Target.Vic20);

        Assert.Contains(allowedTypes, t => t.IsAllowed(@"C:\Somewhere\my-custom-vic20-config.cfg"));
    }

    [Fact]
    public void LinkerConfigAllowedTypes_StillOffersTheAllFilesFilter_ForATargetWithNoKnownConfigs()
    {
        // Cc65Target.None (or any other target outside Cc65TargetExtensions.CommodoreTargets) isn't
        // reachable via the Settings tab's own dropdown, but a hand-edited .tproj could still set
        // one - this must fall back to "show everything" rather than an empty, dead-end filter list.
        var allowedTypes = ProjectSettingsDialog.LinkerConfigAllowedTypes(Cc65Target.None);

        Assert.Single(allowedTypes);
        Assert.True(allowedTypes[0].IsAllowed(@"C:\Somewhere\anything.cfg"));
    }
}
