using System.IO;
using System.Text.Json;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class ConnectionPersistService : IConnectionPersistService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConnectionPersistService(ILogger? logger = null)
    {
        _logger = logger ?? Log.ForContext<ConnectionPersistService>();
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AndroidManager",
            "last_connections.json");
    }

    public async Task<IReadOnlyList<SavedConnection>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAllUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SavedConnection?> GetLastAsync(CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault();
    }

    public async Task SaveAsync(SavedConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await ReadAllUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToList();
            all.RemoveAll(c =>
                (!string.IsNullOrWhiteSpace(connection.DeviceId) &&
                 string.Equals(c.DeviceId, connection.DeviceId, StringComparison.OrdinalIgnoreCase))
                || string.Equals(c.IpAddress, connection.IpAddress, StringComparison.OrdinalIgnoreCase));
            connection.LastConnected = DateTime.Now;
            connection.LastSeen = DateTime.Now;
            if (string.IsNullOrWhiteSpace(connection.StableDeviceId) && !string.IsNullOrWhiteSpace(connection.DeviceId))
                connection.StableDeviceId = $"cid:{connection.DeviceId}";
            all.Insert(0, connection);
            if (all.Count > 10)
                all = all.Take(10).ToList();
            await PersistUnsafeAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await ReadAllUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToList();
            all.RemoveAll(c => string.Equals(c.IpAddress, ipAddress, StringComparison.OrdinalIgnoreCase));
            await PersistUnsafeAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<SavedConnection>> ReadAllUnsafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_filePath))
                return [];

            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<SavedConnection>>(json) ?? [];
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read saved connections");
            return [];
        }
    }

    private async Task PersistUnsafeAsync(List<SavedConnection> list, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(list, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist connections");
        }
    }
}
