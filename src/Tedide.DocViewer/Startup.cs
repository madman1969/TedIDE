using Serilog;
using Tedide.Logging;
using Tedide.Theming;
using Terminal.Gui.App;

namespace Tedide.DocViewer;

/// <summary>
/// Program.cs's startup stages, as methods so [Log] can time each one - Metalama can't weave into
/// top-level statements or local functions.
/// </summary>
internal static class Startup
{
    /// <summary>Throws <see cref="FileNotFoundException"/> if there's no Docs.db at
    /// <paramref name="path"/> - SQLite's own report of that is just "unable to open database file".</summary>
    [Log]
    public static DocDatabase OpenDatabase(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Docs.db was not found next to the program.", path);
        return new DocDatabase(path);
    }

    [Log]
    public static void InitTerminal()
    {
        Application.Init();
        TerminalColors.UseTrueColorInWindowsTerminal(Application.Driver);
    }

    [Log]
    public static void ApplyTheme()
    {
        // The theme still applies if it can't be saved; it just isn't remembered - see ThemeSwitcher.SaveFailed.
        ThemeSwitcher.SaveFailed += ex => Log.Error(ex, "Could not save the theme setting");
        ThemeSwitcher.Apply(ThemeSettings.Load().Theme, persist: false);
    }

    [Log]
    public static DocViewerShell CreateShell(DocDatabase database, bool collapseContents) => new(database, collapseContents);
}
