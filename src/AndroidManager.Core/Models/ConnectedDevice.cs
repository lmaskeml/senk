namespace AndroidManager.Core.Models;

public sealed class ConnectedDevice
{
    public required string Serial { get; init; }
    public string Model { get; init; } = string.Empty;
    public string Product { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;

    /// <summary>Normal Android ADB (<c>device</c> / Online).</summary>
    public bool IsOnline => string.Equals(State, "Online", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

    /// <summary>TWRP / stock recovery. <c>adb devices</c> lists these as <c>recovery</c>, not Online.</summary>
    public bool IsRecovery => State.Contains("recovery", StringComparison.OrdinalIgnoreCase);

    /// <summary>ADB sideload session.</summary>
    public bool IsSideload => State.Contains("sideload", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bootloader / fastboot (ADB gerekmez).</summary>
    public bool IsFastboot => State.Contains("fastboot", StringComparison.OrdinalIgnoreCase)
                              || State.Contains("bootloader", StringComparison.OrdinalIgnoreCase);

    /// <summary>Shell, sideload veya fastboot transport kullanılabilir.</summary>
    public bool IsAdbReady => IsOnline || IsRecovery || IsSideload;

    /// <summary>ROM flash / kurtarma için kullanılabilir bağlantı.</summary>
    public bool IsFlashTransportReady => IsAdbReady || IsFastboot;

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Model) ? Serial : $"{Model} ({Serial})";
}
