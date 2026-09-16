using AndroidManager.Core.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class ErofsPackRunnerTests
{
    [Fact]
    public void BuildArguments_UsesPositionalFileAndSource_NotDashO()
    {
        var args = ErofsPackRunner.BuildArguments(
            @"C:\tmp\patched_vendor.img",
            @"C:\tmp\vendor_tree");

        Assert.DoesNotContain("-o ", args);
        Assert.Contains("-zlz4hc", args);
        Assert.Contains("/cygdrive/c/tmp/patched_vendor.img", args);
        Assert.Contains("/cygdrive/c/tmp/vendor_tree", args);
        Assert.EndsWith("\"/cygdrive/c/tmp/vendor_tree\"", args);
    }

    [Fact]
    public void ToCygwinPath_ConvertsDriveLetter()
    {
        var path = ErofsPackRunner.ToCygwinPath(@"D:\erofs\out.img");
        Assert.Equal("/cygdrive/d/erofs/out.img", path);
    }
}
