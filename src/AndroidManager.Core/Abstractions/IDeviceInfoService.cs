using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IDeviceInfoService
{
    Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default);
    Task<BatteryInfo> GetBatteryInfoAsync(CancellationToken cancellationToken = default);
    Task<StorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken = default);
    Task<RamInfo> GetRamInfoAsync(CancellationToken cancellationToken = default);
    Task<CpuInfo> GetCpuUsageAsync(CancellationToken cancellationToken = default);
    Task<DeviceDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default);
    Task<string> SaveDiagnosticsReportAsync(DeviceDiagnostics diagnostics, CancellationToken cancellationToken = default);
}
