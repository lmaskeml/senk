using AndroidManager.Core.Models;
using AndroidManager.Security.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class RecoveryConnectionDetectorTests
{
    [Theory]
    [InlineData("Recovery", true)]
    [InlineData("recovery", true)]
    [InlineData("Sideload", true)]
    [InlineData("Online", false)]
    [InlineData("device", false)]
    public void IsRecoveryAdbState_ReadsAdbDevicesColumn(string state, bool expected) =>
        Assert.Equal(expected, RecoveryConnectionDetector.IsRecoveryAdbState(state));

    [Fact]
    public void FromDevice_TwprListedAsRecovery_IsRecoveryMode()
    {
        var device = new ConnectedDevice { Serial = "abc", State = "Recovery" };
        var mode = RecoveryConnectionDetector.FromDevice(device, "normal\nmtp,adb\n");
        Assert.Equal(DeviceConnectionMode.Recovery, mode);
        Assert.True(device.IsAdbReady);
        Assert.False(device.IsOnline);
    }

    [Fact]
    public void FromDevice_BootmodeRecoveryProp_IsRecoveryMode()
    {
        var device = new ConnectedDevice { Serial = "abc", State = "Online" };
        var mode = RecoveryConnectionDetector.FromDevice(device, "recovery\n3.7.0_12-0");
        Assert.Equal(DeviceConnectionMode.Recovery, mode);
    }
}
