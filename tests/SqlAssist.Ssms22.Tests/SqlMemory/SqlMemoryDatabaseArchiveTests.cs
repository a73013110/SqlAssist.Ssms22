using System;
using System.IO;
using SqlAssist.Ssms22.SqlMemory;
using Xunit;

namespace SqlAssist.Ssms22.Tests.SqlMemory;

public sealed class SqlMemoryDatabaseArchiveTests : IDisposable
{
    private static readonly DateTimeOffset Stamp = new(2026, 9, 16, 21, 30, 45, TimeSpan.Zero);
    private const string StampText = "20260916_213045";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "SqlAssistArchiveTests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "SQLMemory.db");

    private string BackupPath(string suffix = "") => Path.Combine(_directory, $"SQLMemory.bak.{StampText}.db") + suffix;

    private string NumberedBackupPath(int counter, string suffix = "") =>
        Path.Combine(_directory, $"SQLMemory.bak.{StampText}_{counter}.db") + suffix;

    public SqlMemoryDatabaseArchiveTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Write(string suffix, string content) => File.WriteAllText(DatabasePath + suffix, content);

    [Fact]
    public void ArchiveMovesDatabaseWalAndShmTogether()
    {
        Write("", "db"); Write("-wal", "wal"); Write("-shm", "shm");

        var backup = SqlMemoryDatabaseArchive.Archive(DatabasePath, Stamp);

        Assert.Equal(BackupPath(), backup);
        Assert.False(File.Exists(DatabasePath));
        Assert.False(File.Exists(DatabasePath + "-wal"));
        Assert.False(File.Exists(DatabasePath + "-shm"));
        Assert.Equal("db", File.ReadAllText(BackupPath()));
        Assert.Equal("wal", File.ReadAllText(BackupPath("-wal")));
        Assert.Equal("shm", File.ReadAllText(BackupPath("-shm")));
    }

    [Fact]
    public void ArchiveNumbersFurtherBackupsTakenWithinTheSameSecond()
    {
        Write("", "first");
        Assert.Equal(BackupPath(), SqlMemoryDatabaseArchive.Archive(DatabasePath, Stamp));

        Write("", "second");
        Assert.Equal(NumberedBackupPath(1), SqlMemoryDatabaseArchive.Archive(DatabasePath, Stamp));

        // 第三次只剩孤兒 WAL：流水號仍要跳過已被 -wal 佔用的名稱，不覆蓋上一份備份。
        Write("-wal", "third");
        Assert.Equal(NumberedBackupPath(2), SqlMemoryDatabaseArchive.Archive(DatabasePath, Stamp));

        Assert.Equal("first", File.ReadAllText(BackupPath()));
        Assert.Equal("second", File.ReadAllText(NumberedBackupPath(1)));
        Assert.Equal("third", File.ReadAllText(NumberedBackupPath(2, "-wal")));
    }

    [Fact]
    public void ArchiveMovesOrphanWalAndShmWhenDatabaseIsGone()
    {
        Write("-wal", "wal"); Write("-shm", "shm");

        var backup = SqlMemoryDatabaseArchive.Archive(DatabasePath, Stamp);

        Assert.Equal(BackupPath(), backup);
        Assert.False(File.Exists(BackupPath()));
        Assert.Equal("wal", File.ReadAllText(BackupPath("-wal")));
        Assert.Equal("shm", File.ReadAllText(BackupPath("-shm")));
        Assert.False(File.Exists(DatabasePath + "-wal"));
        Assert.False(File.Exists(DatabasePath + "-shm"));
    }

    [Fact]
    public void ArchiveReportsNothingToDoWhenNoFileExists()
    {
        Assert.False(SqlMemoryDatabaseArchive.Exists(DatabasePath));
        Assert.Equal(string.Empty, SqlMemoryDatabaseArchive.Archive(DatabasePath, Stamp));
    }

    [Fact]
    public void ExistsSeesAnOrphanWal()
    {
        Write("-wal", "wal");
        Assert.True(SqlMemoryDatabaseArchive.Exists(DatabasePath));
    }

    [Fact]
    public void ArchivePutsMovedFilesBackWhenOneIsLocked()
    {
        Write("", "db"); Write("-wal", "wal"); Write("-shm", "shm");

        using (File.Open(DatabasePath + "-shm", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = Assert.Throws<InvalidOperationException>(
                () => SqlMemoryDatabaseArchive.Archive(DatabasePath, Stamp));

            Assert.Contains("鎖定", failure.Message);
            Assert.DoesNotContain("無法搬回", failure.Message);
            Assert.IsAssignableFrom<IOException>(failure.InnerException);
        }

        Assert.Equal("db", File.ReadAllText(DatabasePath));
        Assert.Equal("wal", File.ReadAllText(DatabasePath + "-wal"));
        Assert.False(File.Exists(BackupPath()));
        Assert.False(File.Exists(BackupPath("-wal")));
    }

    [Fact]
    public void ArchiveRejectsAPathWithoutADirectory()
    {
        Assert.Throws<ArgumentException>(() => SqlMemoryDatabaseArchive.Archive("   ", Stamp));
        Assert.Throws<ArgumentException>(() => SqlMemoryDatabaseArchive.Archive("SQLMemory.db", Stamp));
    }
}
