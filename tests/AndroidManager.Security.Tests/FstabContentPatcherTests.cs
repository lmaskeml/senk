using AndroidManager.Core.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class FstabContentPatcherTests
{
    [Theory]
    [InlineData("fstab.qcom", true)]
    [InlineData("fstab.vili", true)]
    [InlineData("charger_fstab.qcom", false)]
    [InlineData("classes.dex", false)]
    public void IsMainFstabFileName_FiltersChargerAndNonFstab(string name, bool expected) =>
        Assert.Equal(expected, FstabContentPatcher.IsMainFstabFileName(name));

    [Fact]
    public void NeedsPatch_ReturnsTrue_ForAndroid15FileEncryption()
    {
        const string userdata =
            "/dev/block/bootdevice/by-name/userdata /data f2fs noatime,nosuid,nodev,discard,inlinecrypt,reserve_root=32768 " +
            "latemount,wait,check,formattable,fileencryption=aes-256-xts:aes-256-cts:v2+inlinecrypt_optimized+wrappedkey_v0," +
            "keydirectory=/metadata/vold/metadata_encryption,metadata_encryption=aes-256-xts:wrappedkey_v0,quota,reservedsize=128M";

        Assert.True(FstabContentPatcher.NeedsPatch(userdata));
        var patched = FstabContentPatcher.PatchContent(userdata);
        Assert.DoesNotContain("fileencryption=", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata_encryption=", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("keydirectory=", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("inlinecrypt", patched, StringComparison.Ordinal);
        Assert.Contains("/data", patched, StringComparison.Ordinal);
        Assert.Contains("formattable", patched, StringComparison.Ordinal);
        Assert.Contains("quota", patched, StringComparison.Ordinal);
    }

    [Fact]
    public void IsMainFstabFileName_AcceptsFstabDefault() =>
        Assert.True(FstabContentPatcher.IsMainFstabFileName("fstab.default"));
}
