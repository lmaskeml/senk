using AndroidManager.Security.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class DfeZipResolverTests
{
    [Theory]
    [InlineData("crDroidAndroid-15.0-20260806-vili-v11.17.zip", 15)]
    [InlineData("Android_14_DFE.zip", 14)]
    public void DetectAndroidMajorFromRomFileName_ParsesCommonPatterns(string name, int expected) =>
        Assert.Equal(expected, DfeZipResolver.DetectAndroidMajorFromRomFileName(name));

    [Fact]
    public void RecommendFormatDataOnly_ForAndroid15Plus() =>
        Assert.True(DfeZipResolver.RecommendFormatDataOnly(15));

    [Fact]
    public void ResolveDfeZip_PrefersUserPath()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"dfe_test_{Guid.NewGuid():N}.zip");
        File.WriteAllText(temp, "fake");
        try
        {
            Assert.Equal(temp, DfeZipResolver.ResolveDfeZip(14, temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
