using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Device.Services;

public sealed class UsbConnectionMonitor : IUsbConnectionMonitor
{
    private readonly IAdbService _adb;
    private readonly IDeviceIdentityService _identity;

    public UsbConnectionMonitor(IAdbService adb, IDeviceIdentityService identity)
    {
        _adb = adb;
        _identity = identity;
    }

    public async Task<UsbDeviceLink?> FindUsbLinkAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken = default)
    {
        var devices = await _adb.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var device in devices.Where(d => d.IsOnline && !IsWifiSerial(d.Serial)))
        {
            if (MatchesTarget(device.Serial, target))
                return new UsbDeviceLink
                {
                    Serial = device.Serial,
                    StableId = target.StableId,
                    IsOnline = true
                };
        }

        return null;
    }

    public async Task<bool> IsUsbOnlineForTargetAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken = default) =>
        await FindUsbLinkAsync(target, cancellationToken).ConfigureAwait(false) is not null;

    private bool MatchesTarget(string usbSerial, WirelessWatchTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.SerialHint)
            && string.Equals(usbSerial, target.SerialHint, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(target.StableId)
            && target.StableId.Contains(usbSerial, StringComparison.OrdinalIgnoreCase))
            return true;

        var serialStable = _identity.ComputeStableId(null, usbSerial, null, target.DeviceModel);
        if (!string.IsNullOrWhiteSpace(serialStable)
            && string.Equals(serialStable, target.StableId, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsWifiSerial(string serial) => serial.Contains(':');
}
