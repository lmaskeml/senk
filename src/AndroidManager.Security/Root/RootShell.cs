using AndroidManager.Core.Abstractions;
using Serilog;

namespace AndroidManager.Security.Root;

/// <summary>
/// Root (su) komut katmanı — Magisk / KernelSU / APatch / ADB-uid=0.
/// </summary>
public sealed class RootShell
{
    private static readonly string[] SuCandidates =
    [
        "/system/bin/su",
        "/system/xbin/su",
        "/sbin/su",
        "/vendor/bin/su",
        "/system/bin/.su",
        "/su/bin/su",
        "/debug_ramdisk/su",
        "/debug_ramdisk/bin/su",
        "/debug_ramdisk/magisk",
        "/system/bin/magisk",
        "/sbin/magisk",
        "/data/adb/magisk/su",
        "/data/adb/ksud",
        "/data/local/bin/su",
        "/data/local/xbin/su",
        "/data/local/tmp/su",
        "/data/local/su"
    ];

    private readonly IAdbService _adb;
    private readonly ILogger _logger;
    private bool? _shellIsRoot;

    public string? FoundSuPath { get; private set; }

    public RootShell(IAdbService adb, ILogger? logger = null)
    {
        _adb = adb;
        _logger = logger ?? Log.ForContext<RootShell>();
    }

    public void Reset()
    {
        FoundSuPath = null;
        _shellIsRoot = null;
    }

    public async Task<bool> IsAdbShellRootAsync(CancellationToken cancellationToken = default)
    {
        if (_shellIsRoot is bool cached)
            return cached;

        try
        {
            var id = await _adb.ExecuteShellAsync("id", cancellationToken).ConfigureAwait(false);
            _shellIsRoot = IsUidZero(id);
            return _shellIsRoot.Value;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "ADB id sorgusu başarısız");
            _shellIsRoot = false;
            return false;
        }
    }

    public async Task<string?> FindSuAsync(CancellationToken cancellationToken = default)
    {
        if (FoundSuPath is not null)
            return FoundSuPath;

        try
        {
            var which = (await _adb.ExecuteShellAsync(
                "command -v su 2>/dev/null || which su 2>/dev/null || echo NOT_FOUND",
                cancellationToken).ConfigureAwait(false)).Trim();
            if (LooksLikeBinaryPath(which))
            {
                FoundSuPath = which.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                return FoundSuPath;
            }
        }
        catch
        {
            // PATH'te yok
        }

        foreach (var path in SuCandidates)
        {
            try
            {
                var result = (await _adb.ExecuteShellAsync(
                    $"ls \"{path}\" 2>/dev/null || echo MISSING", cancellationToken).ConfigureAwait(false)).Trim();
                if (result is not "MISSING" && !string.IsNullOrWhiteSpace(result) &&
                    !result.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                {
                    FoundSuPath = path;
                    return path;
                }
            }
            catch
            {
                // konum yok
            }
        }

        return null;
    }

    public Task<string> RunRootCommandAsync(string command, CancellationToken cancellationToken = default) =>
        RunRootCommandAsync(command, TimeSpan.FromSeconds(45), cancellationToken);

    public async Task<string> RunRootCommandAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        if (await IsAdbShellRootAsync(timeoutCts.Token).ConfigureAwait(false))
        {
            try
            {
                return await _adb.ExecuteShellAsync(command, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.Warning("Root komutu zaman aşımı: {Cmd}", Truncate(command));
                return "";
            }
        }

        var escaped = command.Replace("'", "'\\''", StringComparison.Ordinal);
        string[] invocations =
        [
            $"su -c '{escaped}'",
            $"su 0 -c '{escaped}'",
            $"su 0 sh -c '{escaped}'",
            $"magisk su -c '{escaped}'"
        ];

        foreach (var invocation in invocations)
        {
            try
            {
                var output = await _adb.ExecuteShellAsync(invocation, timeoutCts.Token)
                    .ConfigureAwait(false);
                if (!IsDenied(output))
                    return output;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.Warning("Root komutu zaman aşımı: {Cmd}", Truncate(command));
                return "";
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Root invocation failed: {Inv}", invocation);
            }
        }

        return "";
    }

    public async Task<bool> TestRootAsync(CancellationToken cancellationToken = default)
    {
        if (await IsAdbShellRootAsync(cancellationToken).ConfigureAwait(false))
            return true;

        var id = await RunRootCommandAsync("id", cancellationToken).ConfigureAwait(false);
        return IsUidZero(id);
    }

    public static bool IsUidZero(string? idOutput) =>
        !string.IsNullOrWhiteSpace(idOutput) &&
        idOutput.Contains("uid=0", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeBinaryPath(string which)
    {
        if (string.IsNullOrWhiteSpace(which) ||
            which.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
            which.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return false;

        var first = which.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return first.StartsWith('/') && !first.Contains(' ');
    }

    private static string Truncate(string command) =>
        command.Length <= 60 ? command : command[..60];

    private static bool IsDenied(string output)
    {
        var o = output.Trim().ToLowerInvariant();
        return o.Contains("permission denied", StringComparison.Ordinal) ||
               o.Contains("not allowed", StringComparison.Ordinal) ||
               o.Contains("access denied", StringComparison.Ordinal) ||
               o.Contains("unknown option", StringComparison.Ordinal) ||
               o.Contains("not found", StringComparison.Ordinal) ||
               o.Contains("inaccessible", StringComparison.Ordinal) ||
               o.Contains("unable to", StringComparison.Ordinal);
    }
}
