using AndroidManager.Core.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public class VbmetaFlashAttemptTests
{
    [Fact]
    public void Stub_Is_4Kb_With_Avb0_And_Verification_Disabled()
    {
        var bytes = VbmetaImageHelper.CreateDisabledImage(VbmetaImageHelper.PreferredStubSizeBytes);
        Assert.Equal(4096, bytes.Length);
        Assert.Equal((byte)'A', bytes[0]);
        Assert.Equal((byte)'V', bytes[1]);
        Assert.Equal((byte)'B', bytes[2]);
        Assert.Equal((byte)'0', bytes[3]);
    }

    [Fact]
    public async Task WriteDisabledStub_Also_Writes_Vbmeta_4k_Alias()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vbmeta-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = await VbmetaImageHelper.WriteDisabledStubAsync(dir, "vbmeta", CancellationToken.None);
            Assert.True(File.Exists(path));
            Assert.Equal(4096, new FileInfo(path).Length);
            var alias = Path.Combine(dir, "_vbmeta_4k.img");
            Assert.True(File.Exists(alias));
            Assert.Equal(4096, new FileInfo(alias).Length);
            Assert.True(AvbImageHelper.HasAvbMagic(alias));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnumerateFlashAttempts_Starts_With_Rom_And_Cli_Flags()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vbmeta-try-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var rom = Path.Combine(dir, "vbmeta.img");
            var stub = Path.Combine(dir, "stub.img");
            var patched = Path.Combine(dir, "patched.img");
            File.WriteAllBytes(rom, new byte[512]);
            File.WriteAllBytes(stub, new byte[512]);
            File.WriteAllBytes(patched, new byte[512]);

            var attempts = VbmetaImageHelper
                .EnumerateFlashAttempts(patched, rom, stub, romHasAvb: true)
                .ToList();

            Assert.Equal(rom, attempts[0].ImagePath);
            Assert.True(attempts[0].DisableFlags);
            Assert.False(attempts[0].GlobalFlags);
            Assert.Equal(patched, attempts[1].ImagePath);
            Assert.False(attempts[1].DisableFlags);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GetFlashTargetCandidates_Vbmeta_Prefers_Ab_Alias()
    {
        var targets = XiaomiAbFlashProfile.GetFlashTargetCandidates("vbmeta", "a");
        Assert.Equal("vbmeta_ab", targets[0]);
        Assert.Contains("vbmeta_a", targets);
    }

    [Fact]
    public void ResolveUserImagesDirectory_Finds_Images_Subfolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vbmeta-src-" + Guid.NewGuid().ToString("N"));
        var images = Path.Combine(dir, "images");
        Directory.CreateDirectory(images);
        try
        {
            File.WriteAllBytes(Path.Combine(images, "vbmeta.img"), new byte[512]);
            Assert.Equal(images, CustomRomAvbDisableStep.ResolveUserImagesDirectory(dir));
            Assert.Equal(images, CustomRomAvbDisableStep.ResolveUserImagesDirectory(Path.Combine(images, "vbmeta.img")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IsAvbMagicError_Detects_MinimalAdb_Message()
    {
        Assert.True(AvbImageHelper.IsAvbMagicError(
            "fastboot: error: Failed to find AVB_MAGIC at offset: 0"));
        Assert.False(AvbImageHelper.IsAvbMagicError("FAILED (remote: Partition not found)"));
    }
}
