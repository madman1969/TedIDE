using Serilog;
using Terminal.Gui.App;
using Tedide.App;
using Tedide.App.Highlighting;
using Tedide.Theming;

var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tedide", "logs");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.File(Path.Combine(logDirectory, "tedide-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

// Unhandled exceptions on a Terminal.Gui app otherwise vanish with the console window - the only
// trace is Windows' own Event Log (Application log, ".NET Runtime" source), which isn't somewhere
// most users would think to look. IsTerminating is always true for this event (this handler can't
// prevent the crash, only record it before the process goes down).
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception - process is terminating (IsTerminating={IsTerminating})", e.IsTerminating);
    Log.CloseAndFlush();
};

Log.Information("Tedide starting up");

Application.Init();
try
{
    Cc65AssemblyHighlighting.Register();
    Cc65ListingHighlighting.Register();
    Cc65LinkerMapHighlighting.Register();
    Cc65LabelsHighlighting.Register();
    Cc65CfgHighlighting.Register();
    ThemeSwitcher.Apply(ThemeSettings.Load().Theme);
    var shell = new AppShell();
    Application.Run(shell);
    shell.SaveLayoutSettings();
    shell.SaveSessionState();
}
finally
{
    Application.Shutdown();
    Log.Information("Tedide shutting down");
    Log.CloseAndFlush();
}
