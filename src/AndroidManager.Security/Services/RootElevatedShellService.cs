using AndroidManager.Core.Abstractions;
using AndroidManager.Security.Root;
using Serilog;

namespace AndroidManager.Security.Services;

public sealed class RootElevatedShellService : IElevatedShellService
{
    private readonly RootManager _root;
    private readonly ILogger _logger;

    public RootElevatedShellService(RootManager root, ILogger? logger = null)
    {
        _root = root;
        _logger = logger ?? Log.ForContext<RootElevatedShellService>();
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Root erişimi kontrol edilemedi");
            return false;
        }
    }

    public Task<string> RunAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        _root.Shell.RunRootCommandAsync(
            command,
            timeout ?? TimeSpan.FromSeconds(45),
            cancellationToken);
}