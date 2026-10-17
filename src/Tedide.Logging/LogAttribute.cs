using System.Diagnostics;
using Metalama.Framework.Aspects;
using Serilog;

namespace Tedide.Logging;

/// <summary>
/// Logs, at Debug level, when the method starts and when it finishes, fails or is cancelled,
/// with how long it took. Woven in at compile time by Metalama, so the method body itself carries
/// no logging or Stopwatch code. Async methods are timed until their task completes, not until
/// their first await. Meant for coarse operations (a build, opening a project, a git refresh) -
/// not for anything called per keystroke or per draw, which would flood the log.
/// </summary>
/// <remarks>
/// Failures are logged at Debug without the exception: the callers that matter (AppShell.Guard,
/// BackgroundTask.Watch) already log it at Error, and this would only repeat the stack trace.
/// </remarks>
public sealed class LogAttribute : OverrideMethodAspect
{
    public override dynamic? OverrideMethod()
    {
        var name = $"{meta.Target.Type.Name}.{meta.Target.Method.Name}";
        Log.Debug("{Method} started", name);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = meta.Proceed();
            Log.Debug("{Method} finished in {Elapsed}ms", name, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("{Method} cancelled after {Elapsed}ms", name, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug("{Method} failed after {Elapsed}ms: {Error}", name, stopwatch.ElapsedMilliseconds, ex.GetType().Name);
            throw;
        }
    }

    public override async Task<dynamic?> OverrideAsyncMethod()
    {
        var name = $"{meta.Target.Type.Name}.{meta.Target.Method.Name}";
        Log.Debug("{Method} started", name);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await meta.ProceedAsync();
            Log.Debug("{Method} finished in {Elapsed}ms", name, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("{Method} cancelled after {Elapsed}ms", name, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug("{Method} failed after {Elapsed}ms: {Error}", name, stopwatch.ElapsedMilliseconds, ex.GetType().Name);
            throw;
        }
    }
}
