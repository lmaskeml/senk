using AndroidManager.Security.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class FstabForceEncryptPatcherTests
{
    private const string Sample = """
        /dev/block/by-name/userdata /data ext4 noatime,nosuid,nodev wait,check,formattable,fileencryption=software,forceencrypt=software
        """;

    [Fact]
    public void PatchContent_ReplacesForceEncryptWithEncryptable()
    {
        var patched = FstabForceEncryptPatcher.PatchContent(Sample);
        Assert.DoesNotContain("forceencrypt", patched, StringComparison.Ordinal);
        Assert.Contains("encryptable=software", patched, StringComparison.Ordinal);
    }

    [Fact]
    public void NeedsPatch_ReturnsFalse_WhenFbeAndForceEncryptGone()
    {
        const string clean =
            "/dev/block/by-name/userdata /data ext4 noatime,nosuid,nodev wait,check,formattable,encryptable=software";
        Assert.False(FstabForceEncryptPatcher.NeedsPatch(clean));
    }
}
