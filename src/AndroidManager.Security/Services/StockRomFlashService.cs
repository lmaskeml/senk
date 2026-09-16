using System.Diagnostics;
using System.Text;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Security.Services;

public sealed class StockRomFlashService : IStockRomFlashService
{
    public StockRomFolderInfo Inspect(string folderPath)
    {
        var root = Path.GetFullPath(folderPath);
        var images = Path.Combine(root, "images");
        var magisk = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "Magisk*.zip").OrderByDescending(f => f).FirstOrDefault()
            : null;
        var twrp = Path.Combine(images, "twrp.img");

        return new StockRomFolderInfo
        {
            FolderPath = root,
            ImagesPath = images,
            HasBoot = File.Exists(Path.Combine(images, "boot.img")),
            HasSuper = File.Exists(Path.Combine(images, "super.img")),
            HasVendorBoot = File.Exists(Path.Combine(images, "vendor_boot.img")),
            HasTwrp = File.Exists(twrp),
            TwrpPath = File.Exists(twrp) ? twrp : null,
            HasMagisk = magisk is not null,
            MagiskPath = magisk,
            HasFlashAll = File.Exists(Path.Combine(root, "flash_all.bat")),
            HasInjectScript = File.Exists(Path.Combine(root, "inject-twrp.sh")),
            HasMagiskboot = File.Exists(Path.Combine(root, "tools", "magiskboot"))
        };
    }

    public StockRomFolderInfo StageScripts(string folderPath)
    {
        var root = Path.GetFullPath(folderPath);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "images"));
        Directory.CreateDirectory(Path.Combine(root, "tools"));

        var scripts = ResolveScriptsDirectory();
        CopyIfExists(Path.Combine(scripts, "flash_all.bat"), Path.Combine(root, "flash_all.bat"));
        CopyIfExists(Path.Combine(scripts, "inject-twrp.sh"), Path.Combine(root, "inject-twrp.sh"));
        CopyIfExists(
            Path.Combine(scripts, "tools", "magiskboot"),
            Path.Combine(root, "tools", "magiskboot"));

        return Inspect(root);
    }

    public StockRomFolderInfo CopyTwrpImage(string folderPath, string twrpImagePath)
    {
        var images = Path.Combine(Path.GetFullPath(folderPath), "images");
        Directory.CreateDirectory(images);
        File.Copy(twrpImagePath, Path.Combine(images, "twrp.img"), overwrite: true);
        return Inspect(folderPath);
    }

    public StockRomFolderInfo CopyMagiskZip(string folderPath, string magiskZipPath)
    {
        var dest = Path.Combine(Path.GetFullPath(folderPath), "Magisk-v30.7.zip");
        File.Copy(magiskZipPath, dest, overwrite: true);
        return Inspect(folderPath);
    }

    public async Task<DeviceToolResult> FlashAsync(
        string folderPath,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var info = StageScripts(folderPath);
        if (!info.IsValid)
        {
            return new DeviceToolResult
            {
                Success = false,
                Message = "Geçerli Xiaomi fastboot ROM klasörü değil. images\\boot.img ve images\\super.img gerekli."
            };
        }

        var bat = Path.Combine(info.FolderPath, "flash_all.bat");
        if (!File.Exists(bat))
        {
            return new DeviceToolResult
            {
                Success = false,
                Message = "flash_all.bat kopyalanamadı."
            };
        }

        var toolsDir = Path.GetDirectoryName(PlatformToolsPathResolver.ResolveFastbootPath());
        log?.Report("flash_all.bat başlıyor: " + info.FolderPath);

        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/c flash_all.bat",
            WorkingDirectory = info.FolderPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["AM_NOPAUSE"] = "1";
        if (!string.IsNullOrWhiteSpace(toolsDir) && Directory.Exists(toolsDir))
            start.Environment["AM_PLATFORM_TOOLS"] = toolsDir;

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                log?.Report(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                log?.Report(e.Data);
        };
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        if (!process.Start())
        {
            return new DeviceToolResult { Success = false, Message = "flash_all.bat başlatılamadı." };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var reg = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        var exit = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var ok = exit == 0;
        log?.Report(ok ? "flash_all.bat bitti." : $"flash_all.bat çıkış kodu: {exit}");
        return new DeviceToolResult
        {
            Success = ok,
            Message = ok
                ? "Stock flash tamam. Kurulum ekranı açılmalı."
                : $"flash_all.bat hata ile bitti (kod {exit})."
        };
    }

    private static string ResolveScriptsDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, "Scripts"),
                     Path.Combine(baseDir, "AndroidManager.Security", "Scripts")
                 })
        {
            if (File.Exists(Path.Combine(candidate, "flash_all.bat")))
                return candidate;
        }

        return Path.Combine(baseDir, "Scripts");
    }

    private static void CopyIfExists(string source, string dest)
    {
        if (!File.Exists(source))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: true);
    }
}
