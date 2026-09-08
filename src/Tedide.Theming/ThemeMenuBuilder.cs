using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Tedide.Theming;

/// <summary>
/// Builds the "_Theme" menu shared by Tedide.App and Tedide.DocViewer - same nine entries,
/// clicking one applies it via <see cref="ThemeSwitcher.Apply"/>. The currently active theme's
/// entry is marked with a leading checkmark, kept in sync for the lifetime of the menu by
/// subscribing to <see cref="ThemeSwitcher.Changed"/> - including when the *other* app changes the
/// theme and this one picks it up on next launch, or (within one process) when something other
/// than this menu calls <see cref="ThemeSwitcher.Apply"/>.
/// </summary>
public static class ThemeMenuBuilder
{
    /// <summary>Checkmark prefix for the active theme; kept exactly as wide (2 cells) as the blank
    /// prefix on every other entry, so switching themes doesn't shift any item's text sideways.</summary>
    private const string CheckedPrefix = "✓ ";
    private const string UncheckedPrefix = "  ";

    // Order and mnemonic placement match the original hand-authored menu exactly (e.g. "Solarized
    // D_ark"/"Solarized Li_ght" split their mnemonic mid-word specifically to avoid colliding with
    // each other) - not regenerated from AppThemeExtensions.DisplayName(), which doesn't carry
    // mnemonic placement at all.
    private static readonly (AppTheme Theme, string Title)[] Entries =
    [
        (AppTheme.Vs2026Dark, "VS2026 _Dark"),
        (AppTheme.Vs2026Light, "VS2026 _Light"),
        (AppTheme.BorlandTurboC, "_Borland Turbo C"),
        (AppTheme.Monokai, "_Monokai"),
        (AppTheme.Dracula, "_Dracula"),
        (AppTheme.SolarizedDark, "Solarized D_ark"),
        (AppTheme.SolarizedLight, "Solarized Li_ght"),
        (AppTheme.Commodore64, "_Commodore 64"),
        (AppTheme.AmberPhosphor, "_Amber Phosphor"),
    ];

    public static MenuBarItem Build()
    {
        var items = Entries
            .Select(entry => new MenuItem(UncheckedPrefix + entry.Title, "", () => ThemeSwitcher.Apply(entry.Theme), Key.Empty))
            .ToList();

        void UpdateChecks()
        {
            for (var i = 0; i < items.Count; i++)
            {
                var prefix = Entries[i].Theme == ThemeSwitcher.Current ? CheckedPrefix : UncheckedPrefix;
                items[i].Title = prefix + Entries[i].Title;
            }
        }

        UpdateChecks();
        ThemeSwitcher.Changed += UpdateChecks;

        return new MenuBarItem("_Theme", items);
    }
}
