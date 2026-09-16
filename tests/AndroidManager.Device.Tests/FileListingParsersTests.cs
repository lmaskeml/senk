using AndroidManager.Files.Parsing;

namespace AndroidManager.Device.Tests;

public class FileListingParsersTests
{
    [Fact]
    public void ParseLsOutput_ParsesDirectoryAndFile()
    {
        const string raw = """
            total 16
            drwxrwx--- 2 root everybody 3452 2024-01-15 12:30 DCIM
            -rw-rw---- 1 root everybody 2048 2024-02-01 09:15 notes.txt
            """;

        var items = FileListingParsers.ParseLsOutput(raw, "/sdcard");

        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsDirectory);
        Assert.Equal("DCIM", items[0].Name);
        Assert.Equal("/sdcard/DCIM", items[0].FullPath);
        Assert.False(items[1].IsDirectory);
        Assert.Equal("notes.txt", items[1].Name);
        Assert.Equal(2048, items[1].Size);
    }

    [Fact]
    public void ParseStatOutput_ParsesPipeFormat()
    {
        const string raw = """
            directory|4096|1705312200|/sdcard/Download
            regular file|1024|1705312300|/sdcard/a.txt
            """;

        var items = FileListingParsers.ParseStatOutput(raw, "/sdcard");

        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsDirectory);
        Assert.Equal("Download", items[0].Name);
        Assert.Equal(1024, items[1].Size);
    }
}
