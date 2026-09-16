using AndroidManager.Device.Parsing;

namespace AndroidManager.Device.Tests;

public class DeviceOutputParsersTests
{
    [Fact]
    public void ParseBattery_ReadsLevelVoltageTemperatureAndCharging()
    {
        const string output = """
            Current Battery Service state:
              AC powered: false
              USB powered: true
              Wireless powered: false
              status: 2
              health: 2
              present: true
              level: 87
              scale: 100
              voltage: 4210
              temperature: 285
            """;

        var battery = DeviceOutputParsers.ParseBattery(output);

        Assert.Equal(87, battery.Level);
        Assert.Equal(4210, battery.Voltage);
        Assert.Equal(28.5, battery.Temperature);
        Assert.True(battery.IsCharging);
        Assert.Equal("İyi", battery.Health);
        Assert.Equal("Şarj oluyor", battery.Status);
    }

    [Fact]
    public void ParseDf_ReadsStorageColumns()
    {
        const string output = """
            Filesystem     1K-blocks    Used Available Use% Mounted on
            /dev/fuse       61032448 31234567 29797881  52% /storage/emulated
            """;

        var storage = DeviceOutputParsers.ParseDf(output);

        Assert.Equal(61_032_448, storage.TotalKb);
        Assert.Equal(31_234_567, storage.UsedKb);
        Assert.Equal(29_797_881, storage.FreeKb);
        Assert.True(storage.UsagePercent > 50);
    }

    [Fact]
    public void ParseMemInfo_ReadsTotalFreeAvailable()
    {
        const string output = """
            MemTotal:        5863424 kB
            MemFree:          312456 kB
            MemAvailable:    2100456 kB
            Buffers:           45123 kB
            """;

        var ram = DeviceOutputParsers.ParseMemInfo(output);

        Assert.Equal(5_863_424, ram.TotalKb);
        Assert.Equal(312_456, ram.FreeKb);
        Assert.Equal(2_100_456, ram.AvailableKb);
        Assert.Equal(5_863_424 - 2_100_456, ram.UsedKb);
    }

    [Fact]
    public void ParseCpuUsage_UsesIdleWhenPresent()
    {
        const string output = """
            Tasks: 312 total
            400%cpu  12%user   8%nice  30%sys 350%idle
            """;

        var usage = DeviceOutputParsers.ParseCpuUsage(output);
        Assert.Equal(12.5, usage);
    }
}
