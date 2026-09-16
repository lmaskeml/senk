using System.IO;
using System.IO.Compression;
using AndroidManager.Backup.Parsing;
using AndroidManager.Backup.ViewModels;
using AndroidManager.Core.Models;

namespace AndroidManager.Backup.Tests;

public sealed class BackupArchiveInspectorTests : IDisposable
{
    private readonly List<string> _temp = [];

    [Fact]
    public void InspectZip_MergesApkAndData_AndFlagsSystem()
    {
        var zip = CreateZip(
            ("Apps/com.whatsapp.apk", new byte[64]),
            ("Apps/com.android.settings.apk", new byte[32]),
            ("AppData/com.whatsapp.tar.gz", new byte[128]),
            ("Photos/a.jpg", new byte[8]));

        var entries = BackupArchiveInspector.Inspect(zip);

        Assert.Equal(2, entries.Count);
        var wa = Assert.Single(entries, e => e.PackageName == "com.whatsapp");
        Assert.True(wa.HasApk);
        Assert.True(wa.HasData);
        Assert.False(wa.IsLikelySystem);
        Assert.Equal(64, wa.ApkBytes);
        Assert.Equal(128, wa.DataBytes);

        var settings = Assert.Single(entries, e => e.PackageName == "com.android.settings");
        Assert.True(settings.HasApk);
        Assert.False(settings.HasData);
        Assert.True(settings.IsLikelySystem);
    }

    [Fact]
    public void InspectZip_ReadsNestedPrefixAndBackslashEntries()
    {
        var zip = CreateZip(
            ("backup_20240818/Apps\\com.miui.gallery.apk", new byte[16]),
            ("backup_20240818/AppData/com.whatsapp.tar.gz", new byte[24]));

        var entries = BackupArchiveInspector.Inspect(zip);

        Assert.Contains(entries, e => e.PackageName == "com.miui.gallery" && e.HasApk && e.IsLikelySystem);
        Assert.Contains(entries, e => e.PackageName == "com.whatsapp" && e.HasData && !e.HasApk);
    }

    [Fact]
    public void InspectZip_ReadsSplitApkFolder()
    {
        var zip = CreateZip(
            ("Apps/com.foo.game/base.apk", new byte[40]),
            ("Apps/com.foo.game/split_config.arm64_v8a.apk", new byte[10]));

        var entries = BackupArchiveInspector.Inspect(zip);
        var game = Assert.Single(entries);
        Assert.Equal("com.foo.game", game.PackageName);
        Assert.True(game.HasApk);
        Assert.Equal(50, game.ApkBytes);
    }

    [Fact]
    public void InspectDirectory_FindsAppsAndAppData()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "Apps"));
        Directory.CreateDirectory(Path.Combine(root, "AppData"));
        File.WriteAllBytes(Path.Combine(root, "Apps", "org.mozilla.firefox.apk"), new byte[20]);
        File.WriteAllBytes(Path.Combine(root, "AppData", "org.mozilla.firefox.tar.gz"), new byte[30]);

        var entries = BackupArchiveInspector.Inspect(root);
        var ff = Assert.Single(entries);
        Assert.Equal("org.mozilla.firefox", ff.PackageName);
        Assert.True(ff.HasApk);
        Assert.True(ff.HasData);
        Assert.Equal(50, ff.TotalBytes);
    }

    [Theory]
    [InlineData("Apps/com.foo.bar.apk", "Apps", "com.foo.bar.apk")]
    [InlineData(@"folder\Apps\com.foo.bar.apk", "Apps", "com.foo.bar.apk")]
    [InlineData("backup/AppData/com.foo.bar.tar.gz", "AppData", "com.foo.bar.tar.gz")]
    public void TryClassifyEntry_FindsAppsOrAppData(string fullName, string kind, string relative)
    {
        Assert.True(BackupArchiveInspector.TryClassifyEntry(fullName, out var foundKind, out var foundRelative));
        Assert.Equal(kind, foundKind);
        Assert.Equal(relative, foundRelative);
    }

    [Theory]
    [InlineData("com.android.systemui", true)]
    [InlineData("com.xiaomi.discover", true)]
    [InlineData("com.whatsapp", false)]
    [InlineData("org.telegram.messenger", false)]
    public void IsLikelySystemPackage_UsesPrefixes(string package, bool expected) =>
        Assert.Equal(expected, BackupArchiveInspector.IsLikelySystemPackage(package));

    [Fact]
    public void PackageFromRelative_StripsTarGz()
    {
        var pkg = BackupArchiveInspector.PackageFromRelative("AppData", "com.foo.bar.tar.gz");
        Assert.Equal("com.foo.bar", pkg);
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                else if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
                // ignore locked temp
            }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"am-bakdir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _temp.Add(dir);
        return dir;
    }

    private string CreateZip(params (string Name, byte[] Data)[] files)
    {
        var path = Path.Combine(Path.GetTempPath(), $"am-bak-{Guid.NewGuid():N}.zip");
        _temp.Add(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in files)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(data);
        }

        return path;
    }
}

public sealed class RestoreAppItemViewModelTests
{
    [Fact]
    public void ContentsLabel_IncludesApkDataSystem()
    {
        var vm = new RestoreAppItemViewModel(new BackupAppEntry
        {
            PackageName = "com.android.settings",
            HasApk = true,
            HasData = true,
            IsLikelySystem = true,
            ApkBytes = 10,
            DataBytes = 20
        });

        Assert.Equal("APK · Veri · Sistem", vm.ContentsLabel);
        Assert.Equal(SizeFormatter.Format(30), vm.SizeLabel);
    }
}
