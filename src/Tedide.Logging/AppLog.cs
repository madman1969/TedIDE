using Serilog;

namespace Tedide.Logging;

/// <summary>
/// The file logging both apps start with: a daily-rolling Debug-level log in
/// %LOCALAPPDATA%\Tedide\logs, each app under its own file name prefix.
/// </summary>
public static class AppLog
{
    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tedide", "logs");

    /// <summary>Points Serilog's <see cref="Log.Logger"/> at <c>{filePrefix}-YYYYMMDD.log</c> and
    /// logs unhandled exceptions to it.</summary>
    public static void Start(string filePrefix)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(Directory, $"{filePrefix}-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        // Unhandled exceptions on a Terminal.Gui app otherwise vanish with the console window - the
        // only trace is Windows' own Event Log (Application log, ".NET Runtime" source), which isn't
        // somewhere most users would think to look. IsTerminating is always true for this event
        // (this handler can't prevent the crash, only record it before the process goes down).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception - process is terminating (IsTerminating={IsTerminating})", e.IsTerminating);
            Log.CloseAndFlush();
        };
    }
}
