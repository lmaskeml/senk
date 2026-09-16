using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Serilog;

namespace AndroidManager.Settings;

/// <summary>
/// Ücretsiz beta bitiş tarihi + saat geri alma kontrolü.
/// Bu bir lisans sunucusu değildir; .NET IL her zaman yamalanabilir.
/// </summary>
public static class BetaExpiryGuard
{
    private static readonly byte[] Entropy = "AndroidManager.beta.v1"u8.ToArray();
    private static readonly TimeSpan ClockRollbackGrace = TimeSpan.FromHours(6);

    private static readonly string StampPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidManager",
        "state.bin");

    private const string RegistryPath = @"Software\AndroidManager";
    private const string RegistryValue = "seq";

    public static bool TryContinue(out string blockReason)
    {
        blockReason = "";
        var now = DateTime.UtcNow;
        var expiry = ExpiryUtc();

        StampRead fileRead;
        StampRead registryRead;
        try
        {
            fileRead = ReadFile();
            registryRead = ReadRegistry();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Beta] Depo okunamadı");
            blockReason = "Güvenli depo okunamadı. Uygulama açılamaz.";
            return false;
        }

        if (fileRead.Status == StampStatus.Corrupt || registryRead.Status == StampStatus.Corrupt)
        {
            blockReason =
                "Kurulum kaydı bozulmuş. Deneme süresi doğrulanamadı.\n" +
                "Güncel sürümü indirin.";
            Log.Warning("[Beta] Damga bozuk: file={File} registry={Reg}", fileRead.Status, registryRead.Status);
            return false;
        }

        DateTime? last = Max(fileRead.Value, registryRead.Value);

        if (last is { } saved && now + ClockRollbackGrace < saved)
        {
            blockReason =
                "Sistem saati geri alınmış görünüyor.\n" +
                "Tarih ve saati doğru ayarlayıp uygulamayı yeniden açın.";
            Log.Warning("[Beta] Saat geri alma: now={Now:o} last={Last:o}", now, saved);
            return false;
        }

        if (now >= expiry)
        {
            blockReason =
                "Bu sürümün deneme süresi 1 Aralık 2026'da doldu.\n" +
                "Güncel sürümü kullanın.";
            Log.Warning("[Beta] Süre doldu: now={Now:o}", now);
            return false;
        }

        if (last is null || now >= last.Value)
        {
            try
            {
                Stamp(now);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Beta] Zaman damgası yazılamadı");
            }
        }

        return true;
    }

    public static void Stamp()
    {
        try
        {
            Stamp(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Beta] Zaman damgası yazılamadı");
        }
    }

    private static DateTime ExpiryUtc()
    {
        // 2026-12-01 00:00:00 UTC = 1796083200 — çalışma anında XOR (const katlanmasın).
        Span<byte> masked = [177, 102, 208, 81, 0, 0, 0, 0];
        Span<byte> key = [177, 104, 222, 58, 0, 0, 0, 0];
        var unix = BitConverter.ToInt64(masked) ^ BitConverter.ToInt64(key);
        return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
    }

    private static DateTime? Max(DateTime? a, DateTime? b)
    {
        if (a is null)
            return b;
        if (b is null)
            return a;
        return a.Value > b.Value ? a : b;
    }

    private static StampRead ReadFile()
    {
        if (!File.Exists(StampPath))
            return StampRead.Missing;

        try
        {
            var blob = File.ReadAllBytes(StampPath);
            if (blob.Length == 0)
                return StampRead.Corrupt;
            return StampRead.Ok(Decode(blob));
        }
        catch (CryptographicException)
        {
            return StampRead.Corrupt;
        }
        catch (FormatException)
        {
            return StampRead.Corrupt;
        }
        catch (IOException)
        {
            throw;
        }
    }

    private static StampRead ReadRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        if (key?.GetValue(RegistryValue) is not byte[] blob || blob.Length == 0)
            return StampRead.Missing;

        try
        {
            return StampRead.Ok(Decode(blob));
        }
        catch (CryptographicException)
        {
            return StampRead.Corrupt;
        }
        catch (FormatException)
        {
            return StampRead.Corrupt;
        }
    }

    private static void Stamp(DateTime utc)
    {
        var blob = Encode(utc);
        var dir = Path.GetDirectoryName(StampPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(StampPath, blob);

        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue(RegistryValue, blob, RegistryValueKind.Binary);
    }

    private static byte[] Encode(DateTime utc)
    {
        var payload = Encoding.UTF8.GetBytes(utc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        return ProtectedData.Protect(payload, Entropy, DataProtectionScope.CurrentUser);
    }

    private static DateTime Decode(byte[] blob)
    {
        var payload = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        var ticks = long.Parse(Encoding.UTF8.GetString(payload), CultureInfo.InvariantCulture);
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private enum StampStatus
    {
        Missing,
        Ok,
        Corrupt
    }

    private readonly struct StampRead
    {
        public StampStatus Status { get; init; }
        public DateTime? Value { get; init; }

        public static StampRead Missing => new() { Status = StampStatus.Missing };
        public static StampRead Corrupt => new() { Status = StampStatus.Corrupt };
        public static StampRead Ok(DateTime value) => new() { Status = StampStatus.Ok, Value = value };
    }
}
