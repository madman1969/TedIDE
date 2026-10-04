using Tedide.App.Views;
using Tedide.Theming;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Tests;

public class PaneTabsTests
{
    private static (PaneTabs Tabs, View Locals, View Watch, View Strip) Make()
    {
        var locals = new View();
        var watch = new View();
        var tabs = new PaneTabs(("Locals", locals), ("Watch", watch));
        var strip = tabs.SubViews.First(v => v != locals && v != watch);
        return (tabs, locals, watch, strip);
    }

    [Fact]
    public void TheFirstWindowIsShown()
    {
        var (tabs, locals, watch, _) = Make();

        Assert.Equal(0, tabs.Selected);
        Assert.True(locals.Visible);
        Assert.False(watch.Visible);
    }

    [Fact]
    public void Select_ShowsOnlyThatWindow()
    {
        var (tabs, locals, watch, _) = Make();

        tabs.Select(1);

        Assert.Equal(1, tabs.Selected);
        Assert.False(locals.Visible);
        Assert.True(watch.Visible);
    }

    [Fact]
    public void Select_IgnoresAnIndexThatIsntThere()
    {
        var (tabs, _, _, _) = Make();

        tabs.Select(5);
        tabs.Select(-1);

        Assert.Equal(0, tabs.Selected);
    }

    [Fact]
    public void LeftAndRight_OnTheStrip_MoveBetweenWindows_WrappingRound()
    {
        var (tabs, _, _, strip) = Make();

        strip.NewKeyDownEvent(Key.CursorRight);
        Assert.Equal(1, tabs.Selected);
        strip.NewKeyDownEvent(Key.CursorRight);
        Assert.Equal(0, tabs.Selected);
        strip.NewKeyDownEvent(Key.CursorLeft);
        Assert.Equal(1, tabs.Selected);
    }
}

public class ThemeMenuTests
{
    [Fact]
    public void TheMenuListsEveryTheme()
    {
        var menu = ThemeMenuBuilder.Build();
        var items = menu.PopoverMenu!.Root!.SubViews.OfType<MenuItem>().ToList();

        Assert.Equal(Enum.GetValues<AppTheme>().Length, items.Count);
    }

    [Fact]
    public void UpdateChecks_TicksOnlyTheCurrentTheme()
    {
        var items = Enum.GetValues<AppTheme>().Select(_ => new MenuItem()).ToList();

        ThemeMenuBuilder.UpdateChecks(items, AppTheme.SolarizedLight);

        var ticked = Assert.Single(items, item => item.Title.StartsWith('✓'));
        Assert.Equal("✓ Solarized Li_ght", ticked.Title);
        Assert.All(items.Where(item => item != ticked), item => Assert.StartsWith("  ", item.Title));
    }

    [Fact]
    public void UpdateChecks_MovesTheTick()
    {
        var items = Enum.GetValues<AppTheme>().Select(_ => new MenuItem()).ToList();
        ThemeMenuBuilder.UpdateChecks(items, AppTheme.Dracula);

        ThemeMenuBuilder.UpdateChecks(items, AppTheme.Commodore64);

        Assert.Equal("✓ _Commodore 64", Assert.Single(items, item => item.Title.StartsWith('✓')).Title);
    }
}
