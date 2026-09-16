using System.IO;
using AndroidManager.Backup.Data;
using AndroidManager.Backup.Parsing;
using AndroidManager.Backup.ViewModels;
using AndroidManager.Core.Models;

namespace AndroidManager.Backup.Tests;

public sealed class BackupOutputParsersTests
{
    [Fact]
    public void ParseSmsOutput_ReadsRows()
    {
        const string raw = """
            Row: 0 _id=1, address=05551234567, body=Merhaba, date=1704067200000, type=1
            Row: 1 _id=2, address=05557654321, body=Nasılsın?, date=1704067100000, type=2
            """;

        var messages = BackupOutputParsers.ParseSmsOutput(raw);

        Assert.Equal(2, messages.Count);
        Assert.Equal("05551234567", messages[0].Address);
        Assert.Equal("Merhaba", messages[0].Body);
        Assert.Equal(1, messages[0].Type);
        Assert.Equal("Gelen", messages[0].TypeLabel);
    }

    [Fact]
    public void ParseContacts_OrdersByDisplayName()
    {
        const string raw = """
            Row: 0 _id=1, display_name=Ali Veli, number=05551234567
            Row: 1 _id=2, display_name=Ayşe Kaya, number=05557654321
            """;

        var contacts = BackupOutputParsers.ParseContacts(raw);

        Assert.Equal(2, contacts.Count);
        Assert.Equal("Ali Veli", contacts[0].DisplayName);
        Assert.Equal("Ayşe Kaya", contacts[1].DisplayName);
    }
}

public sealed class BackupRepositoryTests : IDisposable
{
    private readonly string _tempDb = Path.Combine(Path.GetTempPath(), $"am-backup-{Guid.NewGuid():N}.db");
    private readonly BackupRepository _repo;

    public BackupRepositoryTests() => _repo = new BackupRepository(_tempDb);

    [Fact]
    public async Task InsertAndGet_PersistsJob()
    {
        var job = new BackupJob
        {
            DeviceModel = "TestPhone",
            DeviceSerial = "TEST001",
            Type = BackupType.Sms | BackupType.Contacts,
            SavePath = "/tmp/backup.zip",
            CreatedAt = DateTime.UtcNow,
            Status = BackupStatus.Completed,
            Note = "Test backup"
        };

        var id = await _repo.InsertJobAsync(job);
        var jobs = await _repo.GetAllJobsAsync();

        Assert.True(id > 0);
        Assert.Single(jobs);
        Assert.Equal("TestPhone", jobs[0].DeviceModel);
        Assert.Equal(BackupStatus.Completed, jobs[0].Status);
    }

    [Fact]
    public async Task Delete_RemovesJob()
    {
        var id = await _repo.InsertJobAsync(new BackupJob
        {
            DeviceModel = "Phone",
            DeviceSerial = "X",
            Type = BackupType.Sms,
            SavePath = "/tmp/x.zip",
            CreatedAt = DateTime.UtcNow,
            Status = BackupStatus.Completed
        });

        await _repo.DeleteJobAsync(id);
        Assert.Empty(await _repo.GetAllJobsAsync());
    }

    [Fact]
    public async Task Update_ChangesStatusAndSize()
    {
        var job = new BackupJob
        {
            DeviceModel = "Phone",
            DeviceSerial = "X",
            Type = BackupType.Photos,
            SavePath = "/tmp/x.zip",
            CreatedAt = DateTime.UtcNow,
            Status = BackupStatus.Running
        };

        job.Id = await _repo.InsertJobAsync(job);
        job.Status = BackupStatus.Completed;
        job.SizeBytes = 1024;
        await _repo.UpdateJobAsync(job);

        var jobs = await _repo.GetAllJobsAsync();
        Assert.Equal(BackupStatus.Completed, jobs[0].Status);
        Assert.Equal(1024L, jobs[0].SizeBytes);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try
        {
            if (File.Exists(_tempDb))
                File.Delete(_tempDb);
        }
        catch
        {
            // ignore locked temp db on Windows
        }
    }
}

public sealed class BackupJobViewModelTests
{
    [Theory]
    [InlineData(BackupType.Sms, "SMS")]
    [InlineData(BackupType.Sms | BackupType.Contacts, "SMS + Rehber")]
    [InlineData(BackupType.All, "SMS + Rehber + Foto + Video + APK + Veri")]
    [InlineData(BackupType.Apps | BackupType.AppData, "APK + Veri")]
    public void TypeLabel_MatchesFlags(BackupType type, string expected)
    {
        var vm = new BackupJobViewModel(new BackupJob { Type = type });
        Assert.Equal(expected, vm.TypeLabel);
    }
}
