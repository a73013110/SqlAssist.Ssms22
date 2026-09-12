using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlAssist.Core.QueryMemory;

namespace SqlAssist.QueryMemory.Sqlite.Tests;

internal sealed class SqliteTestStore : IDisposable
{
    public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SqlAssist.QueryMemory.Tests", Guid.NewGuid().ToString("N"));
    public string Path => System.IO.Path.Combine(DirectoryPath, "memory.db");
    public static readonly DateTimeOffset Start = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
    public static readonly QueryMemoryPolicy Policy = new(true, true, TimeSpan.FromMinutes(10), true, true);
    public QueryDocument Document { get; } = new(Guid.NewGuid(), "Library.sql", null);
    public QuerySession Session { get; }

    public SqliteTestStore() => Session = new QuerySession(Guid.NewGuid(), Document.DocumentId, Start);

    public Task<SqliteQueryMemoryRepository> Open(CancellationToken cancellationToken) =>
        SqliteQueryMemoryRepository.OpenAsync(Path, cancellationToken);

    public QueryMemoryCapture Capture(long sequence = 1, string sql = "SELECT * FROM Lib_Reader;",
        QueryCaptureKind kind = QueryCaptureKind.BeforeExecute, string? selected = null, int seconds = 0,
        QueryConnectionContext? context = null, QuerySession? session = null) =>
        new(Guid.NewGuid(), Document, session ?? Session, sequence, Start.AddSeconds(seconds), kind,
            new QueryTextSnapshot(sql), context, selected == null ? null : new QueryTextSnapshot(selected));

    public Task Process(SqliteQueryMemoryRepository repository, QueryMemoryCapture capture, CancellationToken cancellationToken,
        QueryMemoryPolicy? policy = null) =>
        new QueryMemoryProcessor(repository, new QueryRevisionEngine()).ProcessAsync(capture, policy ?? Policy, cancellationToken);

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
