using AndroidManager.Security.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class MagiskDenyListServiceTests
{
    [Fact]
    public void ParseLs_ReadsPackageAndProcessLines()
    {
        const string raw = """
            com.ykb.android|com.ykb.android
            com.ykb.android|com.ykb.android:MAIN_PROCESS
            com.ykb.android|com.ykb.android:hce
            com.akbank.android.apps.akbank_direkt|com.akbank.android.apps.akbank_direkt
            """;

        var list = MagiskDenyListService.ParseLs(raw);
        Assert.Equal(4, list.Count);
        Assert.Equal("com.ykb.android", list[0].Package);
        Assert.Equal("com.ykb.android:MAIN_PROCESS", list[1].Process);
        Assert.Contains(list, e => e.Display.Contains("hce", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseLs_IgnoresEmpty()
    {
        Assert.Empty(MagiskDenyListService.ParseLs(""));
        Assert.Empty(MagiskDenyListService.ParseLs("   \n  "));
    }
}
