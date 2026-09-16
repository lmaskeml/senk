namespace AndroidManager.Core.Services;

/// <summary>
/// USB üzerinde adb ve fastboot süreçlerini tek kanalda serileştirir.
/// Watchdog, galeri ve flash aynı anda çalışırken daemon yarışını önler.
/// </summary>
public static class DeviceTransportGate
{
    public static SemaphoreSlim Channel { get; } = new(1, 1);

    public static async Task WaitAcquireAsync(CancellationToken cancellationToken = default)
    {
        if (!await Channel.WaitAsync(ProcessWaitHelper.DefaultAcquireTimeout, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new TimeoutException(
                "USB/ADB kanalı meşgul (başka bir aktarım sürüyor). USB kablosunu kontrol edin.");
        }
    }

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        await WaitAcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Channel.Release();
        }
    }

    public static async Task RunAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        await WaitAcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Channel.Release();
        }
    }
}
