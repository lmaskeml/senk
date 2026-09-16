using System.Collections.Concurrent;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

/// <summary>
/// Companion APK'yı deviceidle whitelist + RUN_IN_BACKGROUND ile OEM batarya katliamından korur.
/// </summary>
public sealed class CompanionBatteryGuardService : ICompanionBatteryGuard
{
    private static readonly ConcurrentDictionary<string, byte> WhitelistAttempted =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IAdbService _adb;
    private readonly IElevatedShellService? _elevated;
    private readonly ILogger _logger;

    public CompanionBatteryGuardService(
        IAdbService adb,
        IElevatedShellService? elevated = null,
        ILogger? logger = null)
    {
        _adb = adb;
        _elevated = elevated;
        _logger = logger ?? Log.ForContext<CompanionBatteryGuardService>();
    }

    public async Task<bool> TryEnsureCompanionCanRunInBackgroundAsync(
        string? adbSerial = null,
        CancellationToken cancellationToken = default)
    {
        adbSerial ??= _adb.SelectedDevice?.Serial;
        if (string.IsNullOrWhiteSpace(adbSerial))
            return false;

        if (WhitelistAttempted.ContainsKey(adbSerial))
            return true;

        var pkg = CompanionAppInfo.PackageName;
        var anyOk = false;

        anyOk |= await RunWhitelistCommandAsync(
            adbSerial,
            $"dumpsys deviceidle whitelist +{pkg}",
            cancellationToken).ConfigureAwait(false);

        anyOk |= await RunWhitelistCommandAsync(
            adbSerial,
            $"cmd appops set {pkg} RUN_IN_BACKGROUND allow",
            cancellationToken).ConfigureAwait(false);

        if (_elevated is not null && await _elevated.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            anyOk |= await RunRootCommandAsync(
                $"dumpsys deviceidle whitelist +{pkg}",
                cancellationToken).ConfigureAwait(false);
            anyOk |= await RunRootCommandAsync(
                $"cmd appops set {pkg} RUN_IN_BACKGROUND allow",
                cancellationToken).ConfigureAwait(false);
        }

        WhitelistAttempted.TryAdd(adbSerial, 0);

        if (anyOk)
            _logger.Information("[CompanionBattery] Whitelist applied for {Pkg} on {Serial}", pkg, adbSerial);
        else
            _logger.Debug("[CompanionBattery] Whitelist commands inconclusive on {Serial}", adbSerial);

        return anyOk;
    }

    private async Task<bool> RunWhitelistCommandAsync(
        string serial,
        string shellCommand,
        CancellationToken cancellationToken)
    {
        try
        {
            var (code, output) = await _adb.RunHostAdbAsync(
                serial,
                $"shell {shellCommand}",
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);

            if (code == 0)
                return true;

            var lower = output.ToLowerInvariant();
            return lower.Contains("added", StringComparison.Ordinal)
                   || lower.Contains("allow", StringComparison.Ordinal)
                   || lower.Contains("already", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[CompanionBattery] shell failed: {Cmd}", shellCommand);
            return false;
        }
    }

    private async Task<bool> RunRootCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (_elevated is null)
            return false;

        try
        {
            var output = await _elevated.RunAsync(command, TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
            var lower = output.ToLowerInvariant();
            return !lower.Contains("error", StringComparison.Ordinal)
                   && !lower.Contains("not found", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[CompanionBattery] root failed: {Cmd}", command);
            return false;
        }
    }
}
