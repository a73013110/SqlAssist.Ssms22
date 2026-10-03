using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Completion;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Caching;

/// <summary>
/// 目錄物件（登入、使用者、憑證…）的名冊：每一種都有一條查詢、只在被問到時查、有效期內只問一次、查不到時降級。
/// </summary>
[Collection(MetadataFailureCollection.Name)]
public sealed class SqlCatalogEntityCatalogTests
{
    /// <summary>Core 的名冊每一種都要有一條查詢或第一層快照的那一份；少了的那一種在執行期擲例外。</summary>
    [Fact]
    public void 名冊的每一種都有查詢()
    {
        Assert.All(SqlCatalogEntity.All, entity =>
        {
            var query = SqlCatalogEntityQuery.For(entity);
            Assert.True(query.Text is not null ^ query.FromSnapshot is not null, entity.Kind);
        });
    }

    /// <summary>DROP SCHEMA 要的多半是空的那一個：取完整名單，不是建議清單用的那一份。</summary>
    [Fact]
    public void 結構描述取完整名單_資料庫取快照()
    {
        var snapshot = new SqlDatabaseSnapshot(
            "Library",
            new[] { new SqlObjectInfo(1, "dbo", "Loan", SqlObjectKind.Table) },
            new[] { "dbo", "Lending" },
            new[] { "Library", "LibArchive" },
            DateTimeOffset.UtcNow);

        Assert.Equal(new[] { "dbo", "Lending" }, SqlCatalogEntityQuery.For(SqlCatalogEntity.Schema).FromSnapshot!(snapshot));
        Assert.Equal(new[] { "Library", "LibArchive" }, SqlCatalogEntityQuery.For(SqlCatalogEntity.Database).FromSnapshot!(snapshot));
    }

    /// <summary>2016 才有的檢視先問 sys.all_views：舊版是一份成功的空名單，不是每一次都失敗的降級。</summary>
    [Theory]
    [InlineData("database_scoped_credentials")]
    [InlineData("external_data_sources")]
    [InlineData("external_file_formats")]
    public void 新版才有的檢視先問有沒有(string view)
    {
        var entity = view switch
        {
            "database_scoped_credentials" => SqlCatalogEntity.DatabaseScopedCredential,
            "external_data_sources" => SqlCatalogEntity.ExternalDataSource,
            _ => SqlCatalogEntity.ExternalFileFormat
        };
        var text = SqlCatalogEntityQuery.For(entity).Text!;

        Assert.Contains("sys.all_views", text);
        Assert.Contains($"EXEC (N'SELECT", text);
        Assert.Contains("sys." + view, text);
    }

    [Fact]
    public async Task 查得到名稱_有效期內只問一次()
    {
        var source = new NameSource();
        var catalog = Create(source);

        var first = await catalog.GetCatalogEntityNamesAsync(SqlCatalogEntity.Login, CancellationToken.None);
        var second = await catalog.GetCatalogEntityNamesAsync(SqlCatalogEntity.Login, CancellationToken.None);

        Assert.Equal(new[] { "LibAdmin", @"LIBRARY\Reader" }, first);
        Assert.Same(first, second);
        Assert.Equal(1, source.Queries);
    }

    [Fact]
    public async Task 不同種類各問各的_重新整理之後重問()
    {
        var source = new NameSource();
        var catalog = Create(source);

        await catalog.GetCatalogEntityNamesAsync(SqlCatalogEntity.Login, CancellationToken.None);
        var users = await catalog.GetCatalogEntityNamesAsync(SqlCatalogEntity.User, CancellationToken.None);
        catalog.Invalidate();
        await catalog.GetCatalogEntityNamesAsync(SqlCatalogEntity.Login, CancellationToken.None);

        Assert.Equal(new[] { "LibClerk" }, users);
        Assert.Equal(3, source.Queries);
    }

    /// <summary>查不到回空的；剛失敗過的那一種在退避期間不再撞同一堵牆，過了才重試，失敗也不進快取。</summary>
    [Fact]
    public async Task 連不上時回空名單_退避期間不重試()
    {
        var source = new FailingSource();
        var backingOff = new SqlMetadataCatalog(source, TimeSpan.FromMinutes(5), failureBackoff: TimeSpan.FromMinutes(5));

        Assert.Empty(await backingOff.GetCatalogEntityNamesAsync(SqlCatalogEntity.Login, CancellationToken.None));
        Assert.Empty(await backingOff.GetCatalogEntityNamesAsync(SqlCatalogEntity.Login, CancellationToken.None));
        Assert.Equal(1, source.Attempts);

        var retrying = new SqlMetadataCatalog(source, TimeSpan.FromMinutes(5), failureBackoff: TimeSpan.Zero);
        await retrying.GetCatalogEntityNamesAsync(SqlCatalogEntity.Login, CancellationToken.None);
        await retrying.GetCatalogEntityNamesAsync(SqlCatalogEntity.Login, CancellationToken.None);
        Assert.Equal(3, source.Attempts);
    }

    /// <summary>紀錄檔要看得出是哪一種：標題相同，主體是種類名稱。</summary>
    [Fact]
    public async Task 查詢失敗會帶著種類送出去()
    {
        var reported = new List<string>();
        var previous = SqlMetadataFailure.Reporter;
        SqlMetadataFailure.Reporter = (title, _) => reported.Add(title);

        try
        {
            await Create(new FailingSource()).GetCatalogEntityNamesAsync(SqlCatalogEntity.Certificate, CancellationToken.None);
        }
        finally
        {
            SqlMetadataFailure.Reporter = previous;
        }

        Assert.Contains(reported, line => line == "載入目錄物件名單：" + SqlCatalogEntity.Certificate.KindText);
    }

    /// <summary>名稱寫進的是本機這一句：連結伺服器那一台的登入在這裡不存在。</summary>
    [Fact]
    public async Task 連結伺服器不問名單()
    {
        var source = new NameSource();
        var catalog = new SqlMetadataCatalog(
            source,
            TimeSpan.FromMinutes(5),
            qualifier: SqlCatalogQualifier.ForLinkedServer("LibMirror", "LibArchive"));

        Assert.Empty(await catalog.GetCatalogEntityNamesAsync(SqlCatalogEntity.Login, CancellationToken.None));
        Assert.Equal(0, source.Queries);
    }

    private static SqlMetadataCatalog Create(ISqlConnectionSource source) =>
        new(source, TimeSpan.FromMinutes(5));

    private sealed class FailingSource : ISqlConnectionSource
    {
        public string CacheKey => "unreachable|Library";

        public string ServerCacheKey => "unreachable";

        public string DatabaseName => "Library";

        public int Attempts { get; private set; }

        public IDbConnection OpenConnection()
        {
            Attempts++;
            throw new UnreachableServerException();
        }
    }

    private sealed class UnreachableServerException : DbException
    {
        public UnreachableServerException()
            : base("連不上伺服器。")
        {
        }
    }

    /// <summary>只替代資料庫 I/O：依查詢文字回一欄名稱。</summary>
    private sealed class NameSource : ISqlConnectionSource
    {
        private readonly Dictionary<string, string[]> _rows = new()
        {
            [SqlCatalogEntityQuery.For(SqlCatalogEntity.Login).Text!] = new[] { "LibAdmin", @"LIBRARY\Reader" },
            [SqlCatalogEntityQuery.For(SqlCatalogEntity.User).Text!] = new[] { "LibClerk" },
        };

        public string CacheKey => "library-server|Library";

        public string ServerCacheKey => "library-server";

        public string DatabaseName => "Library";

        public int Queries { get; set; }

        public IDbConnection OpenConnection() => new NameConnection(this);

        public DataTable Read(string text)
        {
            Queries++;
            var table = new DataTable();
            table.Columns.Add("name", typeof(string));

            foreach (var name in _rows.TryGetValue(text, out var rows) ? rows : Array.Empty<string>())
            {
                table.Rows.Add(name);
            }

            return table;
        }
    }

    private sealed class NameConnection : IDbConnection
    {
        private readonly NameSource _source;

        public NameConnection(NameSource source)
        {
            _source = source;
        }

        [AllowNull]
        public string ConnectionString { get; set; } = string.Empty;

        public int ConnectionTimeout => 0;

        public string Database => "Library";

        public ConnectionState State => ConnectionState.Open;

        public IDbCommand CreateCommand() => new NameCommand(_source);

        public void Dispose()
        {
        }

        public void Open()
        {
        }

        public void Close()
        {
        }

        public void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public IDbTransaction BeginTransaction() => throw new NotSupportedException();

        public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotSupportedException();
    }

    private sealed class NameCommand : IDbCommand
    {
        private readonly NameSource _source;

        public NameCommand(NameSource source)
        {
            _source = source;
        }

        [AllowNull]
        public string CommandText { get; set; } = string.Empty;

        public int CommandTimeout { get; set; }

        public CommandType CommandType { get; set; }

        public IDbConnection? Connection { get; set; }

        public IDbTransaction? Transaction { get; set; }

        public UpdateRowSource UpdatedRowSource { get; set; }

        public IDataParameterCollection Parameters => throw new NotSupportedException();

        public IDbDataParameter CreateParameter() => throw new NotSupportedException();

        public void Dispose()
        {
        }

        public void Cancel()
        {
        }

        public void Prepare() => throw new NotSupportedException();

        public int ExecuteNonQuery() => throw new NotSupportedException();

        public object ExecuteScalar() => throw new NotSupportedException();

        public IDataReader ExecuteReader(CommandBehavior behavior) => ExecuteReader();

        public IDataReader ExecuteReader() => _source.Read(CommandText).CreateDataReader();
    }
}
