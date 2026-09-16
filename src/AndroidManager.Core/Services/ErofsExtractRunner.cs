using System.Diagnostics;

namespace AndroidManager.Core.Services;

internal static class ErofsExtractRunner
{
    public static async Task<(bool Success, string Message, string? ExtractRoot)> ExtractAsync(
        string vendorImagePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var tool = ErofsUtilsPathResolver.ResolveExtractErofsPath();
        if (tool is null)
        {
            var target = ErofsUtilsPathResolver.GetBundledToolsDirectory();
            return (false,
                "extract.erofs bulunamadı.\n\n" +
                "Android 15 vendor EROFS formatındadır — 7-Zip/ext4 ile açılmaz.\n" +
                "sekaiacg/erofs-utils Cygwin_x86_64 zip içinden extract.erofs.exe ve cygwin1.dll dosyalarını\n" +
                $"şu klasöre kopyalayın (iç içe erofs\\erofs değil):\n{target}",
                null);
        }

        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, recursive: true);
        Directory.CreateDirectory(outputDirectory);

        var toolDir = Path.GetDirectoryName(tool) ?? Directory.GetCurrentDirectory();
        // Cygwin: -x çıktı klasörü almaz; -o gerekir. DLL için çalışma dizini araç klasörü.
        var args = $"-i \"{vendorImagePath}\" -x -o \"{outputDirectory}\"";
        var result = await RunAsync(tool, args, toolDir, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return (false,
                $"EROFS ayıklama başarısız:\n{result.Output}\n\nAraç: {tool}",
                null);
        }

        var etcDir = FindEtcDirectory(outputDirectory);
        if (etcDir is null)
        {
            return (false,
                "EROFS ayıklandı ancak etc/ klasörü bulunamadı.\n\n" +
                $"Çıktı: {outputDirectory}",
                null);
        }

        return (true, "EROFS vendor ayıklandı.", etcDir);
    }

    private static string? FindEtcDirectory(string root)
    {
        var direct = Path.Combine(root, "etc");
        if (Directory.Exists(direct))
            return root;

        var nested = Path.Combine(root, "vendor", "etc");
        if (Directory.Exists(nested))
            return Path.Combine(root, "vendor");

        foreach (var dir in Directory.EnumerateDirectories(root, "etc", SearchOption.AllDirectories))
        {
            var parent = Directory.GetParent(dir)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
                return parent;
        }

        return null;
    }

    private static async Task<(bool Success, string Output)> RunAsync(
        string executable,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
            return (false, "extract.erofs başlatılamadı.");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var combined = string.Join(
            Environment.NewLine,
            new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return (process.ExitCode == 0, combined);
    }
}
