using System;
using System.Data;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Ssms22.Tests.Search;

/// <summary>不開連線的目錄；只要快取鍵分得開伺服器與資料庫。</summary>
internal static class SqlSearchTestCatalogs
{
    internal static SqlMetadataCatalog Create(string database, string server = "LIBSQL01") =>
        new(new StubSource(server, database), TimeSpan.FromMinutes(5));

    private sealed class StubSource : ISqlConnectionSource
    {
        private readonly string _server;

        internal StubSource(string server, string database)
        {
            _server = server;
            DatabaseName = database;
        }

        public string CacheKey => _server + "/" + DatabaseName;

        public string ServerCacheKey => _server;

        public string DatabaseName { get; }

        public IDbConnection OpenConnection() =>
            throw new InvalidOperationException("這一份測試不開連線。");
    }
}
