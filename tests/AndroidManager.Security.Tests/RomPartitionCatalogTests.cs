using AndroidManager.Core.Models;
using AndroidManager.Security.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class RomPartitionCatalogTests
{
    private const string LsAl = """
        total 0
        lrwxrwxrwx 1 root root 16 1970-01-01 00:00 boot -> /dev/block/sde11
        lrwxrwxrwx 1 root root 16 1970-01-01 00:00 boot_a -> /dev/block/sde11
        lrwxrwxrwx 1 root root 16 1970-01-01 00:00 boot_b -> /dev/block/sde32
        lrwxrwxrwx 1 root root 16 1970-01-01 00:00 modemst1 -> /dev/block/sdf1
        lrwxrwxrwx 1 root root 16 1970-01-01 00:00 nvdata -> /dev/block/sdf4
        lrwxrwxrwx 1 root root 16 1970-01-01 00:00 super -> /dev/block/sda16
        lrwxrwxrwx 1 root root 16 1970-01-01 00:00 userdata -> /dev/block/sda17
        """;

    private const string Proc = """
        major minor  #blocks  name
           8       11    196608 sde11
           8       32    196608 sde32
           8       65     32768 sdf1
           8       68     32768 sdf4
           8        0   8388608 sda16
           8        1  11010048 sda17
        """;

    [Fact]
    public void Classify_KernelAndImeiAndSuper()
    {
        Assert.Equal(RomBackupCategory.KernelRecovery, RomPartitionCatalog.Classify("boot_a"));
        Assert.Equal(RomBackupCategory.RadioImei, RomPartitionCatalog.Classify("nvdata"));
        Assert.Equal(RomBackupCategory.SuperSystem, RomPartitionCatalog.Classify("super"));
        Assert.Null(RomPartitionCatalog.Classify("userdata"));
    }

    [Fact]
    public void BuildMap_SkipsUserdata_MarksBootAlias_SizesFromProc()
    {
        var map = RomPartitionCatalog.BuildMap(LsAl, Proc);

        Assert.DoesNotContain(map, p => p.Name.Equals("userdata", StringComparison.OrdinalIgnoreCase));
        var boot = Assert.Single(map, p => p.Name == "boot");
        Assert.True(boot.IsSlotAlias);
        Assert.Equal(196608L * 1024, boot.SizeBytes);

        var super = Assert.Single(map, p => p.Name == "super");
        Assert.Equal(RomBackupCategory.SuperSystem, super.Category);
        Assert.Equal(8388608L * 1024, super.SizeBytes);

        Assert.Contains(map, p => p.Name == "nvdata");
    }

    [Fact]
    public void IsSlotAlias_WhenAbExists()
    {
        string[] present = ["boot", "boot_a", "boot_b"];
        Assert.True(RomPartitionCatalog.IsSlotAlias("boot", present));
        Assert.False(RomPartitionCatalog.IsSlotAlias("boot_a", present));
    }
}
