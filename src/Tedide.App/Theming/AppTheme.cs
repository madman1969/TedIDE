namespace Tedide.App.Theming;

/// <summary>The IDE color themes Tedide ships with.</summary>
public enum AppTheme
{
    Vs2026Dark,
    Vs2026Light,
    BorlandTurboC,
}

public static class AppThemeExtensions
{
    public static string DisplayName(this AppTheme theme) => theme switch
    {
        AppTheme.Vs2026Dark => "VS2026 Dark",
        AppTheme.Vs2026Light => "VS2026 Light",
        AppTheme.BorlandTurboC => "Borland Turbo C",
        _ => theme.ToString(),
    };
}
