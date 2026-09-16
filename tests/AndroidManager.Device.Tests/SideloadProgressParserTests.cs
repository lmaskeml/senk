using AndroidManager.Device.Parsing;

namespace AndroidManager.Device.Tests;

public sealed class SideloadProgressParserTests
{
    [Theory]
    [InlineData("serving: 'rom.zip'  (~47%)", 47)]
    [InlineData("[  100%]", 100)]
    [InlineData("Total xfer: 1.00x", 100)]
    [InlineData("Total xfer: 0.42x", null)]
    [InlineData("waiting for device", null)]
    public void TryParsePercent_ReadsAdbSideloadLines(string line, int? expected) =>
        Assert.Equal(expected, SideloadProgressParser.TryParsePercent(line));

    [Fact]
    public void IsSuccessMarker_DetectsTotalXfer() =>
        Assert.True(SideloadProgressParser.IsSuccessMarker("Total xfer: 1.00x"));

    [Fact]
    public void IsTransientDeviceError_DetectsMissingDevice() =>
        Assert.True(SideloadProgressParser.IsTransientDeviceError("error: no devices/emulators found"));
}
