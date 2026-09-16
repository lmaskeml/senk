using System.Diagnostics;
using System.Text;

namespace AndroidManager.Core.Services;

public static class ErofsPackRunner
{
    public static async Task<(bool Success, string Message)> PackAsync(
        string sourceDirectory,
        string outputImagePath,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceDirectory))
            return (false, $"EROFS kaynak klasörü yok: {sourceDirectory}");

        var tool = ErofsUtilsPathResolver.ResolveMkfsErofsPath();
        if (tool is null)
        {
            var target = ErofsUtilsPathResolver.GetBundledToolsDirectory();
            return (false,
                "mkfs.erofs bulunamadı.\n\n" +
                "Android 15 vendor yeniden paketlemek için sekaiacg erofs-utils paketinden " +
                $"mkfs.erofs.exe dosyasını şuraya kopyalayın:\n{target}");
        }

        var parent = Path.GetDirectoryName(outputImagePath);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        if (File.Exists(outputImagePath))
            File.Delete(outputImagePath);

        var toolDir = Path.GetDirectoryName(tool) ?? Directory.GetCurrentDirectory();
        var args = BuildArguments(outputImagePath, sourceDirectory);
        var result = await RunAsync(tool, args, toolDir, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return (false,
                $"EROFS paketleme başarısız:\n{result.Output}\n\nAraç: {tool}\nKomut: mkfs.erofs {args}");
        }

        if (!File.Exists(outputImagePath) || new FileInfo(outputImagePath).Length == 0)
            return (false, "mkfs.erofs çıktı dosyası oluşmadı.");

        return (true, "patched vendor.img hazır.");
    }

    /// <summary>
    /// mkfs.erofs 1.8: <c>[OPTIONS] FILE SOURCE</c> — <c>-o</c> yok (extract.erofs ile karıştırılmamalı).
    /// </summary>
    public static string BuildArguments(string outputImagePath, string sourceDirectory)
    {
        var file = Quote(ToCygwinPath(outputImagePath));
        var source = Quote(ToCygwinPath(sourceDirectory));
        return $"-zlz4hc --mount-point=/ {file} {source}";
    }

    public static string ToCygwinPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length >= 2 && full[1] == ':')
        {
            var drive = char.ToLowerInvariant(full[0]);
            var rest = full[2..].Replace('\\', '/');
            if (!rest.StartsWith('/'))
                rest = "/" + rest;
            return $"/cygdrive/{drive}{rest}";
        }

        return full.Replace('\\', '/');
    }

    private static string Quote(string value) =>
        value.Contains('"') ? value : $"\"{value}\"";

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
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(psi);
        if (process is null)
            return (false, "mkfs.erofs başlatılamadı.");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var combined = string.Join(
            Environment.NewLine,
            new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return (process.ExitCode == 0, combined);
    }
}
