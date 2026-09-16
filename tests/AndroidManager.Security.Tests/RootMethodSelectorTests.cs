using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Root;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class RootMethodSelectorTests
{
    [Fact]
    public void Recommend_PrefersMagiskWhenSupported()
    {
        var profile = new DeviceProfile
        {
            ApiLevel = 33,
            Architecture = "arm64-v8a",
            BootloaderUnlocked = true,
            HasInitBoot = true,
            IsAbPartition = true
        };

        IRootMethodProvider[] providers =
        [
            new FakeProvider(RootMethodType.Magisk, true),
            new FakeProvider(RootMethodType.KernelSU, true)
        ];

        var rec = RootMethodSelector.Recommend(profile, providers);
        Assert.Equal(RootMethodType.Magisk, rec.Method);
        Assert.Equal("init_boot", rec.PatchTarget);
    }

    [Fact]
    public void IsUidZero_DetectsAdbRootShell()
    {
        const string id =
            "uid=0(root) gid=0(root) groups=0(root),1004(input) context=u:r:su:s0";
        Assert.True(RootShell.IsUidZero(id));
        Assert.False(RootShell.IsUidZero("uid=2000(shell) gid=2000(shell)"));
        Assert.False(RootShell.IsUidZero(""));
        Assert.False(RootShell.IsUidZero(null));
    }

    private sealed class FakeProvider(RootMethodType type, bool supported) : IRootMethodProvider
    {
        public RootMethodType MethodType => type;
        public bool SupportsDevice(DeviceProfile profile) => supported;
        public Task<string> DownloadManagerApkAsync(string targetDir, IProgress<int>? progress, CancellationToken cancellationToken = default) =>
            Task.FromResult("");
        public Task<string> WaitForPatchResultAsync(string serial, string remoteBootPath, string workingDir, IProgress<RootStep>? progress, CancellationToken cancellationToken = default) =>
            Task.FromResult("");
        public Task<bool> VerifyRootAsync(string serial, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
