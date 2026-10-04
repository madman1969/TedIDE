namespace Tedide.App.Tests;

public class BackgroundTaskTests
{
    [Fact]
    public async Task Watch_HandsAFailureToTheHandler()
    {
        var reported = new TaskCompletionSource<Exception>();
        BackgroundTask.Watch(Fail(new InvalidOperationException("broken pipe")), e => reported.SetResult(e));

        var exception = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("broken pipe", exception.Message);
    }

    [Fact]
    public async Task Watch_IgnoresSuccessAndCancellation()
    {
        var reported = false;
        var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        BackgroundTask.Watch(Task.CompletedTask, _ => reported = true);
        BackgroundTask.Watch(Task.FromCanceled(cancelled.Token), _ => reported = true);
        BackgroundTask.Watch(Fail(new OperationCanceledException()), _ => reported = true);

        await Task.Delay(100);
        Assert.False(reported);
    }

    private static async Task Fail(Exception exception)
    {
        await Task.Yield();
        throw exception;
    }
}
