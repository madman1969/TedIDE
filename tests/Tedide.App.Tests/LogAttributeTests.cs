using Serilog;
using Serilog.Core;
using Serilog.Events;
using Tedide.Logging;

namespace Tedide.App.Tests;

public class LogAttributeTests
{
    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            lock (Events)
                Events.Add(logEvent);
        }
    }

    private sealed class Logged
    {
        [Log]
        public int Add(int a, int b) => a + b;

        [Log]
        public void Throw() => throw new InvalidOperationException("boom");

        [Log]
        public async Task<string> SlowAsync()
        {
            await Task.Delay(150);
            return "done";
        }

        [Log]
        public async Task CancelledAsync()
        {
            await Task.Yield();
            throw new OperationCanceledException();
        }
    }

    // Log.Logger is process-wide and other test classes run in parallel and may log through it,
    // so the assertions only look at this class's own method names.
    private static List<(string Message, long Elapsed)> Capture(string method, Action run)
    {
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            run();
        }
        finally
        {
            Log.Logger = previous;
        }

        lock (sink.Events)
            return sink.Events
                .Where(e => e.Properties.TryGetValue("Method", out var m) && m is ScalarValue { Value: string s } && s == method)
                .Select(e => (e.RenderMessage(), e.Properties.TryGetValue("Elapsed", out var v) && v is ScalarValue { Value: long ms } ? ms : -1L))
                .ToList();
    }

    [Fact]
    public void Logs_start_and_finish_and_keeps_the_return_value()
    {
        var result = 0;
        var events = Capture("Logged.Add", () => result = new Logged().Add(2, 3));

        Assert.Equal(5, result);
        Assert.Equal(2, events.Count);
        Assert.Equal("\"Logged.Add\" started", events[0].Message);
        Assert.StartsWith("\"Logged.Add\" finished in ", events[1].Message);
    }

    [Fact]
    public void Logs_a_failure_and_rethrows()
    {
        var events = Capture("Logged.Throw", () => Assert.Throws<InvalidOperationException>(new Logged().Throw));

        Assert.Equal(2, events.Count);
        Assert.Contains("failed after", events[1].Message);
        Assert.Contains("InvalidOperationException", events[1].Message);
    }

    [Fact]
    public void Times_an_async_method_until_its_task_completes()
    {
        var result = "";
        var events = Capture("Logged.SlowAsync", () => result = new Logged().SlowAsync().GetAwaiter().GetResult());

        Assert.Equal("done", result);
        Assert.Equal(2, events.Count);
        Assert.Contains("finished in", events[1].Message);
        Assert.True(events[1].Elapsed >= 100, $"Elapsed was {events[1].Elapsed}ms - timed only up to the first await?");
    }

    [Fact]
    public void Logs_cancellation_separately_from_failure()
    {
        var events = Capture("Logged.CancelledAsync",
            () => Assert.ThrowsAny<OperationCanceledException>(() => new Logged().CancelledAsync().GetAwaiter().GetResult()));

        Assert.Equal(2, events.Count);
        Assert.Contains("cancelled after", events[1].Message);
    }
}
