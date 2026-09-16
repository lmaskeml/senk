using AndroidManager.Core.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class ErofsUtilsPathResolverTests
{
    [Fact]
    public void GetBundledToolsDirectory_DoesNotNestErofsTwice()
    {
        var dir = ErofsUtilsPathResolver.GetBundledToolsDirectory();
        Assert.False(
            dir.Contains(Path.Combine("erofs", "erofs"), StringComparison.OrdinalIgnoreCase),
            dir);
        Assert.EndsWith(Path.Combine("tools", "erofs"), dir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveExtractErofsPath_FindsRepoToolsCopy()
    {
        var path = ErofsUtilsPathResolver.ResolveExtractErofsPath();
        Assert.NotNull(path);
        Assert.True(File.Exists(path), path);
        Assert.Contains("extract.erofs", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
    }
}
