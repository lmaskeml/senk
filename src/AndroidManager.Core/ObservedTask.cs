namespace AndroidManager.Core;

/// <summary>Fire-and-forget görevlerde yutulmuş exception'ı görünür kılar.</summary>
public static class ObservedTask
{
    public static Action<Exception>? OnError { get; set; }

    public static void Run(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsCompletedSuccessfully)
            return;

        _ = Await(task);
    }

    private static async Task Await(Task task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
        }
    }
}
