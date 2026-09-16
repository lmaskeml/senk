using AndroidManager.Core.Abstractions;
using Serilog;

namespace AndroidManager.Security.Services;

internal sealed record FastbootWipeResult(
    bool Success,
    string Message,
    bool UserdataErased,
    bool CacheErased,
    bool MetadataErased);

/// <summary>Format Data / cache — fastboot erase (Xiaomi'de partition-size:0 olsa bile dener).</summary>
internal static class FastbootDataWiper
{
    private static readonly string[] UserdataCandidates = ["userdata", "userdata_a", "userdata_b"];
    private static readonly string[] MetadataCandidates = ["metadata", "md_udc", "metadata_a", "metadata_b"];
    private static readonly string[] CacheCandidates = ["cache", "cache_a", "cache_b"];

    public static async Task<FastbootWipeResult> WipeForFreshInstallAsync(
        string serial,
        bool formatData,
        bool wipeCache,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!formatData && !wipeCache)
        {
            return new FastbootWipeResult(true, "Fastboot wipe atlandı.", false, false, false);
        }

        var userdataErased = false;
        var metadataErased = false;
        var cacheErased = false;
        var notes = new List<string>();
        string? lastError = null;

        if (formatData)
        {
            // Xiaomi: metadata önce (FBE)
            metadataErased = await EraseFirstAvailableAsync(serial, MetadataCandidates, logger, cancellationToken)
                .ConfigureAwait(false);
            if (metadataErased)
                notes.Add("metadata silindi");

            userdataErased = await EraseFirstAvailableAsync(serial, UserdataCandidates, logger, cancellationToken)
                .ConfigureAwait(false);

            if (!userdataErased)
            {
                logger.Information("[FastbootWipe] erase userdata başarısız — fastboot -w deneniyor");
                var wipeAll = await FastbootDeviceProbe
                    .WipeUserDataAndCacheAsync(serial, cancellationToken)
                    .ConfigureAwait(false);
                if (IsEraseSuccess(wipeAll))
                {
                    userdataErased = true;
                    cacheErased = true;
                    notes.Add("fastboot -w (userdata+cache)");
                }
                else
                {
                    lastError = wipeAll.Message;
                }
            }

            if (!userdataErased)
            {
                return new FastbootWipeResult(
                    false,
                    "fastboot erase userdata başarısız.\n\n" +
                    (string.IsNullOrWhiteSpace(lastError) ? "" : lastError.Trim() + "\n\n") +
                    "Telefon fastboot ekranında ve bootloader açık olsun. " +
                    "Stok ROM flash sürüyorsa bitmesini bekleyin, ardından tekrar deneyin.",
                    false,
                    false,
                    metadataErased);
            }

            notes.Add("userdata silindi");
        }

        if ((wipeCache || formatData) && !cacheErased)
        {
            cacheErased = await EraseFirstAvailableAsync(serial, CacheCandidates, logger, cancellationToken)
                .ConfigureAwait(false);
            if (cacheErased)
                notes.Add("cache silindi");
        }

        var summary = notes.Count > 0
            ? string.Join(", ", notes)
            : "Temizlik tamamlandı.";

        return new FastbootWipeResult(true, summary, userdataErased, cacheErased, metadataErased);
    }

    private static async Task<bool> EraseFirstAvailableAsync(
        string serial,
        IReadOnlyList<string> candidates,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? lastError = null;

        foreach (var partition in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var erase = await FastbootDeviceProbe
                .ErasePartitionAsync(serial, partition, cancellationToken)
                .ConfigureAwait(false);

            if (IsEraseSuccess(erase))
            {
                logger.Information("[FastbootWipe] Erased {Partition}", partition);
                return true;
            }

            lastError = erase.Message;
            if (FastbootDeviceProbe.LooksLikeUnknownPartition(erase.Message))
            {
                logger.Debug("[FastbootWipe] {Partition} yok — sonraki aday", partition);
                continue;
            }

            logger.Warning("[FastbootWipe] erase {Partition}: {Msg}", partition, Truncate(erase.Message, 200));
        }

        if (lastError is not null)
            logger.Warning("[FastbootWipe] Tüm adaylar başarısız: {Msg}", Truncate(lastError, 300));

        return false;
    }

    private static bool IsEraseSuccess(DeviceToolResult result) =>
        result.Success || FastbootDeviceProbe.LooksLikeFastbootOk(result.Message);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
