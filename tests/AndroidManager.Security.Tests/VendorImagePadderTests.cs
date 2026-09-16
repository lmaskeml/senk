using AndroidManager.Core.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class VendorImagePadderTests
{
    [Fact]
    public void PrepareForFlash_PadsSmallerImageToOriginalSize()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"am_pad_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var original = Path.Combine(dir, "vendor.img");
            var patched = Path.Combine(dir, "patched_vendor.img");
            File.WriteAllBytes(original, new byte[4096]);
            File.WriteAllBytes(patched, new byte[1024]);

            var result = VendorImagePadder.PrepareForFlash(patched, original, dir);

            Assert.NotEqual(patched, result);
            Assert.Equal(4096, new FileInfo(result).Length);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
