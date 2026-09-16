using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Device.Parsing;
using AndroidManager.Device.Services;
using NSubstitute;

namespace AndroidManager.Device.Tests;

public sealed class AdbConnectResultTests
{
    [Theory]
    [InlineData("connected to 192.168.1.100:5555", true)]
    [InlineData("failed to connect", false)]
    [InlineData("already connected to 192.168.1.100:5555", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSuccess_ParsesAdbConnectOutput(string? output, bool expected) =>
        Assert.Equal(expected, AdbConnectResult.IsSuccess(output));
}

public sealed class DeviceInfoServiceTests
{
    private readonly IAdbService _adb = Substitute.For<IAdbService>();

    [Fact]
    public async Task GetDeviceInfoAsync_MapsPropertiesCorrectly()
    {
        SetupProp("ro.product.manufacturer", "Samsung");
        SetupProp("ro.product.model", "Galaxy S24");
        SetupProp("ro.build.version.release", "14");
        SetupProp("ro.build.version.sdk", "34");
        SetupProp("ro.serialno", "R5CW12345");
        _adb.ExecuteShellAsync("dumpsys battery", Arg.Any<CancellationToken>())
            .Returns("level: 90\nvoltage: 4200\ntemperature: 300\nUSB powered: true");
        _adb.ExecuteShellAsync("df /sdcard", Arg.Any<CancellationToken>())
            .Returns("/dev/fuse 64000000 32000000 32000000 50% /sdcard");
        _adb.ExecuteShellAsync("cat /proc/meminfo", Arg.Any<CancellationToken>())
            .Returns("MemTotal: 8000000 kB\nMemFree: 2000000 kB\nMemAvailable: 4000000 kB");
        _adb.ExecuteShellAsync("top -bn1 -m 1", Arg.Any<CancellationToken>())
            .Returns("400%cpu  12%user   8%nice  30%sys 350%idle");

        var info = await new DeviceInfoService(_adb).GetDeviceInfoAsync();

        Assert.Equal("Samsung", info.Manufacturer);
        Assert.Equal("Galaxy S24", info.Model);
        Assert.Equal("14", info.AndroidVersion);
        Assert.Equal("34", info.ApiLevel);
        Assert.Equal("R5CW12345", info.Serial);
    }

    [Fact]
    public async Task GetBatteryInfoAsync_ParsesLevel()
    {
        _adb.ExecuteShellAsync("dumpsys battery", Arg.Any<CancellationToken>())
            .Returns("""
                Current Battery Service state:
                  USB powered: true
                  status: 2
                  level: 87
                  voltage: 4100
                  temperature: 280
                """);

        var battery = await new DeviceInfoService(_adb).GetBatteryInfoAsync();

        Assert.Equal(87, battery.Level);
        Assert.Equal(4100, battery.Voltage);
        Assert.Equal(28.0, battery.Temperature);
        Assert.True(battery.IsCharging);
    }

    [Fact]
    public async Task GetStorageInfoAsync_CalculatesUsagePercent()
    {
        _adb.ExecuteShellAsync("df /sdcard", Arg.Any<CancellationToken>())
            .Returns("""
                Filesystem     1K-blocks      Used Available Use% Mounted on
                /dev/fuse      117187500  50000000  67187500  43% /sdcard
                """);

        var storage = await new DeviceInfoService(_adb).GetStorageInfoAsync();

        Assert.Equal(117_187_500L, storage.TotalKb);
        Assert.Equal(50_000_000L, storage.UsedKb);
        Assert.InRange(storage.UsagePercent, 40, 45);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1_572_864, "1.5 MB")]
    public void FileSystemItem_SizeFormatted_FormatsCorrectly(long bytes, string expected)
    {
        var item = new FileSystemItem { Size = bytes, IsDirectory = false };
        Assert.Equal(expected, item.SizeFormatted);
    }

    private void SetupProp(string prop, string value) =>
        _adb.ExecuteShellAsync($"getprop {prop}", Arg.Any<CancellationToken>())
            .Returns(value + "\n");
}
