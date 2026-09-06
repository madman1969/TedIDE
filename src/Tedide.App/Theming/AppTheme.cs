namespace Tedide.App.Theming;

/// <summary>The IDE color themes Tedide ships with.</summary>
public enum AppTheme
{
    Vs2026Dark,
    Vs2026Light,
    BorlandTurboC,
    Monokai,
    Dracula,
    SolarizedDark,
    SolarizedLight,
    Commodore64,
    AmberPhosphor,
}

public static class AppThemeExtensions
{
    public static string DisplayName(this AppTheme theme) => theme switch
    {
        AppTheme.Vs2026Dark => "VS2026 Dark",
        AppTheme.Vs2026Light => "VS2026 Light",
        AppTheme.BorlandTurboC => "Borland Turbo C",
        AppTheme.Monokai => "Monokai",
        AppTheme.Dracula => "Dracula",
        AppTheme.SolarizedDark => "Solarized Dark",
        AppTheme.SolarizedLight => "Solarized Light",
        AppTheme.Commodore64 => "Commodore 64",
        AppTheme.AmberPhosphor => "Amber Phosphor",
        _ => theme.ToString(),
    };
}
