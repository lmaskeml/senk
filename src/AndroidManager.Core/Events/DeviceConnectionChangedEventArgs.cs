using AndroidManager.Core.Models;

namespace AndroidManager.Core.Events;

public sealed class DeviceConnectionChangedEventArgs : EventArgs
{
    public required ConnectedDevice Device { get; init; }
    public required bool IsConnected { get; init; }
}
