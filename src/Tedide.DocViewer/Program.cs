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

// Checked before any UI exists, so the reason goes to the console (and the log) rather than as a
// raw stack trace: SQLite reports a missing file as "unable to open database file".
var databasePath = Path.Combine(AppContext.BaseDirectory, "Docs.db");
DocDatabase database;
try
{
    if (!File.Exists(databasePath))
        throw new FileNotFoundException("Docs.db was not found next to the program.", databasePath);
    database = new DocDatabase(databasePath);
}
catch (Exception ex) when (ex is FileNotFoundException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
{
    Log.Error(ex, "Could not open the documentation database {Path}", databasePath);
    Console.Error.WriteLine($"Tedide DocViewer can't start: the documentation database '{databasePath}' could not be opened.");
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Rebuild or reinstall the Doc Viewer to restore it (see tools/Cc65DocsDbBuilder).");
    Log.CloseAndFlush();
    return 1;
}
using var _ = database;

Application.Init();
TerminalColors.UseTrueColorInWindowsTerminal(Application.Driver);
try
{
    // The theme still applies if it can't be saved; it just isn't remembered - see ThemeSwitcher.SaveFailed.
    ThemeSwitcher.SaveFailed += ex => Log.Error(ex, "Could not save the theme setting");
    ThemeSwitcher.Apply(ThemeSettings.Load().Theme, persist: false);
    var shell = new DocViewerShell(database);
    Application.Run(shell);
    try
    {
        shell.SaveLayoutSettings();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Log.Error(ex, "Could not save the layout settings");
    }
}
finally
{
    Application.Shutdown();
    Log.Information("Tedide DocViewer shutting down");
    Log.CloseAndFlush();
}

return 0;
