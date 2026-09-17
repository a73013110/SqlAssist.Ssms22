using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.SqlMemory.Sqlite.Tests;

internal sealed class SqliteTestStore : IDisposable
{
    public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SqlAssist.SqlMemory.Tests", Guid.NewGuid().ToString("N"));
    public string Path => System.IO.Path.Combine(DirectoryPath, "memory.db");
    public static readonly DateTimeOffset Start = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
    public static readonly SqlCapturePolicy Policy = new(true, true, TimeSpan.FromMinutes(10), true, true);
    public SqlDocument Document { get; } = new(Guid.NewGuid(), "Library.sql", null);
    public SqlSession Session { get; }

    public SqliteTestStore() => Session = new SqlSession(Guid.NewGuid(), Document.DocumentId);

    public Task<SqliteTestRepository> Open(CancellationToken cancellationToken) =>
        SqliteTestRepository.OpenAsync(Path, cancellationToken);

    public SqlCapture Capture(long sequence = 1, string sql = "SELECT * FROM Lib_Reader;",
        SqlCaptureKind kind = SqlCaptureKind.BeforeExecute, string? selected = null, int seconds = 0,
        SqlConnectionLabel? context = null, SqlSession? session = null) =>
        new(Guid.NewGuid(), Document, session ?? Session, sequence, Start.AddSeconds(seconds), kind,
            new SqlTextSnapshot(sql), context, selected == null ? null : new SqlTextSnapshot(selected));

    public Task Process(SqliteTestRepository repository, SqlCapture capture, CancellationToken cancellationToken,
        SqlCapturePolicy? policy = null, string? leaseId = null) =>
        new SqlCaptureCommitter(repository, new SqlCapturePlanner(), leaseId: () => leaseId)
            .ProcessAsync(capture, policy ?? Policy, cancellationToken);

    /// <summary>所有期限都已過的政策；只留保護根，讓測試分辨「不該刪」與「刪不到」。</summary>
    public static SqlRetentionPolicy Expired(long? capacity = null) =>
        new(Start.AddDays(1), Start.AddDays(1), capacity);

    /// <summary>
    /// 逐批巡到一輪結束；順便確認每批都守著工作量上限，以及每候選最多刪本體與投影兩列、
    /// 每列再帶出一個內容與一個連線。
    /// </summary>
    public static async Task<SqlMemoryMaintenanceResult> Drain(ISqlMemoryMaintenanceStore repository,
        SqlRetentionPolicy policy, int budget = 2, string? cursor = null,
        SqlMemoryMaintenanceScan scan = SqlMemoryMaintenanceScan.Indexed)
    {
        for (var batch = 0; batch < 200; batch++)
        {
            var result = await repository.MaintainAsync(new SqlMemoryMaintenanceRequest(policy, budget, cursor, scan),
                TestContext.Current.CancellationToken);
            Assert.InRange(result.ExaminedCandidates, 0, budget);
            Assert.InRange(result.DeletedRows, 0, 6 * result.ExaminedCandidates);
            if (result.Cursor == null && !result.RequiresAnotherPass) return result;
            cursor = result.Cursor;
        }
        throw new InvalidOperationException("維護未在測試上限內收斂。");
    }

    public object? Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public IEnumerable<string> Query(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    public void Dispose()
    {
        // 只刪除本次建立的唯一測試目錄，不遍歷使用者的資料庫目錄。
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }
}
