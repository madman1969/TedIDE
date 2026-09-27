using Tedide.Theming;
using Terminal.Gui.Drawing;
using GuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace Tedide.App.Tests;

/// <summary>
/// Checks every theme's colors for the two failures a screenshot shows as missing letters: text
/// drawn in (almost) its own background color, and a hotkey letter indistinguishable from the text
/// around it. Both were live bugs: Solarized Light/Dark and Dracula drew a focused menu item's
/// hotkey in the focus background color ("ile" for File, "ookmarks", "emove Bookmark").
/// </summary>
public class ThemeSchemeTests
{
    public static TheoryData<AppTheme> AllThemes => new(Enum.GetValues<AppTheme>());

    private static readonly string[] Slots = ["Base", "Menu", "Dialog", "Accent", "Error", "Warning"];

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryTextAndHotkeyRole_StandsOutFromItsBackground(AppTheme theme)
    {
        var problems = new List<string>();
        foreach (var (slot, scheme) in ThemeSwitcher.SchemesFor(theme))
        {
            foreach (var (role, attribute) in new[] { ("Normal", scheme.Normal), ("Focus", scheme.Focus), ("HotNormal", scheme.HotNormal), ("HotFocus", scheme.HotFocus) })
            {
                if (ThemeSwitcher.Indistinguishable(attribute.Foreground, attribute.Background))
                    problems.Add($"{slot}.{role}: {attribute.Foreground} on {attribute.Background}");
            }
        }

        Assert.True(problems.Count == 0, $"{theme}: {string.Join("; ", problems)}");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryHotkey_IsDistinguishableFromTheTextAroundIt(AppTheme theme)
    {
        var problems = new List<string>();
        foreach (var (slot, scheme) in ThemeSwitcher.SchemesFor(theme))
        {
            if (!StandsOut(scheme.HotNormal, scheme.Normal))
                problems.Add($"{slot}.HotNormal vs Normal");
            if (!StandsOut(scheme.HotFocus, scheme.Focus))
                problems.Add($"{slot}.HotFocus vs Focus");
        }

        Assert.True(problems.Count == 0, $"{theme}: {string.Join("; ", problems)}");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryRole_IsReadableAgainstItsBackground(AppTheme theme)
    {
        // WCAG AA for text; the lower 3:1 bar for what's meant to look muted. An underlined hotkey
        // is drawn in its (already checked) text color, so it passes by construction.
        var problems = new List<string>();
        foreach (var (slot, scheme) in ThemeSwitcher.SchemesFor(theme))
        {
            foreach (var (role, attribute, target) in new[]
            {
                ("Normal", scheme.Normal, ThemeSwitcher.TextContrast),
                ("Focus", scheme.Focus, ThemeSwitcher.TextContrast),
                ("HotNormal", scheme.HotNormal, ThemeSwitcher.MutedContrast),
                ("HotFocus", scheme.HotFocus, ThemeSwitcher.MutedContrast),
                ("Disabled", scheme.Disabled, ThemeSwitcher.MutedContrast),
            })
            {
                var ratio = ThemeSwitcher.ContrastRatio(attribute.Foreground, attribute.Background);
                if (ratio < target)
                    problems.Add($"{slot}.{role}: {attribute.Foreground} on {attribute.Background} = {ratio:F2}:1");
            }
        }

        Assert.True(problems.Count == 0, $"{theme}: {string.Join("; ", problems)}");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryCodeRole_IsThemedExplicitly_AndReadable(AppTheme theme)
    {
        // Terminal.Gui.Editor only uses a scheme's color for a token when the role is explicitly
        // set - a role left equal to Normal isn't, and the xshd's fixed light-background colors
        // (DarkBlue numbers, DarkGreen punctuation) showed through on dark themes instead.
        var editor = ThemeSwitcher.SchemesFor(theme)["Base"];
        var problems = new List<string>();
        foreach (var role in Enum.GetValues<VisualRole>().Where(r => r.ToString().StartsWith("Code")))
        {
            if (!editor.TryGetExplicitlySetAttributeForRole(role, out var attribute))
            {
                problems.Add($"{role} not explicit");
                continue;
            }
            var ratio = ThemeSwitcher.ContrastRatio(attribute!.Value.Foreground, attribute.Value.Background);
            if (ratio < ThemeSwitcher.MutedContrast)
                problems.Add($"{role} {ratio:F2}:1");
        }

        Assert.True(problems.Count == 0, $"{theme}: {string.Join("; ", problems)}");
    }

    [Fact]
    public void BorlandTurboC_KeepsItsRedHotkeys()
    {
        // Bright red on gray is obvious by hue but 1.01:1 in brightness - it's darkened to a
        // readable red (as the original's dark red on light gray was), not flattened into
        // underlined gray text.
        var hot = ThemeSwitcher.SchemesFor(AppTheme.BorlandTurboC)["Menu"].HotNormal;

        Assert.True(hot.Foreground.R > 0x60 && hot.Foreground.G == 0 && hot.Foreground.B == 0, hot.Foreground.ToString());
        Assert.Equal(TextStyle.None, hot.Style & TextStyle.Underline);
    }

    [Fact]
    public void SolarizedLight_KeepsItsBlueHotkeysInMenus()
    {
        // The accent blue is close to the gray text in brightness and raw RGB, but obviously a
        // different color - an earlier version of the rule underlined it into plain gray.
        var menu = ThemeSwitcher.SchemesFor(AppTheme.SolarizedLight)["Menu"];

        Assert.Equal(new Color(0x26, 0x8B, 0xD2), menu.HotNormal.Foreground);
        Assert.Equal(TextStyle.None, menu.HotNormal.Style & TextStyle.Underline);
    }

    [Fact]
    public void SolarizedLight_UnderlinesTheFocusedMenuHotkey_InsteadOfDrawingItInTheFocusColor()
    {
        // The bug this guards: the focused item's hotkey was blue on the same blue - invisible.
        var menu = ThemeSwitcher.SchemesFor(AppTheme.SolarizedLight)["Menu"];

        Assert.Equal(menu.Focus.Foreground, menu.HotFocus.Foreground);
        Assert.NotEqual(TextStyle.None, menu.HotFocus.Style & TextStyle.Underline);
    }

    [Fact]
    public void SchemesFor_CoversEverySlotOfEveryTheme()
    {
        foreach (var theme in Enum.GetValues<AppTheme>())
            Assert.Equal(Slots.Order(), ThemeSwitcher.SchemesFor(theme).Keys.Order());
    }

    private static bool StandsOut(GuiAttribute hotkey, GuiAttribute text) =>
        !ThemeSwitcher.Indistinguishable(hotkey.Foreground, text.Foreground)
        || (hotkey.Style & TextStyle.Underline) != 0 && (text.Style & TextStyle.Underline) == 0;
}
