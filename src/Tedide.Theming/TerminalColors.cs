using Terminal.Gui.Drivers;

namespace Tedide.Theming;

/// <summary>
/// Turns full 24-bit color back on inside Windows Terminal. Terminal.Gui 2.4.17 detects Windows
/// Terminal as a legacy console (OutputBase.IsLegacyConsole) and forces 16-color mode during
/// Application.Init - even though it reports SupportsTrueColor - so every theme was drawn in
/// Windows Terminal's 16 standard colors instead of its own (Solarized Light's cream page became
/// plain gray, its beige menus bright yellow, and ThemeSwitcher's contrast adjustments were lost
/// in the rounding). Only done when WT_SESSION is set - Windows Terminal sets it for every process
/// it hosts - since a genuine old conhost window may really not handle 24-bit color.
/// </summary>
public static class TerminalColors
{
    /// <summary>Call right after Application.Init.</summary>
    public static void UseTrueColorInWindowsTerminal(IDriver? driver)
    {
        if (driver is not null && ShouldUseTrueColor(driver.Force16Colors, driver.SupportsTrueColor, Environment.GetEnvironmentVariable("WT_SESSION")))
            driver.Force16Colors = false;
    }

    internal static bool ShouldUseTrueColor(bool force16Colors, bool supportsTrueColor, string? wtSession) =>
        force16Colors && supportsTrueColor && !string.IsNullOrEmpty(wtSession);
}
