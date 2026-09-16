using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

/// <summary>
/// payload'dan çıkan system/vendor/product imajlarından super.img üretir (lpmake).
/// Xiaomi fastbootd'de tekil system flash resize gerektirir — super doğrudan yazılır.
/// </summary>
internal static class DynamicSuperImageBuilder
{
    private static readonly string[] SuperLogicalPartitions =
    [
        "system", "system_ext", "vendor", "product", "odm"
    ];

    internal sealed record SuperImageBuildResult(bool Success, string? Path, string? ErrorMessage);

    public static async Task<SuperImageBuildResult> BuildSuperImageAsync(
        IReadOnlyDictionary<string, string> imageMap,
        string outputPath,
        long deviceSuperSizeBytes,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (imageMap.TryGetValue("super", out var existingSuper)
            && File.Exists(existingSuper)
            && new FileInfo(existingSuper).Length > 0)
        {
            return new SuperImageBuildResult(true, existingSuper, null);
        }

        if (await TryReuseCachedSuperAsync(imageMap, outputPath, cancellationToken).ConfigureAwait(false))
            return new SuperImageBuildResult(true, outputPath, null);

        _ = LpmakePathResolver.TryProvisionBundledCopy();
        var lpmake = LpmakePathResolver.ResolveLpmakePath();
        if (lpmake is null)
        {
            return new SuperImageBuildResult(false, null, BuildLpmakeMissingMessage());
        }

        var members = SuperLogicalPartitions
            .Where(imageMap.ContainsKey)
            .Select(name =>
            {
                var path = imageMap[name];
                return (Name: name, Path: path, Size: Align4096(new FileInfo(path).Length));
            })
            .Where(m => File.Exists(m.Path) && m.Size > 0)
            .ToList();

        if (members.Count == 0)
        {
            return new SuperImageBuildResult(
                false,
                null,
                "Super oluşturmak için system/vendor/product imajlarından en az biri gerekli.");
        }

        if (deviceSuperSizeBytes <= 0)
            deviceSuperSizeBytes = 0x220000000L;

        const long metadataSize = 65536;
        const int metadataSlots = 2;
        var totalPartitionBytes = members.Sum(m => m.Size);
        var groupSize = totalPartitionBytes + metadataSize * metadataSlots + 4096;
        if (groupSize > deviceSuperSizeBytes)
        {
            return new SuperImageBuildResult(
                false,
                null,
                $"Partition boyutları super alanından büyük ({totalPartitionBytes} > {deviceSuperSizeBytes}).");
        }

        var groupName = "qti_dynamic_partitions";
        var args = new StringBuilder();
        args.Append(CultureInfo.InvariantCulture, $"--device-size={deviceSuperSizeBytes} ");
        args.Append(CultureInfo.InvariantCulture, $"--metadata-size={metadataSize} --metadata-slots={metadataSlots} ");
        args.Append(CultureInfo.InvariantCulture, $"--group={groupName}:{groupSize} ");

        foreach (var (name, _, size) in members)
        {
            args.Append(CultureInfo.InvariantCulture,
                $"--partition={name}:readonly:{size}:{groupName} ");
        }

        foreach (var (name, path, _) in members)
        {
            args.Append(CultureInfo.InvariantCulture, $"--image={name}=\"{path}\" ");
        }

        args.Append("--sparse ");
        args.Append(CultureInfo.InvariantCulture, $"--output=\"{outputPath}\"");

        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.DumpingPayload,
            Percent = 52,
            Message = $"lpmake ile super.img oluşturuluyor ({members.Count} partition)…"
        });

        var run = await RunLpmakeAsync(lpmake, args.ToString(), cancellationToken).ConfigureAwait(false);
        if (!run.Success || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            var detail = string.IsNullOrWhiteSpace(run.StdErr)
                ? $"lpmake çıkış kodu: {run.ExitCode}"
                : run.StdErr.Trim();
            return new SuperImageBuildResult(
                false,
                null,
                $"lpmake super.img oluşturamadı.\n\n{detail}\n\n{BuildLpmakeMissingMessage()}");
        }

        WriteCacheManifest(imageMap, outputPath, members);
        return new SuperImageBuildResult(true, outputPath, null);
    }

    public static bool HasSuperLogicalImages(IReadOnlyDictionary<string, string> imageMap) =>
        SuperLogicalPartitions.Any(imageMap.ContainsKey);

    public static string BuildLpmakeMissingMessage()
    {
        var toolsDir = LpmakePathResolver.GetBundledToolsDirectory();

        return
            "lpmake.exe bulunamadı — crDroid payload ROM'ları super.img içermez; " +
            "Xiaomi fastbootd'de system/vendor ayrı flash edilemez (resize / locked devices).\n\n" +
            "Kurulum:\n" +
            $"PowerShell: .\\tools\\install-lpmake.ps1 -Download\n" +
            $"Hedef: {Path.Combine(toolsDir, "lpmake.exe")}\n" +
            "   veya ortam değişkeni LPMAKE_PATH";
    }

    private static long Align4096(long size) =>
        (size + 4095) / 4096 * 4096;

    private static async Task<bool> TryReuseCachedSuperAsync(
        IReadOnlyDictionary<string, string> imageMap,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            return false;

        var manifestPath = outputPath + ".manifest.json";
        if (!File.Exists(manifestPath))
            return false;

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<SuperCacheManifest>(json);
            if (manifest?.Members is null || manifest.Members.Count == 0)
                return false;

            foreach (var member in manifest.Members)
            {
                if (!imageMap.TryGetValue(member.Name, out var path) || !File.Exists(path))
                    return false;

                var info = new FileInfo(path);
                if (info.Length != member.Size || info.LastWriteTimeUtc.Ticks != member.LastWriteUtcTicks)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteCacheManifest(
        IReadOnlyDictionary<string, string> imageMap,
        string outputPath,
        IReadOnlyList<(string Name, string Path, long Size)> members)
    {
        var manifest = new SuperCacheManifest
        {
            BuiltUtc = DateTime.UtcNow,
            Members = members.Select(m =>
            {
                var info = new FileInfo(imageMap[m.Name]);
                return new SuperCacheMember
                {
                    Name = m.Name,
                    Size = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
                };
            }).ToList()
        };

        var manifestPath = outputPath + ".manifest.json";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
    }

    private static async Task<(bool Success, int ExitCode, string StdErr)> RunLpmakeAsync(
        string lpmakePath,
        string arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = lpmakePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
            return (false, -1, "lpmake process başlatılamadı");

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
        _ = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode == 0, process.ExitCode, stderr.ToString());
    }

    private sealed class SuperCacheManifest
    {
        public DateTime BuiltUtc { get; set; }
        public List<SuperCacheMember> Members { get; set; } = [];
    }

    private sealed class SuperCacheMember
    {
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
        public long LastWriteUtcTicks { get; set; }
    }
}
