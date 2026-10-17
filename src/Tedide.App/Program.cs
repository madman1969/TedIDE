using Serilog;
using Terminal.Gui.App;
using Tedide.App;
using Tedide.Logging;

AppLog.Start("tedide");
Log.Information("Tedide starting up, {Elapsed}ms after the process started",
    (long)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds);
// Each stage logs its own duration ([Log] on Startup's methods); this is the total to first draw.
var startup = System.Diagnostics.Stopwatch.StartNew();

Startup.InitTerminal();
try
{
    Startup.RegisterHighlighting();
    Startup.ApplyTheme();
    var shell = Startup.CreateShell();
    shell.DrawComplete += FirstDraw;
    void FirstDraw(object? sender, Terminal.Gui.ViewBase.DrawEventArgs e)
    {
        shell.DrawComplete -= FirstDraw;
        Log.Debug("Startup: first draw at {Elapsed}ms", startup.ElapsedMilliseconds);
    }
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
