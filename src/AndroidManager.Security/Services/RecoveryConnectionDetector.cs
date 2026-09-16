using AndroidManager.Core.Models;

namespace AndroidManager.Security.Services;

public static class RecoveryConnectionDetector
{
    public static bool IsRecoveryAdbState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return false;

        return state.Contains("recovery", StringComparison.OrdinalIgnoreCase)
               || state.Contains("sideload", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeCustomRecovery(string? propsOrOutput)
    {
        if (string.IsNullOrWhiteSpace(propsOrOutput))
            return false;

        return propsOrOutput.Contains("twrp", StringComparison.OrdinalIgnoreCase)
               || propsOrOutput.Contains("orangefox", StringComparison.OrdinalIgnoreCase)
               || propsOrOutput.Contains("orange fox", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeRecoveryBootMode(string? propsOrOutput)
    {
        if (string.IsNullOrWhiteSpace(propsOrOutput))
            return false;

        if (LooksLikeCustomRecovery(propsOrOutput))
            return true;

        return propsOrOutput.Contains("recovery", StringComparison.OrdinalIgnoreCase);
    }

    public static DeviceConnectionMode FromDevice(ConnectedDevice? device, string? props)
    {
        if (device is not null && IsRecoveryAdbState(device.State))
            return DeviceConnectionMode.Recovery;

        if (LooksLikeRecoveryBootMode(props))
            return DeviceConnectionMode.Recovery;

        return device is null ? DeviceConnectionMode.Offline : DeviceConnectionMode.Adb;
    }
}
