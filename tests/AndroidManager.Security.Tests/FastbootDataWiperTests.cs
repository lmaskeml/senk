using AndroidManager.Security.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class FastbootDataWiperTests
{
    [Theory]
    [InlineData("Erasing 'userdata' (bootloader) Partition erase successfully\nOKAY [  0.039s]")]
    [InlineData("Finished. Total time: 0.046s")]
    public void LooksLikeFastbootOk_AcceptsEraseOutput(string output) =>
        Assert.True(FastbootDeviceProbe.LooksLikeFastbootOk(output));

    [Fact]
    public void LooksLikeUnknownPartition_DetectsMissingPartition() =>
        Assert.True(FastbootDeviceProbe.LooksLikeUnknownPartition("FAILED (remote: 'unknown partition')"));
}
