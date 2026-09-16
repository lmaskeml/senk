using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

public sealed class FastbootDiscoveryService : IFastbootDiscoveryService
{
    private readonly FastbootFlashRunner _runner = new();

    public string? ResolvedExecutablePath => PlatformToolsPathResolver.ResolveFastbootPath();

    public async Task<IReadOnlyList<ConnectedDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var serials = await _runner.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (serials.Count == 0)
            return [];

        var devices = new List<ConnectedDevice>(serials.Count);
        foreach (var serial in serials)
        {
            var product = await TryGetProductAsync(serial, cancellationToken).ConfigureAwait(false);
            devices.Add(new ConnectedDevice
            {
                Serial = serial,
                State = "fastboot",
                Product = product ?? "",
                Model = string.IsNullOrWhiteSpace(product) ? "Fastboot cihazı" : product
            });
        }

        return devices;
    }

    public async Task<string?> GetFirstSerialAsync(CancellationToken cancellationToken = default)
    {
        var list = await _runner.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault();
    }

    public Task<string?> GetVariableAsync(string serial, string name, CancellationToken cancellationToken = default) =>
        TryGetVarAsync(serial, name, cancellationToken);

    private async Task<string?> TryGetProductAsync(string serial, CancellationToken cancellationToken)
    {
        foreach (var key in new[] { "device", "product", "variant", "hw-revision" })
        {
            var value = await TryGetVarAsync(serial, key, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var trimmed = value.Trim();
            if (IsGenericHardwareToken(trimmed))
                continue;

            return trimmed;
        }

        // Xiaomi bootloader often only reports SM_UFS — keep it for display, not as ROM codename.
        var product = await TryGetVarAsync(serial, "product", cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(product) ? null : product.Trim();
    }

    private async Task<string?> TryGetVarAsync(string serial, string name, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await _runner.GetVarAsync(serial, name, cancellationToken).ConfigureAwait(false);
            return ParseGetVarValue(raw, name);
        }
        catch
        {
            return null;
        }
    }

    internal static string? ParseGetVarValue(string output, string variableName)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith($"{variableName}:", StringComparison.OrdinalIgnoreCase))
                return trimmed[(variableName.Length + 1)..].Trim();

            if (trimmed.Contains(':'))
                continue;

            if (!trimmed.Contains("finished", StringComparison.OrdinalIgnoreCase)
                && !trimmed.Contains("total time", StringComparison.OrdinalIgnoreCase))
                return trimmed;
        }

        return null;
    }

    private static bool IsGenericHardwareToken(string value)
    {
        // SM_UFS / "SM UFS" / sm-ufs → smufs (Xiaomi storage type, not ROM codename)
        var normalized = string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return normalized is "smufs" or "smemmc" or "ufs" or "emmc" or "nand";
    }
}
