using System.IO.Compression;
using System.Text;
using AndroidManager.Security.Root;
using AndroidManager.Security.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class MagiskModulePropParserTests
{
    [Fact]
    public void TryParse_ReadsStandardModuleProp()
    {
        const string prop = """
            id=zygisk_lsposed
            name=LSPosed
            version=v1.9.2
            versionCode=6712
            author=LSPosed Developers
            description=Xposed framework
            """;

        var parsed = MagiskModulePropParser.TryParse(prop);
        Assert.NotNull(parsed);
        Assert.Equal("zygisk_lsposed", parsed.Id);
        Assert.Equal("LSPosed", parsed.Name);
        Assert.Equal("v1.9.2", parsed.Version);
        Assert.Equal("LSPosed Developers", parsed.Author);
    }

    [Fact]
    public void TryParse_RejectsMissingOrBadId()
    {
        Assert.Null(MagiskModulePropParser.TryParse("name=NoId"));
        Assert.Null(MagiskModulePropParser.TryParse("id=1bad"));
        Assert.Null(MagiskModulePropParser.TryParse(""));
    }

    [Fact]
    public void TryReadZip_ReadsRootModuleProp()
    {
        var zip = Path.Combine(Path.GetTempPath(), "am-mod-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var fs = File.Create(zip))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("module.prop");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("id=testmod\nname=Test Mod\nversion=1.0\nauthor=SeND\n");
            }

            var info = MagiskModulePropParser.TryReadZip(zip);
            Assert.NotNull(info);
            Assert.Equal("testmod", info.Id);
            Assert.Equal("Test Mod", info.Name);
            Assert.Equal("1.0", info.Version);
        }
        finally
        {
            if (File.Exists(zip))
                File.Delete(zip);
        }
    }

    [Fact]
    public void TryParse_FallsBackToFolderNameWhenIdMissing()
    {
        var parsed = MagiskModulePropParser.TryParse(
            "name=HiddenApp\nversion=1",
            "/data/adb/modules/zygisk_shamiko");
        Assert.NotNull(parsed);
        Assert.Equal("zygisk_shamiko", parsed.Id);
        Assert.Equal("HiddenApp", parsed.Name);
    }

    [Fact]
    public void ParseDeviceDump_ReadsModuleWhenManagerWouldBeHidden()
    {
        const string dump = """
            AM_ENV_BEGIN
            AM_VER=27.0
            AM_DB=1
            AM_DIR=1
            AM_REQ=com.random.hiddenstub
            AM_ENV_END
            AM_BEGIN
            AM_PATH=/data/adb/modules/busybox_ndk
            AM_DISABLED=0
            AM_REMOVE=0
            AM_UPDATE=0
            AM_PENDING=0
            name=Busybox for Android NDK
            version=1.36.1
            AM_END
            """;

        var list = MagiskModulePropParser.ParseDeviceDump(dump);
        Assert.Single(list);
        Assert.Equal("busybox_ndk", list[0].Id);
        Assert.True(list[0].IsEnabled);

        var env = MagiskModuleService.ParseEnvironment(dump, stockManagerInstalled: false);
        Assert.True(env.CorePresent);
        Assert.True(env.ManagerHidden);
        Assert.Equal("com.random.hiddenstub", env.HiddenManagerPackage);
    }

    [Fact]
    public void ParseDeviceDump_ReadsFlags()
    {
        const string dump = """
            AM_BEGIN
            AM_PATH=/data/adb/modules/testmod
            AM_DISABLED=1
            AM_REMOVE=0
            AM_UPDATE=0
            AM_PENDING=0
            id=testmod
            name=Test
            version=2
            AM_END
            """;

        var list = MagiskModulePropParser.ParseDeviceDump(dump);
        Assert.Single(list);
        Assert.Equal("testmod", list[0].Id);
        Assert.False(list[0].IsEnabled);
        Assert.False(list[0].RemovePending);
    }

    [Theory]
    [InlineData("- Device platform: arm64\n- Done\n", true)]
    [InlineData("! Installation failed", false)]
    [InlineData("This zip is not a Magisk module", false)]
    [InlineData("", false)]
    public void IsInstallSuccess_DetectsMagiskOutput(string output, bool expected)
    {
        Assert.Equal(expected, MagiskModulePropParser.IsInstallSuccess(output));
    }
}
