using System.IO;
using System.IO.Compression;
using System.Text;
using AndroidManager.Core.Models;

namespace AndroidManager.Device.Tests;

public class SplitPackageExtractorTests
{
    [Fact]
    public void IsSplitArchive_RecognizesExtensions()
    {
        Assert.True(AndroidPackageFormats.IsSplitArchive("a.xapk"));
        Assert.True(AndroidPackageFormats.IsSplitArchive("b.APKS"));
        Assert.True(AndroidPackageFormats.IsSplitArchive("c.apkm"));
        Assert.False(AndroidPackageFormats.IsSplitArchive("d.apk"));
        Assert.True(AndroidPackageFormats.IsInstallablePackage("d.apk"));
    }

    [Fact]
    public void Extract_OrdersBaseApkFirst_AndReadsManifest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "am-split-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var archive = Path.Combine(dir, "demo.xapk");

        try
        {
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                AddEntry(zip, "manifest.json", """{"package_name":"com.demo.app","name":"Demo"}""");
                AddEntry(zip, "config.arm64_v8a.apk", "split");
                AddEntry(zip, "base.apk", "base");
                AddEntry(zip, "Android/obb/com.demo.app/main.123.obb", "obb");
            }

            var contents = SplitPackageExtractor.Extract(archive);
            try
            {
                Assert.Equal("com.demo.app", contents.PackageName);
                Assert.Equal(2, contents.ApkPaths.Count);
                Assert.Equal("base.apk", Path.GetFileName(contents.ApkPaths[0]));
                Assert.Single(contents.ObbFiles);
                Assert.Equal("com.demo.app/main.123.obb", contents.ObbFiles[0].RelativeRemotePath);
            }
            finally
            {
                SplitPackageExtractor.TryDeleteDirectory(contents.ExtractDirectory);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
