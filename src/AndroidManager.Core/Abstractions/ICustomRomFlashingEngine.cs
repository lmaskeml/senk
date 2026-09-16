using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

/// <summary>
/// Payload tabanlı Custom ROM'ları fastboot / fastbootd üzerinden yükler.
/// TWRP sideload yerine payload.bin → imaj çıkarma → sıralı fastboot flash.
/// </summary>
public interface ICustomRomFlashingEngine
{
    Task<CustomRomFlashResult> FlashAsync(
        CustomRomFlashRequest request,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Zip içinden payload.bin var mı ve hangi imajlar çıkarılır — flash etmeden önizleme.</summary>
    Task<IReadOnlyList<string>> PreviewPartitionsAsync(
        string zipPath,
        CancellationToken cancellationToken = default);
}
