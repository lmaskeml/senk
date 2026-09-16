using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace AndroidManager.Device.Data;

public sealed class PlatformRepository : IPlatformRepository, IDisposable
{
    /// <summary>Bump when schema changes; Migrate applies incremental steps.</summary>
    internal const int CurrentSchemaVersion = 3;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _connectionString;
    private readonly ILogger _logger;

    public PlatformRepository(ILogger? logger = null)
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AndroidManager", "platform.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";
        _logger = logger ?? Log.ForContext<PlatformRepository>();
        Initialize();
    }

    private void Initialize()
    {
        using var conn = Open();
        conn.Open();
        Migrate(conn);
        _logger.Information("[Platform] Database ready schema={Version}", CurrentSchemaVersion);
    }

    private void Migrate(SqliteConnection conn)
    {
        var version = ReadUserVersion(conn);
        if (version < 1)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS RootBackups (
                        Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                        StableDeviceId TEXT    NOT NULL DEFAULT '',
                        DeviceSerial   TEXT    NOT NULL DEFAULT '',
                        DeviceModel    TEXT    NOT NULL DEFAULT '',
                        PartitionName  TEXT    NOT NULL,
                        FilePath       TEXT    NOT NULL,
                        Sha256         TEXT    NOT NULL DEFAULT '',
                        SizeBytes      INTEGER NOT NULL DEFAULT 0,
                        CreatedAt      TEXT    NOT NULL,
                        Source         TEXT    NOT NULL DEFAULT ''
                    );
                    CREATE INDEX IF NOT EXISTS idx_root_backup_stable ON RootBackups(StableDeviceId);
                    CREATE INDEX IF NOT EXISTS idx_root_backup_serial ON RootBackups(DeviceSerial);
                    """;
                cmd.ExecuteNonQuery();
            }

            SetUserVersion(conn, 1);
            _logger.Information("[Platform] Migrated schema 0 → 1");
            version = 1;
        }

        if (version < 2)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS ScanHistory (
                        SessionId     TEXT PRIMARY KEY,
                        StartedAt     TEXT NOT NULL,
                        CompletedAt   TEXT NOT NULL,
                        ThreatCount   INTEGER NOT NULL DEFAULT 0,
                        CriticalCount INTEGER NOT NULL DEFAULT 0,
                        IsClean       INTEGER NOT NULL DEFAULT 1,
                        Score         INTEGER NOT NULL DEFAULT 0,
                        DeviceModel   TEXT NOT NULL DEFAULT ''
                    );
                    CREATE INDEX IF NOT EXISTS idx_scan_history_completed ON ScanHistory(CompletedAt DESC);

                    CREATE TABLE IF NOT EXISTS QuarantineItems (
                        ThreatId            TEXT PRIMARY KEY,
                        PackageName         TEXT NOT NULL DEFAULT '',
                        Name                TEXT NOT NULL DEFAULT '',
                        FilePath            TEXT NOT NULL DEFAULT '',
                        HashSha256          TEXT NOT NULL DEFAULT '',
                        HashMd5             TEXT NOT NULL DEFAULT '',
                        QuarantineLocalPath TEXT NOT NULL DEFAULT '',
                        Severity            INTEGER NOT NULL DEFAULT 0,
                        ThreatType          INTEGER NOT NULL DEFAULT 0,
                        DetectedAt          TEXT NOT NULL,
                        PayloadJson         TEXT NOT NULL DEFAULT '',
                        UpdatedAt           TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_quarantine_package ON QuarantineItems(PackageName);
                    """;
                cmd.ExecuteNonQuery();
            }

            SetUserVersion(conn, 2);
            _logger.Information("[Platform] Migrated schema 1 → 2 (ScanHistory + QuarantineItems)");
        }

        if (version < 3)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS DevicePresence (
                        StableId              TEXT PRIMARY KEY,
                        LastSeen              TEXT NOT NULL,
                        LastKnownIp           TEXT,
                        LastKnownPort         INTEGER,
                        LastCompanionOnline   INTEGER NOT NULL DEFAULT 0,
                        LastNetworkType       TEXT,
                        LastNetworkGeneration INTEGER NOT NULL DEFAULT 0,
                        LastPortSource        TEXT
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            SetUserVersion(conn, 3);
            _logger.Information("[Platform] Migrated schema 2 → 3 (DevicePresence)");
        }
    }

    private static int ReadUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static void SetUserVersion(SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    public async Task<int> SavePartitionBackupAsync(PartitionBackupRecord record, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO RootBackups
                (StableDeviceId, DeviceSerial, DeviceModel, PartitionName, FilePath, Sha256, SizeBytes, CreatedAt, Source)
            VALUES
                (@stable, @serial, @model, @part, @path, @hash, @size, @date, @source);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@stable", record.StableDeviceId);
        cmd.Parameters.AddWithValue("@serial", record.DeviceSerial);
        cmd.Parameters.AddWithValue("@model", record.DeviceModel);
        cmd.Parameters.AddWithValue("@part", record.PartitionName);
        cmd.Parameters.AddWithValue("@path", record.FilePath);
        cmd.Parameters.AddWithValue("@hash", record.Sha256);
        cmd.Parameters.AddWithValue("@size", record.SizeBytes);
        cmd.Parameters.AddWithValue("@date", record.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@source", record.Source);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<PartitionBackupRecord>> ListPartitionBackupsAsync(string? stableDeviceId = null, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = stableDeviceId is null
            ? "SELECT * FROM RootBackups ORDER BY CreatedAt DESC"
            : "SELECT * FROM RootBackups WHERE StableDeviceId = @stable OR DeviceSerial = @stable ORDER BY CreatedAt DESC";
        if (stableDeviceId is not null)
            cmd.Parameters.AddWithValue("@stable", stableDeviceId);

        var list = new List<PartitionBackupRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new PartitionBackupRecord
            {
                Id = reader.GetInt32(0),
                StableDeviceId = reader.GetString(1),
                DeviceSerial = reader.GetString(2),
                DeviceModel = reader.GetString(3),
                PartitionName = reader.GetString(4),
                FilePath = reader.GetString(5),
                Sha256 = reader.GetString(6),
                SizeBytes = reader.GetInt64(7),
                CreatedAt = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Source = reader.GetString(9)
            });
        }

        return list;
    }

    public async Task SaveScanHistoryAsync(ScanHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ScanHistory
                (SessionId, StartedAt, CompletedAt, ThreatCount, CriticalCount, IsClean, Score, DeviceModel)
            VALUES
                (@id, @start, @end, @threats, @crit, @clean, @score, @model)
            ON CONFLICT(SessionId) DO UPDATE SET
                CompletedAt=@end, ThreatCount=@threats, CriticalCount=@crit, IsClean=@clean, Score=@score, DeviceModel=@model;
            """;
        cmd.Parameters.AddWithValue("@id", entry.SessionId);
        cmd.Parameters.AddWithValue("@start", entry.StartedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@end", entry.CompletedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@threats", entry.ThreatCount);
        cmd.Parameters.AddWithValue("@crit", entry.CriticalCount);
        cmd.Parameters.AddWithValue("@clean", entry.IsClean ? 1 : 0);
        cmd.Parameters.AddWithValue("@score", entry.Score);
        cmd.Parameters.AddWithValue("@model", entry.DeviceModel);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScanHistoryEntry>> ListScanHistoryAsync(int limit = 30, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SessionId, StartedAt, CompletedAt, ThreatCount, CriticalCount, IsClean, Score, DeviceModel
            FROM ScanHistory
            ORDER BY CompletedAt DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

        var list = new List<ScanHistoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new ScanHistoryEntry
            {
                SessionId = reader.GetString(0),
                StartedAt = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                CompletedAt = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ThreatCount = reader.GetInt32(3),
                CriticalCount = reader.GetInt32(4),
                IsClean = reader.GetInt32(5) != 0,
                Score = reader.GetInt32(6),
                DeviceModel = reader.GetString(7)
            });
        }

        return list;
    }

    public async Task UpsertQuarantineItemAsync(ThreatItem threat, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO QuarantineItems
                (ThreatId, PackageName, Name, FilePath, HashSha256, HashMd5, QuarantineLocalPath,
                 Severity, ThreatType, DetectedAt, PayloadJson, UpdatedAt)
            VALUES
                (@id, @pkg, @name, @path, @sha, @md5, @local, @sev, @type, @det, @json, @upd)
            ON CONFLICT(ThreatId) DO UPDATE SET
                PackageName=@pkg, Name=@name, FilePath=@path, HashSha256=@sha, HashMd5=@md5,
                QuarantineLocalPath=@local, Severity=@sev, ThreatType=@type, PayloadJson=@json, UpdatedAt=@upd;
            """;
        threat.IsQuarantined = true;
        cmd.Parameters.AddWithValue("@id", threat.Id);
        cmd.Parameters.AddWithValue("@pkg", threat.PackageName ?? "");
        cmd.Parameters.AddWithValue("@name", threat.Name ?? "");
        cmd.Parameters.AddWithValue("@path", threat.FilePath ?? "");
        cmd.Parameters.AddWithValue("@sha", threat.HashSha256 ?? "");
        cmd.Parameters.AddWithValue("@md5", threat.HashMd5 ?? "");
        cmd.Parameters.AddWithValue("@local", threat.QuarantineLocalPath ?? "");
        cmd.Parameters.AddWithValue("@sev", (int)threat.Severity);
        cmd.Parameters.AddWithValue("@type", (int)threat.Type);
        cmd.Parameters.AddWithValue("@det", threat.DetectedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(threat, JsonOpts));
        cmd.Parameters.AddWithValue("@upd", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveQuarantineItemAsync(string threatId, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM QuarantineItems WHERE ThreatId = @id OR PackageName = @id;";
        cmd.Parameters.AddWithValue("@id", threatId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ThreatItem>> ListQuarantineItemsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PayloadJson FROM QuarantineItems ORDER BY UpdatedAt DESC;";

        var list = new List<ThreatItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var json = reader.GetString(0);
                var item = JsonSerializer.Deserialize<ThreatItem>(json);
                if (item is not null)
                {
                    item.IsQuarantined = true;
                    list.Add(item);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[Platform] Quarantine payload parse failed");
            }
        }

        return list;
    }

    public async Task UpsertDevicePresenceAsync(DevicePresenceRecord record, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DevicePresence
                (StableId, LastSeen, LastKnownIp, LastKnownPort, LastCompanionOnline,
                 LastNetworkType, LastNetworkGeneration, LastPortSource)
            VALUES
                (@stable, @seen, @ip, @port, @online, @net, @gen, @src)
            ON CONFLICT(StableId) DO UPDATE SET
                LastSeen = excluded.LastSeen,
                LastKnownIp = excluded.LastKnownIp,
                LastKnownPort = excluded.LastKnownPort,
                LastCompanionOnline = excluded.LastCompanionOnline,
                LastNetworkType = excluded.LastNetworkType,
                LastNetworkGeneration = excluded.LastNetworkGeneration,
                LastPortSource = excluded.LastPortSource;
            """;
        cmd.Parameters.AddWithValue("@stable", record.StableId);
        cmd.Parameters.AddWithValue("@seen", record.LastSeen.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@ip", (object?)record.LastKnownIp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@port", (object?)record.LastKnownPort ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@online", record.LastCompanionOnline ? 1 : 0);
        cmd.Parameters.AddWithValue("@net", (object?)record.LastNetworkType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@gen", record.LastNetworkGeneration);
        cmd.Parameters.AddWithValue("@src", (object?)record.LastPortSource ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DevicePresenceRecord?> GetDevicePresenceAsync(
        string stableId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT StableId, LastSeen, LastKnownIp, LastKnownPort, LastCompanionOnline,
                   LastNetworkType, LastNetworkGeneration, LastPortSource
            FROM DevicePresence WHERE StableId = @stable;
            """;
        cmd.Parameters.AddWithValue("@stable", stableId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new DevicePresenceRecord
        {
            StableId = reader.GetString(0),
            LastSeen = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LastKnownIp = reader.IsDBNull(2) ? null : reader.GetString(2),
            LastKnownPort = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            LastCompanionOnline = reader.GetInt32(4) != 0,
            LastNetworkType = reader.IsDBNull(5) ? null : reader.GetString(5),
            LastNetworkGeneration = reader.GetInt32(6),
            LastPortSource = reader.IsDBNull(7) ? null : reader.GetString(7)
        };
    }

    public async Task InvalidateDevicePresenceEndpointAsync(string stableId, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE DevicePresence SET
                LastKnownIp = NULL,
                LastKnownPort = NULL,
                LastCompanionOnline = 0
            WHERE StableId = @stable;
            """;
        cmd.Parameters.AddWithValue("@stable", stableId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private SqliteConnection Open() => new(_connectionString);

    public void Dispose() { }
}
