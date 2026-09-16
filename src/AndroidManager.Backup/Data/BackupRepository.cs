using System.Globalization;
using AndroidManager.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace AndroidManager.Backup.Data;

public sealed class BackupRepository : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public BackupRepository(string dbPath, ILogger? logger = null)
    {
        _connectionString = $"Data Source={dbPath}";
        _logger = logger ?? Log.ForContext<BackupRepository>();
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS BackupJobs (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                DeviceModel  TEXT    NOT NULL,
                DeviceSerial TEXT    NOT NULL,
                Type         INTEGER NOT NULL,
                SavePath     TEXT    NOT NULL,
                CreatedAt    TEXT    NOT NULL,
                SizeBytes    INTEGER DEFAULT 0,
                Status       INTEGER NOT NULL DEFAULT 0,
                Note         TEXT    DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_backup_serial ON BackupJobs(DeviceSerial);
            CREATE INDEX IF NOT EXISTS idx_backup_date ON BackupJobs(CreatedAt DESC);
            """;
        cmd.ExecuteNonQuery();
        _logger.Information("Backup database ready");
    }

    public async Task<int> InsertJobAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO BackupJobs
                (DeviceModel, DeviceSerial, Type, SavePath, CreatedAt, SizeBytes, Status, Note)
            VALUES
                (@model, @serial, @type, @path, @date, @size, @status, @note);
            SELECT last_insert_rowid();
            """;

        cmd.Parameters.AddWithValue("@model", job.DeviceModel);
        cmd.Parameters.AddWithValue("@serial", job.DeviceSerial);
        cmd.Parameters.AddWithValue("@type", (int)job.Type);
        cmd.Parameters.AddWithValue("@path", job.SavePath);
        cmd.Parameters.AddWithValue("@date", job.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@size", job.SizeBytes);
        cmd.Parameters.AddWithValue("@status", (int)job.Status);
        cmd.Parameters.AddWithValue("@note", job.Note);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task UpdateJobAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE BackupJobs
            SET SizeBytes = @size, Status = @status, SavePath = @path
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("@size", job.SizeBytes);
        cmd.Parameters.AddWithValue("@status", (int)job.Status);
        cmd.Parameters.AddWithValue("@path", job.SavePath);
        cmd.Parameters.AddWithValue("@id", job.Id);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BackupJob>> GetAllJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, DeviceModel, DeviceSerial, Type, SavePath, CreatedAt, SizeBytes, Status, Note
            FROM BackupJobs
            ORDER BY CreatedAt DESC
            LIMIT 200;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var jobs = new List<BackupJob>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            jobs.Add(MapJob(reader));

        return jobs;
    }

    public async Task DeleteJobAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM BackupJobs WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static BackupJob MapJob(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0),
        DeviceModel = r.GetString(1),
        DeviceSerial = r.GetString(2),
        Type = (BackupType)r.GetInt32(3),
        SavePath = r.GetString(4),
        CreatedAt = DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        SizeBytes = r.GetInt64(6),
        Status = (BackupStatus)r.GetInt32(7),
        Note = r.IsDBNull(8) ? string.Empty : r.GetString(8)
    };

    public void Dispose()
    {
    }
}
