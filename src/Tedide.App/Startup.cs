using Serilog;
using Tedide.App.Highlighting;
using Tedide.Logging;
using Tedide.Theming;
using Terminal.Gui.App;

namespace Tedide.App;

/// <summary>
/// Program.cs's startup stages, as methods so [Log] can time each one - Metalama can't weave into
/// top-level statements or local functions.
/// </summary>
internal static class Startup
{
    [Log]
    public static void InitTerminal()
    {
        Application.Init();
        TerminalColors.UseTrueColorInWindowsTerminal(Application.Driver);
        // Awaits started on the UI thread come back to it - see UiSynchronizationContext.
        UiSynchronizationContext.Install();
    }

    [Log]
    public static void RegisterHighlighting()
    {
        Cc65AssemblyHighlighting.Register();
        Cc65ListingHighlighting.Register();
        Cc65LinkerMapHighlighting.Register();
        Cc65LabelsHighlighting.Register();
        Cc65CfgHighlighting.Register();
    }

    [Log]
    public static void ApplyTheme()
    {
        // Theme still applies if it can't be saved; it just isn't remembered - see ThemeSwitcher.SaveFailed.
        ThemeSwitcher.SaveFailed += ex => Log.Error(ex, "Could not save the theme setting");
        ThemeSwitcher.Apply(ThemeSettings.Load().Theme, persist: false);
    }

    [Log]
    public static AppShell CreateShell() => new();
}
