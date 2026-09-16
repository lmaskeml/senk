using System.IO.Compression;
using System.Text;
using AndroidManager.Core.Models;
using AndroidManager.Security.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class CustomRomPackageInspectorTests
{
    [Fact]
    public void ParseClaimedDevices_ReadsUpdaterScriptAsserts()
    {
        const string script = """
            assert(getprop("ro.product.device") == "vili" ||
                   getprop("ro.build.product") == "vili" ||
                   getprop("ro.product.name") == "vili");
            """;

        var claimed = CustomRomPackageInspector.ParseClaimedDevices(script, metadata: null);

        Assert.Contains("vili", claimed);
        Assert.Single(claimed);
    }

    [Fact]
    public void ParseClaimedDevices_ReadsMetadataPreDevice()
    {
        const string metadata = "ota-type=AB\npre-device=vili,alioth\npost-timestamp=1";
        var claimed = CustomRomPackageInspector.ParseClaimedDevices(null, metadata);
        Assert.Contains("vili", claimed);
        Assert.Contains("alioth", claimed);
    }

    [Fact]
    public void CodenamesMatch_IgnoresRegionSuffix()
    {
        Assert.True(CustomRomPackageInspector.CodenamesMatch("vili_global", "vili"));
        Assert.True(CustomRomPackageInspector.CodenamesMatch("vili", "vili_eea"));
        Assert.False(CustomRomPackageInspector.CodenamesMatch("vili", "alioth"));
    }

    [Fact]
    public void Evaluate_MatchingCodename_IsCompatible()
    {
        var package = new CustomRomPackageInfo { ClaimedDevices = ["vili"] };
        var result = CustomRomPackageInspector.Evaluate(package, "vili");
        Assert.Equal(CustomRomCompatibility.Compatible, result.Compatibility);
        Assert.True(result.CanInstall);
    }

    [Fact]
    public void Evaluate_WrongCodename_IsBlocked()
    {
        var package = new CustomRomPackageInfo { ClaimedDevices = ["alioth"] };
        var result = CustomRomPackageInspector.Evaluate(package, "vili");
        Assert.Equal(CustomRomCompatibility.Incompatible, result.Compatibility);
        Assert.False(result.CanInstall);
    }

    [Fact]
    public void Evaluate_SmUfsWithViliFileName_IsCompatible()
    {
        var package = new CustomRomPackageInfo
        {
            FileName = "crDroidAndroid-15.0-20260806-vili-v11.17.zip",
            ClaimedDevices = ["vili"]
        };

        var result = CustomRomPackageInspector.Evaluate(package, "SM_UFS");

        Assert.Equal(CustomRomCompatibility.Compatible, result.Compatibility);
        Assert.Equal("vili", result.DeviceCodename);
        Assert.True(result.CanInstall);
    }

    [Fact]
    public void Evaluate_SmUfsWithoutFileNameHint_IsUnverifiableNotBlocked()
    {
        var package = new CustomRomPackageInfo { ClaimedDevices = ["vili"] };
        var result = CustomRomPackageInspector.Evaluate(package, "SM_UFS");
        Assert.Equal(CustomRomCompatibility.Unverifiable, result.Compatibility);
        Assert.True(result.CanInstall);
    }

    [Fact]
    public void Evaluate_EmptyAsserts_IsUnverifiable()
    {
        var package = new CustomRomPackageInfo { ClaimedDevices = [] };
        var result = CustomRomPackageInspector.Evaluate(package, "vili");
        Assert.Equal(CustomRomCompatibility.Unverifiable, result.Compatibility);
        Assert.True(result.CanInstall);
    }

    [Fact]
    public void Inspect_ReadsUpdaterScriptFromZip()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"rom-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("META-INF/com/google/android/updater-script");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("""assert(getprop("ro.product.device") == "vili");""");
            }

            var info = CustomRomPackageInspector.Inspect(zipPath);

            Assert.True(info.HasUpdaterScript);
            Assert.False(info.HasPayloadBin);
            Assert.Contains("vili", info.ClaimedDevices);
            var compatibility = CustomRomPackageInspector.Evaluate(info, "vili");
            Assert.Equal(CustomRomCompatibility.Compatible, compatibility.Compatibility);
        }
        finally
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
        }
    }
}
