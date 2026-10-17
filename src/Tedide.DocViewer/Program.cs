using Serilog;
using Tedide.DocViewer;
using Tedide.Logging;
using Terminal.Gui.App;

// Same logging as Tedide.App (its own file, beside Tedide.App's in the same folder): without it a
// crash left no trace at all - a Terminal.Gui app's console window just vanishes - and the only
// way to find out why was Windows' own Event Log.
AppLog.Start("docviewer");
Log.Information("Tedide DocViewer starting up, {Elapsed}ms after the process started",
    (long)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds);
// Each stage logs its own duration ([Log] on Startup's methods); this is the total to first draw.
var startup = System.Diagnostics.Stopwatch.StartNew();

// Checked before any UI exists, so the reason goes to the console (and the log) rather than as a
// raw stack trace.
var databasePath = Path.Combine(AppContext.BaseDirectory, "Docs.db");
DocDatabase database;
try
{
    database = Startup.OpenDatabase(databasePath);
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

Startup.InitTerminal();
try
{
    Startup.ApplyTheme();
    var topic = TopicArgument(args);
    // With nothing to look up, the Contents tree starts fully collapsed.
    var shell = Startup.CreateShell(database, collapseContents: topic is null);
    shell.DrawComplete += FirstDraw;
    void FirstDraw(object? sender, Terminal.Gui.ViewBase.DrawEventArgs e)
    {
        shell.DrawComplete -= FirstDraw;
        Log.Debug("Startup: first draw at {Elapsed}ms", startup.ElapsedMilliseconds);
    }
    // "--topic word": Tedide's F1 (see Tedide.Core's ContextHelp). Shown once the window is up -
    // the search dialog it may open is modal, and a heading can only be scrolled to once laid out.
    if (topic is not null)
        Application.AddTimeout(TimeSpan.Zero, () =>
        {
            shell.ShowTopic(topic);
            return false;
        });
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

static string? TopicArgument(string[] args)
{
    var index = Array.FindIndex(args, a => string.Equals(a, "--topic", StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1]) ? args[index + 1] : null;
}
