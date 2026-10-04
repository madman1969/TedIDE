namespace Tedide.App;

/// <summary>
/// Starting a task without waiting for it. A discarded task (<c>_ = DoAsync()</c>) loses its
/// exception: nothing logs it and nothing tells the user, and whatever it was updating just stops -
/// the blame text above the editor once stuck on the previous file that way. Watching it instead
/// hands any failure to a handler. Cancellation isn't a failure.
/// </summary>
internal static class BackgroundTask
{
    public static void Watch(Task task, Action<Exception> onFailure) =>
        task.ContinueWith(
            t =>
            {
                var exception = t.Exception!.GetBaseException();
                if (exception is not OperationCanceledException)
                    onFailure(exception);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
