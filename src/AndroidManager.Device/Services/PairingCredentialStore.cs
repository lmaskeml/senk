using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

/// <summary>
/// Stores pairing state per stable device id (never stores pairing codes or secrets).
/// Uses DPAPI-protected local file.
/// </summary>
public sealed class PairingCredentialStore : IPairingCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PairingCredentialStore(ILogger? logger = null)
    {
        _logger = logger ?? Log.ForContext<PairingCredentialStore>();
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AndroidManager",
            "pairing_state.dat");
    }

    public async Task<PairingState> GetPairingStateAsync(string stableId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            return PairingState.Unknown;

        var map = await ReadMapAsync(cancellationToken).ConfigureAwait(false);
        return map.TryGetValue(stableId, out var state) ? state : PairingState.Unknown;
    }

    public async Task SetPairedAsync(string stableId, CancellationToken cancellationToken = default)
    {
        await SetStateAsync(stableId, PairingState.Paired, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPairingRequiredAsync(string stableId, CancellationToken cancellationToken = default)
    {
        await SetStateAsync(stableId, PairingState.PairingRequired, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(string stableId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await ReadMapUnsafeAsync(cancellationToken).ConfigureAwait(false);
            map.Remove(stableId);
            await WriteMapUnsafeAsync(map, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SetStateAsync(string stableId, PairingState state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await ReadMapUnsafeAsync(cancellationToken).ConfigureAwait(false);
            map[stableId] = state;
            await WriteMapUnsafeAsync(map, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, PairingState>> ReadMapAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadMapUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, PairingState>> ReadMapUnsafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, PairingState>(StringComparer.OrdinalIgnoreCase);

            var encrypted = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plain);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, PairingState>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in raw)
            {
                if (Enum.TryParse<PairingState>(value, true, out var parsed))
                    result[key] = parsed;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pairing state read failed");
            return new Dictionary<string, PairingState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task WriteMapUnsafeAsync(Dictionary<string, PairingState> map, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var raw = map.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(raw, JsonOptions);
            var plain = Encoding.UTF8.GetBytes(json);
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(_filePath, encrypted, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pairing state write failed");
        }
    }
}
