using System;
using System.Collections.Generic;
using System.Data;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Querying;

public sealed class SqlDatabaseScopedConnectionSourceTests
{
    [Fact]
    public void 開出來的連線指向目標資料庫()
    {
        var inner = new FakeConnectionSource("db1");
        var source = new SqlDatabaseScopedConnectionSource(inner, "LibArchive");

        var connection = (FakeConnection)source.OpenConnection();

        Assert.Equal("LibArchive", connection.Database);
        Assert.Equal(new[] { "LibArchive" }, connection.ChangedTo);
    }

    /// <remarks>
    /// 查詢視窗每換一次資料庫就會從當時那份目錄再包一層，疊起來的每一層都會在開連線時
    /// 多發一次 <c>ChangeDatabase</c>——那是一趟往返，症狀是跨資料庫查詢愈用愈慢。
    /// </remarks>
    [Fact]
    public void 疊在跨資料庫來源上時只換一次資料庫()
    {
        var inner = new FakeConnectionSource("db1");
        var once = new SqlDatabaseScopedConnectionSource(inner, "LibArchive");
        var twice = new SqlDatabaseScopedConnectionSource(once, "LibMirror");

        var connection = (FakeConnection)twice.OpenConnection();

        Assert.Equal(new[] { "LibMirror" }, connection.ChangedTo);
    }

    private sealed class FakeConnectionSource : ISqlConnectionSource
    {
        public FakeConnectionSource(string databaseName)
        {
            DatabaseName = databaseName;
            CacheKey = SqlConnectionCacheKey.Compose("server-a", databaseName);
        }

        public string CacheKey { get; }

        public string ServerCacheKey => "server-a";

        public string DatabaseName { get; }

        public IDbConnection OpenConnection() => new FakeConnection(DatabaseName);
    }

    private sealed class FakeConnection : IDbConnection
    {
        private readonly List<string> _changedTo = new();

        public FakeConnection(string databaseName)
        {
            Database = databaseName;
        }

        public IReadOnlyList<string> ChangedTo => _changedTo;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public string ConnectionString { get; set; } = string.Empty;

        public int ConnectionTimeout => 0;

        public string Database { get; private set; }

        public ConnectionState State => ConnectionState.Open;

        public IDbTransaction BeginTransaction() => throw new NotSupportedException();

        public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotSupportedException();

        public void ChangeDatabase(string databaseName)
        {
            _changedTo.Add(databaseName);
            Database = databaseName;
        }

        public void Close()
        {
        }

        public IDbCommand CreateCommand() => throw new NotSupportedException();

        public void Open()
        {
        }

        public void Dispose()
        {
        }
    }
}
