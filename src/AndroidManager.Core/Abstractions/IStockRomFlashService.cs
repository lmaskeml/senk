using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IStockRomFlashService
{
    StockRomFolderInfo Inspect(string folderPath);

    /// <summary>flash_all.bat, inject-twrp.sh ve magiskboot'u ROM klasörüne kopyalar.</summary>
    StockRomFolderInfo StageScripts(string folderPath);

    StockRomFolderInfo CopyTwrpImage(string folderPath, string twrpImagePath);

    StockRomFolderInfo CopyMagiskZip(string folderPath, string magiskZipPath);

    Task<DeviceToolResult> FlashAsync(
        string folderPath,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default);
}
