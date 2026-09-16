using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Apps.Services;

public sealed partial class ApkAnalyzerService : IApkAnalyzerService
{
    private readonly IAdbService _adb;
    private readonly IAdbSyncService _sync;
    private readonly ILogger _logger;
    private readonly string _aaptPath;

    public ApkAnalyzerService(IAdbService adb, IAdbSyncService sync, ILogger? logger = null)
    {
        _adb = adb;
        _sync = sync;
        _logger = logger ?? Log.ForContext<ApkAnalyzerService>();
        _aaptPath = ResolveAaptPath();
    }

    public async Task<ApkAnalysisResult> AnalyzeLocalApkAsync(
        string localApkPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localApkPath) || !File.Exists(localApkPath))
            throw new FileNotFoundException("APK bulunamadı.", localApkPath);

        var size = new FileInfo(localApkPath).Length;
        var badging = await RunAaptAsync($"dump badging \"{localApkPath}\"", cancellationToken)
            .ConfigureAwait(false);
        var permissions = await RunAaptAsync($"dump permissions \"{localApkPath}\"", cancellationToken)
            .ConfigureAwait(false);

        var built = BuildFromBadging(badging, permissions, localApkPath, size);
        built = EnrichZip(built, localApkPath);

        if (!File.Exists(_aaptPath))
            built = WithNotes(built, "aapt.exe bulunamadı; ZIP özeti gösteriliyor.");

        return built;
    }

    public async Task<ApkAnalysisResult> AnalyzeInstalledPackageAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            throw new ArgumentException("Paket adı gerekli.", nameof(packageName));
        if (_adb.SelectedDevice is null)
            throw new InvalidOperationException("Cihaz bağlı değil.");

        var dump = await _adb.ExecuteShellAsync($"dumpsys package {packageName}", cancellationToken)
            .ConfigureAwait(false);
        var pathLine = await _adb.ExecuteShellAsync($"pm path {packageName}", cancellationToken)
            .ConfigureAwait(false);

        var apkPath = ExtractPmPath(pathLine);
        long apkSize = 0;
        if (!string.IsNullOrWhiteSpace(apkPath))
        {
            var sizeRaw = await _adb.ExecuteShellAsync($"stat -c %s \"{apkPath}\" 2>/dev/null", cancellationToken)
                .ConfigureAwait(false);
            long.TryParse(sizeRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out apkSize);
        }

        var result = new ApkAnalysisResult
        {
            Source = "installed",
            PackageName = packageName,
            AppLabel = FirstNonEmpty(ParseDumpValue(dump, "applicationLabel="), packageName),
            VersionName = ParseDumpValue(dump, "versionName="),
            VersionCode = ParseDumpValue(dump, "versionCode=").Split(' ', '\r', '\n')[0],
            MinSdk = ParseDumpValue(dump, "minSdk="),
            TargetSdk = ParseDumpValue(dump, "targetSdk="),
            InstallLocation = string.IsNullOrWhiteSpace(apkPath) ? "—" : apkPath,
            ApkSizeBytes = apkSize,
            Permissions = ExtractGrantedPermissions(dump),
            Activities = ExtractComponents(dump, "Activity Resolver Table:").Take(40).ToList(),
            Services = ExtractComponents(dump, "Service Resolver Table:").Take(40).ToList(),
            Receivers = ExtractComponents(dump, "Receiver Resolver Table:").Take(40).ToList(),
            Notes = "Kurulu paket dumpsys + pm path ile analiz edildi."
        };

        if (string.IsNullOrWhiteSpace(apkPath) || !File.Exists(_aaptPath))
            return result;

        var temp = Path.Combine(Path.GetTempPath(), $"am_apk_{Guid.NewGuid():N}.apk");
        try
        {
            await _sync.PullAsync(apkPath, temp, null, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                return result;

            var local = await AnalyzeLocalApkAsync(temp, cancellationToken).ConfigureAwait(false);
            return new ApkAnalysisResult
            {
                Source = "installed+aapt",
                PackageName = FirstNonEmpty(local.PackageName, result.PackageName),
                AppLabel = FirstNonEmpty(local.AppLabel, result.AppLabel),
                VersionName = FirstNonEmpty(local.VersionName, result.VersionName),
                VersionCode = FirstNonEmpty(local.VersionCode, result.VersionCode),
                MinSdk = FirstNonEmpty(local.MinSdk, result.MinSdk),
                TargetSdk = FirstNonEmpty(local.TargetSdk, result.TargetSdk),
                InstallLocation = result.InstallLocation,
                ApkSizeBytes = result.ApkSizeBytes > 0 ? result.ApkSizeBytes : local.ApkSizeBytes,
                IsDebuggable = local.IsDebuggable,
                SupportsRtl = local.SupportsRtl,
                Permissions = local.Permissions.Count > 0 ? local.Permissions : result.Permissions,
                Activities = local.Activities.Count > 0 ? local.Activities : result.Activities,
                Services = local.Services.Count > 0 ? local.Services : result.Services,
                Receivers = local.Receivers.Count > 0 ? local.Receivers : result.Receivers,
                NativeLibs = local.NativeLibs,
                LargestEntries = local.LargestEntries,
                SignerSubject = local.SignerSubject,
                CertificateIssuer = local.CertificateIssuer,
                CertificateSha256 = local.CertificateSha256,
                SigningScheme = local.SigningScheme,
                Notes = "Kurulu APK çekildi ve aapt ile incelendi."
            };
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Installed APK pull/aapt skipped for {Pkg}", packageName);
            return result;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* ignore */ }
        }
    }

    private ApkAnalysisResult BuildFromBadging(string badging, string permissionsDump, string path, long size)
    {
        var pkg = MatchGroup(badging, @"package: name='([^']+)'");
        var verName = MatchGroup(badging, @"versionName='([^']*)'");
        var verCode = MatchGroup(badging, @"versionCode='([^']*)'");
        var minSdk = MatchGroup(badging, @"sdkVersion:'([^']+)'");
        var targetSdk = MatchGroup(badging, @"targetSdkVersion:'([^']+)'");
        var label = MatchGroup(badging, @"application-label(?:-[\w-]+)?:'([^']*)'");
        if (string.IsNullOrWhiteSpace(label))
            label = MatchGroup(badging, @"application:\s+label='([^']*)'");

        var perms = new List<string>();
        foreach (Match m in UsesPermissionRegex().Matches(badging))
            perms.Add(m.Groups[1].Value);
        foreach (Match m in NamePermissionRegex().Matches(permissionsDump))
        {
            if (!perms.Contains(m.Groups[1].Value, StringComparer.Ordinal))
                perms.Add(m.Groups[1].Value);
        }

        var activities = MatchAll(badging, @"launchable-activity:\s+name='([^']+)'");
        activities.AddRange(MatchAll(badging, @"activity(?:-alias)?:\s+name='([^']+)'"));

        return new ApkAnalysisResult
        {
            Source = path,
            PackageName = pkg,
            AppLabel = string.IsNullOrWhiteSpace(label) ? pkg : label,
            VersionName = verName,
            VersionCode = verCode,
            MinSdk = minSdk,
            TargetSdk = targetSdk,
            InstallLocation = path,
            ApkSizeBytes = size,
            IsDebuggable = badging.Contains("application-debuggable", StringComparison.OrdinalIgnoreCase),
            SupportsRtl = badging.Contains("supports-rtl:'true'", StringComparison.OrdinalIgnoreCase),
            Permissions = perms.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            Activities = activities.Distinct(StringComparer.Ordinal).Take(40).ToList(),
            Notes = File.Exists(_aaptPath) ? "aapt dump badging" : "aapt yok"
        };
    }

    private static ApkAnalysisResult EnrichZip(ApkAnalysisResult result, string apkPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(apkPath);
            var libs = zip.Entries
                .Where(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
                .Select(e =>
                {
                    var parts = e.FullName.Split('/');
                    return parts.Length > 1 ? parts[1] : "";
                })
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a)
                .ToList();

            var largest = zip.Entries
                .Where(e => e.Length > 0)
                .OrderByDescending(e => e.Length)
                .Take(15)
                .Select(e => new ApkZipEntryInfo
                {
                    Name = e.FullName,
                    CompressedBytes = e.CompressedLength,
                    UncompressedBytes = e.Length
                })
                .ToList();

            var signing = ExtractSigningInfo(zip, apkPath);
            return WithZip(result, libs, largest, signing);
        }
        catch
        {
            return result;
        }
    }

    private static (string Subject, string Issuer, string Sha256, string Scheme) ExtractSigningInfo(
        ZipArchive zip, string apkPath)
    {
        var block = HasApkSigningBlockMagic(apkPath);
        var certEntry = zip.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) &&
            (e.FullName.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) ||
             e.FullName.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) ||
             e.FullName.EndsWith(".EC", StringComparison.OrdinalIgnoreCase)));

        if (certEntry is null)
        {
            return ("", "", "", block ? "v2/v3 (JAR sertifikası yok)" : "İmza bulunamadı");
        }

        try
        {
            using var stream = certEntry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0)
                return ("", "", "", "v1 (boş)");

            var cms = new SignedCms();
            cms.Decode(bytes);
            if (cms.Certificates.Count == 0)
                return ("", "", "", block ? "v1+v2/v3" : "v1 JAR");

            var cert = cms.Certificates[0];
            var sha = Convert.ToHexString(SHA256.HashData(cert.RawData));
            var scheme = block ? "v1 + v2/v3" : "v1 JAR";
            return (cert.Subject, cert.Issuer, sha, scheme);
        }
        catch
        {
            return ("", "", "", block ? "v2/v3 (PKCS çözülemedi)" : "v1 (PKCS çözülemedi)");
        }
    }

    private static bool HasApkSigningBlockMagic(string apkPath)
    {
        try
        {
            using var fs = File.OpenRead(apkPath);
            if (fs.Length < 32) return false;

            var readLen = (int)Math.Min(65536, fs.Length);
            fs.Seek(-readLen, SeekOrigin.End);
            var buf = new byte[readLen];
            var read = fs.Read(buf, 0, buf.Length);
            // "APK Sig Block 42"
            ReadOnlySpan<byte> magic = "APK Sig Block 42"u8;
            for (var i = 0; i <= read - magic.Length; i++)
            {
                if (buf.AsSpan(i, magic.Length).SequenceEqual(magic))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static ApkAnalysisResult WithZip(
        ApkAnalysisResult r,
        IReadOnlyList<string> libs,
        IReadOnlyList<ApkZipEntryInfo> largest,
        (string Subject, string Issuer, string Sha256, string Scheme) signing) => new()
    {
        Source = r.Source,
        PackageName = r.PackageName,
        AppLabel = r.AppLabel,
        VersionName = r.VersionName,
        VersionCode = r.VersionCode,
        MinSdk = r.MinSdk,
        TargetSdk = r.TargetSdk,
        InstallLocation = r.InstallLocation,
        ApkSizeBytes = r.ApkSizeBytes,
        IsDebuggable = r.IsDebuggable,
        SupportsRtl = r.SupportsRtl,
        Permissions = r.Permissions,
        Activities = r.Activities,
        Services = r.Services,
        Receivers = r.Receivers,
        NativeLibs = libs,
        LargestEntries = largest,
        SignerSubject = signing.Subject,
        CertificateIssuer = signing.Issuer,
        CertificateSha256 = signing.Sha256,
        SigningScheme = signing.Scheme,
        Notes = r.Notes
    };

    private static ApkAnalysisResult WithNotes(ApkAnalysisResult r, string notes) => new()
    {
        Source = r.Source,
        PackageName = r.PackageName,
        AppLabel = r.AppLabel,
        VersionName = r.VersionName,
        VersionCode = r.VersionCode,
        MinSdk = r.MinSdk,
        TargetSdk = r.TargetSdk,
        InstallLocation = r.InstallLocation,
        ApkSizeBytes = r.ApkSizeBytes,
        IsDebuggable = r.IsDebuggable,
        SupportsRtl = r.SupportsRtl,
        Permissions = r.Permissions,
        Activities = r.Activities,
        Services = r.Services,
        Receivers = r.Receivers,
        NativeLibs = r.NativeLibs,
        LargestEntries = r.LargestEntries,
        SignerSubject = r.SignerSubject,
        CertificateIssuer = r.CertificateIssuer,
        CertificateSha256 = r.CertificateSha256,
        SigningScheme = r.SigningScheme,
        Notes = notes
    };

    private async Task<string> RunAaptAsync(string args, CancellationToken ct)
    {
        if (!File.Exists(_aaptPath))
            return "";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _aaptPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return output;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "aapt failed: {Args}", args);
            return "";
        }
    }

    private static string ExtractPmPath(string pathLine)
    {
        foreach (var line in pathLine.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf(':');
            if (idx > 0)
                return line[(idx + 1)..].Trim();
        }

        return "";
    }

    private static List<string> ExtractGrantedPermissions(string dump)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(dump, @"android\.permission\.[\w]+|(?:[\w]+\.)+permission\.[\w\.]+"))
        {
            if (!list.Contains(m.Value, StringComparer.Ordinal))
                list.Add(m.Value);
        }

        return list.OrderBy(p => p, StringComparer.Ordinal).Take(200).ToList();
    }

    private static List<string> ExtractComponents(string dump, string marker)
    {
        var list = new List<string>();
        var idx = dump.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return list;

        var slice = dump[idx..];
        if (slice.Length > 8000)
            slice = slice[..8000];

        foreach (Match m in Regex.Matches(slice, @"([a-zA-Z0-9_.]+)/(\.?[a-zA-Z0-9_.]+)"))
        {
            var name = m.Groups[1].Value + "/" + m.Groups[2].Value;
            if (!list.Contains(name, StringComparer.Ordinal))
                list.Add(name);
        }

        return list;
    }

    private static string ParseDumpValue(string text, string key)
    {
        var idx = text.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return "";
        var remainder = text[(idx + key.Length)..];
        return remainder.Split(['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries)[0].Trim().Trim('\'', '"');
    }

    private static string MatchGroup(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static List<string> MatchAll(string text, string pattern) =>
        Regex.Matches(text, pattern)
            .Select(m => m.Groups[1].Value)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string ResolveAaptPath()
    {
        var bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "aapt.exe");
        if (File.Exists(bundled)) return bundled;

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "build-tools");
        if (Directory.Exists(local))
        {
            var candidate = Directory.GetDirectories(local)
                .OrderByDescending(d => d)
                .Select(d => Path.Combine(d, "aapt.exe"))
                .FirstOrDefault(File.Exists);
            if (candidate is not null) return candidate;
        }

        return bundled;
    }

    [GeneratedRegex(@"uses-permission(?:-sdk-\d+)?: name='([^']+)'", RegexOptions.Compiled)]
    private static partial Regex UsesPermissionRegex();

    [GeneratedRegex(@"name='(android\.permission\.[^']+)'", RegexOptions.Compiled)]
    private static partial Regex NamePermissionRegex();
}
