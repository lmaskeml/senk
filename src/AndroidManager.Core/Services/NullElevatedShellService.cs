using AndroidManager.Core.Abstractions;

namespace AndroidManager.Core.Services;

/// <summary>Root yokken / Security modülü yüklenmeden varsayılan no-op.</summary>
public sealed class NullElevatedShellService : IElevatedShellService
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<string> RunAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);
}
