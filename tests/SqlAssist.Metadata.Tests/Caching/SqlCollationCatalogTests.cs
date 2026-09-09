using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Caching;

/// <summary>
/// 定序名單的載入、共用與降級。
/// </summary>
/// <remarks>
/// 名單屬於<b>伺服器</b>而不是資料庫，是這一組與其他層唯一不同的地方：
/// 跟著每一份目錄各存一次的話，使用者每打出一個跨資料庫的限定字就多五千多個
/// 字串，而且對同一台伺服器多送一輪查詢。
/// </remarks>
public sealed class SqlCollationCatalogTests
{
    [Fact]
    public async Task 查得到名單與目前資料庫的定序()
    {
        var collations = await Create(new CollationSource(NewServer()))
            .GetCollationsAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "Chinese_Taiwan_Stroke_CI_AS", "Latin1_General_CI_AS" },
            collations.Names);
        Assert.Equal("Chinese_Taiwan_Stroke_CI_AS", collations.DatabaseCollation);
    }

    /// <summary>同一台伺服器的第二個資料庫不再重問名單。</summary>
    [Fact]
    public async Task 名單在同一台伺服器的目錄之間共用()
    {
        var server = NewServer();
        var first = new CollationSource(server, "Library");
        var second = new CollationSource(server, "LibArchive");

        await Create(first).GetCollationsAsync(CancellationToken.None);
        await Create(second).GetCollationsAsync(CancellationToken.None);

        Assert.Equal(1, first.CollationQueries);
        Assert.Equal(0, second.CollationQueries);
    }

    /// <summary>資料庫的定序不共用：它屬於資料庫，不屬於伺服器。</summary>
    [Fact]
    public async Task 資料庫的定序仍然各問各的()
    {
        var server = NewServer();
        var second = new CollationSource(server, "LibArchive")
        {
            DatabaseCollation = "Latin1_General_CI_AS"
        };

        await Create(new CollationSource(server, "Library"))
            .GetCollationsAsync(CancellationToken.None);
        var collations = await Create(second).GetCollationsAsync(CancellationToken.None);

        Assert.Equal("Latin1_General_CI_AS", collations.DatabaseCollation);
    }

    /// <summary>同一份目錄問第二次不再送查詢；名單不會在一次工作階段中途變動。</summary>
    [Fact]
    public async Task 同一份目錄只問一次()
    {
        var source = new CollationSource(NewServer());
        var catalog = Create(source);

        await catalog.GetCollationsAsync(CancellationToken.None);
        await catalog.GetCollationsAsync(CancellationToken.None);

        Assert.Equal(1, source.Attempts);
    }

    [Fact]
    public async Task 連不上資料庫時回傳空名單而不是擲例外()
    {
        var collations = await Create(new FailingCollationSource())
            .GetCollationsAsync(CancellationToken.None);

        Assert.Empty(collations.Names);
        Assert.Null(collations.DatabaseCollation);
    }

    /// <summary>失敗不進快取，否則連線恢復之後仍然拿到空的。</summary>
    [Fact]
    public async Task 失敗過的名單不會被記住()
    {
        var source = new FailingCollationSource();
        var catalog = Create(source);

        await catalog.GetCollationsAsync(CancellationToken.None);
        await catalog.GetCollationsAsync(CancellationToken.None);

        Assert.Equal(2, source.Attempts);
    }

    /// <summary>
    /// 連結伺服器的目錄一律不問定序。
    /// </summary>
    /// <remarks>
    /// 定序屬於執行個體，而使用者正在編輯的指令碼跑在<b>本機</b>那條連線上。
    /// 列出對面那台的名單，選中的每一個名稱都可能在這裡不存在，而畫面上
    /// 看不出差別——那正是「查不到就退回本機同名的東西」的反面。
    /// </remarks>
    [Fact]
    public async Task 連結伺服器不問定序()
    {
        var source = new CollationSource(NewServer());

        var catalog = new SqlMetadataCatalog(
            source,
            TimeSpan.FromMinutes(5),
            qualifier: SqlCatalogQualifier.ForLinkedServer("LibMirror", "LibArchive"));

        Assert.Empty((await catalog.GetCollationsAsync(CancellationToken.None)).Names);
        Assert.Equal(0, source.Attempts);
    }

    /// <summary>降級不等於一個字都不留；紀錄檔要看得出是哪一條查詢。</summary>
    [Fact]
    public async Task 查詢失敗會把伺服器說的那句話送出去()
    {
        var reported = new List<string>();
        var previous = SqlMetadataFailure.Reporter;
        SqlMetadataFailure.Reporter = (operation, exception) =>
            reported.Add(operation + "｜" + exception.Message);

        try
        {
            await Create(new FailingCollationSource()).GetCollationsAsync(CancellationToken.None);
        }
        finally
        {
            SqlMetadataFailure.Reporter = previous;
        }

        Assert.Contains(reported, line => line.StartsWith("定序名單", StringComparison.Ordinal));
    }

    private static SqlMetadataCatalog Create(ISqlConnectionSource source) =>
        new(source, TimeSpan.FromMinutes(5));

    /// <summary>名單的快取是跨目錄共用的，每一個案例都要自己那一台伺服器。</summary>
    private static string NewServer() => Guid.NewGuid().ToString("N");

    private sealed class FailingCollationSource : ISqlConnectionSource
    {
        public string CacheKey => "unreachable|db1";

        public string ServerCacheKey => "unreachable";

        public string DatabaseName => "db1";

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

    /// <summary>只替代資料庫 I/O；快取層級與降級仍執行產品實作。</summary>
    private sealed class CollationSource : ISqlConnectionSource
    {
        private readonly string _server;
        private readonly Counters _counters = new();

        public CollationSource(string server, string database = "Library")
        {
            _server = server;
            DatabaseName = database;
        }

        public string CacheKey => _server + "|" + DatabaseName;

        public string ServerCacheKey => _server;

        public string DatabaseName { get; }

        public string DatabaseCollation { get; set; } = "Chinese_Taiwan_Stroke_CI_AS";

        public int Attempts => _counters.Connections;

        /// <summary>名單真的被問了幾次；共用生效時第二份目錄是 0。</summary>
        public int CollationQueries => _counters.Collations;

        public IDbConnection OpenConnection()
        {
            _counters.Connections++;
            return new CollationConnection(_counters, DatabaseCollation);
        }
    }

    private sealed class Counters
    {
        public int Connections { get; set; }

        public int Collations { get; set; }
    }

    private sealed class CollationConnection : IDbConnection
    {
        private readonly Counters _counters;
        private readonly string _databaseCollation;

        public CollationConnection(Counters counters, string databaseCollation)
        {
            _counters = counters;
            _databaseCollation = databaseCollation;
        }

        [AllowNull]
        public string ConnectionString { get; set; } = string.Empty;

        public int ConnectionTimeout => 0;

        public string Database => "Library";

        public ConnectionState State => ConnectionState.Open;

        public IDbCommand CreateCommand() => new CollationCommand(_counters, _databaseCollation);

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

    private sealed class CollationCommand : IDbCommand
    {
        private readonly Counters _counters;
        private readonly string _databaseCollation;

        public CollationCommand(Counters counters, string databaseCollation)
        {
            _counters = counters;
            _databaseCollation = databaseCollation;
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

        public IDataReader ExecuteReader()
        {
            using var table = new DataTable();
            table.Columns.Add("name", typeof(string));

            if (CommandText.Contains("fn_helpcollations"))
            {
                _counters.Collations++;
                table.Rows.Add("Chinese_Taiwan_Stroke_CI_AS");
                table.Rows.Add("Latin1_General_CI_AS");
            }
            else
            {
                table.Rows.Add(_databaseCollation);
            }

            return table.CreateDataReader();
        }
    }
}
