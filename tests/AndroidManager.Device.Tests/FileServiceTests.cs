using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Files.Services;
using NSubstitute;

namespace AndroidManager.Device.Tests;

public sealed class FileServiceTests
{
    private readonly IAdbService _adb = Substitute.For<IAdbService>();
    private readonly IAdbSyncService _sync = Substitute.For<IAdbSyncService>();

    [Fact]
    public async Task ListDirectoryAsync_WithStatOutput_ParsesAndSorts()
    {
        _adb.ExecuteShellAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("""
                directory|4096|1704067200|/sdcard/DCIM
                regular file|2048576|1704067100|/sdcard/DCIM/photo.jpg
                regular file|512|1704067050|/sdcard/DCIM/thumb.png
                """);

        var items = await CreateService().ListDirectoryAsync("/sdcard/DCIM");

        Assert.Equal(3, items.Count);
        Assert.True(items[0].IsDirectory);
        Assert.Equal("DCIM", items[0].Name);
        Assert.Equal("photo.jpg", items[1].Name);
        Assert.Equal(2_048_576L, items[1].Size);
    }

    [Fact]
    public async Task ListDirectoryAsync_WithLsOutput_FallbackParses()
    {
        _adb.ExecuteShellAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("""
                drwxrwxrwx 2 root root 4096 2024-01-01 12:00 DCIM
                -rw-rw-rw- 1 root root 2048576 2024-01-01 11:00 photo.jpg
                """);

        var items = await CreateService().ListDirectoryAsync("/sdcard");

        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsDirectory);
        Assert.False(items[1].IsDirectory);
    }

    [Fact]
    public async Task ListDirectoryAsync_EmptyOutput_ReturnsEmpty()
    {
        _adb.ExecuteShellAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var items = await CreateService().ListDirectoryAsync("/sdcard/Empty");
        Assert.Empty(items);
    }

    [Fact]
    public async Task DeleteAsync_CallsRmRf()
    {
        _adb.ExecuteShellAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(string.Empty);

        await CreateService().DeleteAsync("/sdcard/test.txt");

        await _adb.Received(1).ExecuteShellAsync(
            "rm -rf \"/sdcard/test.txt\"",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateDirectoryAsync_UsesMkdirP()
    {
        _adb.ExecuteShellAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(string.Empty);

        await CreateService().CreateDirectoryAsync("/sdcard/NewFolder");

        await _adb.Received(1).ExecuteShellAsync(
            "mkdir -p \"/sdcard/NewFolder\"",
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("/sdcard/old.txt", "new.txt", "/sdcard/new.txt")]
    [InlineData("/sdcard/DCIM/img.jpg", "photo.jpg", "/sdcard/DCIM/photo.jpg")]
    public async Task RenameAsync_CallsMv(string oldPath, string newName, string expectedNew)
    {
        _adb.ExecuteShellAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(string.Empty);

        await CreateService().RenameAsync(oldPath, newName);

        await _adb.Received(1).ExecuteShellAsync(
            $"mv \"{oldPath}\" \"{expectedNew}\"",
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("YES\n", true)]
    [InlineData("NO\n", false)]
    public async Task ExistsAsync_ReturnsExpected(string shellOutput, bool expected)
    {
        _adb.ExecuteShellAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(shellOutput);

        var result = await CreateService().ExistsAsync("/sdcard/test");
        Assert.Equal(expected, result);
    }

    private FileService CreateService() => new(_adb, _sync);
}

public sealed class FileIconResolverTests
{
    [Theory]
    [InlineData("photo.jpg", false, "🖼")]
    [InlineData("video.mp4", false, "🎬")]
    [InlineData("music.mp3", false, "🎵")]
    [InlineData("doc.pdf", false, "📄")]
    [InlineData("app.apk", false, "📦")]
    [InlineData("archive.zip", false, "🗜")]
    [InlineData("code.cs", false, "💻")]
    [InlineData("unknown.xyz", false, "📄")]
    [InlineData("folder", true, "📁")]
    public void GetEmoji_ReturnsExpected(string fileName, bool isDir, string expected) =>
        Assert.Equal(expected, FileIconResolver.GetEmoji(fileName, isDir));

    [Theory]
    [InlineData("photo.jpg", true)]
    [InlineData("image.PNG", true)]
    [InlineData("photo.webp", true)]
    [InlineData("video.mp4", false)]
    [InlineData("doc.pdf", false)]
    public void IsImage_ReturnsExpected(string fileName, bool expected) =>
        Assert.Equal(expected, FileIconResolver.IsImage(fileName));
}
