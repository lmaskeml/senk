using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Security.Root;

public sealed class DeviceProfileAnalyzer
{
    private readonly IAdbService _adb;
    private readonly IDeviceIdentityService _identity;
    private readonly RootManager _rootManager;
    private readonly ILogger _logger;

    public DeviceProfileAnalyzer(
        IAdbService adb,
        IDeviceIdentityService identity,
        RootManager rootManager,
        ILogger? logger = null)
    {
        _adb = adb;
        _identity = identity;
        _rootManager = rootManager;
        _logger = logger ?? Log.ForContext<DeviceProfileAnalyzer>();
    }

    public async Task<DeviceProfile> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        var device = _adb.SelectedDevice
            ?? throw new InvalidOperationException("Cihaz bağlı değil");

        async Task<string> Prop(string name) =>
            (await _adb.ExecuteShellAsync($"getprop {name}", cancellationToken).ConfigureAwait(false)).Trim();

        var manufacturer = await Prop("ro.product.manufacturer").ConfigureAwait(false);
        var model = await Prop("ro.product.model").ConfigureAwait(false);
        var codename = await Prop("ro.product.device").ConfigureAwait(false);
        var androidVer = await Prop("ro.build.version.release").ConfigureAwait(false);
        var apiStr = await Prop("ro.build.version.sdk").ConfigureAwait(false);
        var chipset = await Prop("ro.hardware").ConfigureAwait(false);
        var arch = await Prop("ro.product.cpu.abi").ConfigureAwait(false);
        var slot = await Prop("ro.boot.slot_suffix").ConfigureAwait(false);
        var treble = await Prop("ro.treble.enabled").ConfigureAwait(false);

        _ = int.TryParse(apiStr, out var api);

        var partitions = await ListPartitionsAsync(cancellationToken).ConfigureAwait(false);
        var hasInitBoot = partitions.Any(p => p.Contains("init_boot", StringComparison.OrdinalIgnoreCase));
        var hasVendorBoot = partitions.Any(p => p.Contains("vendor_boot", StringComparison.OrdinalIgnoreCase));
        var hasRecovery = partitions.Any(p => p.Equals("recovery", StringComparison.OrdinalIgnoreCase));
        var hasVbmeta = partitions.Any(p => p.Contains("vbmeta", StringComparison.OrdinalIgnoreCase));

        var recoveryType = await DetectRecoveryTypeAsync(cancellationToken).ConfigureAwait(false);
        var bootloaderUnlocked = await IsBootloaderUnlockedAsync(cancellationToken).ConfigureAwait(false);
        var rootStatus = await _rootManager.DetectAsync(cancellationToken).ConfigureAwait(false);

        var stableId = _identity.ComputeStableId(null, device.Serial, manufacturer, model);
        var isAb = !string.IsNullOrWhiteSpace(slot);
        var isGki = api >= 30 && treble.Equals("true", StringComparison.OrdinalIgnoreCase);

        var profile = new DeviceProfile
        {
            StableId = stableId,
            SerialNumber = device.Serial,
            Manufacturer = manufacturer,
            Model = model,
            Codename = codename,
            ApiLevel = api,
            AndroidVersion = androidVer,
            Chipset = chipset,
            Architecture = arch,
            IsAbPartition = isAb,
            HasVbmeta = hasVbmeta,
            HasInitBoot = hasInitBoot,
            HasVendorBoot = hasVendorBoot,
            HasRecovery = hasRecovery,
            IsGki = isGki,
            ActiveSlot = string.IsNullOrWhiteSpace(slot) ? "n/a" : slot.TrimStart('_'),
            BootloaderUnlocked = bootloaderUnlocked,
            IsRooted = rootStatus.IsRooted,
            CurrentRoot = rootStatus.Access,
            DetectedRecovery = recoveryType
        };

        _logger.Information("[Root] Profile {Model} API{Api} init_boot={InitBoot} recovery={Recovery}",
            profile.Model, profile.ApiLevel, profile.HasInitBoot, profile.DetectedRecovery);

        return profile with { Compatibility = RootMethodSelector.EvaluateCompatibility(profile) };
    }

    private async Task<IReadOnlyList<string>> ListPartitionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await _adb.ExecuteShellAsync(
                "ls /dev/block/by-name 2>/dev/null", cancellationToken).ConfigureAwait(false);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Root] Partition list failed");
            return [];
        }
    }

    private async Task<RecoveryType> DetectRecoveryTypeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var props = await _adb.ExecuteShellAsync(
                "getprop ro.bootmode; getprop ro.recovery.battery; getprop ro.twrp.version; getprop ro.orangefox.version; command -v twrp 2>/dev/null",
                cancellationToken).ConfigureAwait(false);

            if (props.Contains("orangefox", StringComparison.OrdinalIgnoreCase))
                return RecoveryType.OrangeFox;

            if (props.Contains("twrp", StringComparison.OrdinalIgnoreCase))
                return RecoveryType.Twrp;

            var state = _adb.SelectedDevice?.State ?? "";
            if (state.Contains("recovery", StringComparison.OrdinalIgnoreCase)
                || state.Contains("sideload", StringComparison.OrdinalIgnoreCase)
                || props.Contains("recovery", StringComparison.OrdinalIgnoreCase))
            {
                return RecoveryType.Custom;
            }

            var stock = await _adb.ExecuteShellAsync(
                "ls /system/recovery-from-boot.p 2>/dev/null",
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stock)
                && !stock.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                return RecoveryType.Stock;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Root] Recovery detection failed");
        }

        return RecoveryType.Unknown;
    }

    private async Task<bool> IsBootloaderUnlockedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var locked = (await _adb.ExecuteShellAsync("getprop ro.boot.flash.locked", cancellationToken).ConfigureAwait(false)).Trim();
            if (locked == "0") return true;
            if (locked == "1") return false;

            var verified = (await _adb.ExecuteShellAsync("getprop ro.boot.verifiedbootstate", cancellationToken).ConfigureAwait(false)).Trim();
            return verified.Equals("orange", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
