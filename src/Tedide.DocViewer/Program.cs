using Serilog;
using Tedide.DocViewer;
using Tedide.Theming;
using Terminal.Gui.App;

// Same logging as Tedide.App (its own file, beside Tedide.App's in the same folder): without it a
// crash left no trace at all - a Terminal.Gui app's console window just vanishes - and the only
// way to find out why was Windows' own Event Log.
var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tedide", "logs");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.File(Path.Combine(logDirectory, "docviewer-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

// IsTerminating is always true for this event - it can't prevent the crash, only record it.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception - process is terminating (IsTerminating={IsTerminating})", e.IsTerminating);
    Log.CloseAndFlush();
};

Log.Information("Tedide DocViewer starting up");

using var database = new DocDatabase(Path.Combine(AppContext.BaseDirectory, "Docs.db"));

Application.Init();
try
{
    // The theme still applies if it can't be saved; it just isn't remembered - see ThemeSwitcher.SaveFailed.
    ThemeSwitcher.SaveFailed += ex => Log.Error(ex, "Could not save the theme setting");
    ThemeSwitcher.Apply(ThemeSettings.Load().Theme, persist: false);
    Application.Run(new DocViewerShell(database));
}
finally
{
    Application.Shutdown();
    Log.Information("Tedide DocViewer shutting down");
    Log.CloseAndFlush();
}
