using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Completion;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Caching;

/// <summary>
/// 執行個體名單（定序、語言、時區）的載入、共用與降級。
/// </summary>
/// <remarks>
/// 名單屬於<b>伺服器</b>而不是資料庫，是這一組與其他層唯一不同的地方：
/// 跟著每一份目錄各存一次的話，使用者每打出一個跨資料庫的限定字就多五千多個
/// 定序字串，而且對同一台伺服器多送一輪查詢。三份名單走同一條路，案例盡量三份一起測。
/// </remarks>
[Collection(MetadataFailureCollection.Name)]
public sealed class SqlInstanceListCatalogTests
{
    public static TheoryData<string> Lists() => new() { "Collation", "Language", "TimeZone" };

    [Fact]
    public async Task 查得到名單與在用的值()
    {
        var catalog = Create(new ListSource(NewServer()));

        var collations = await catalog.GetInstanceListAsync(SqlInstanceList.Collation, CancellationToken.None);
        var languages = await catalog.GetInstanceListAsync(SqlInstanceList.Language, CancellationToken.None);
        var timeZones = await catalog.GetInstanceListAsync(SqlInstanceList.TimeZone, CancellationToken.None);

        Assert.Equal(new[] { "Chinese_Taiwan_Stroke_CI_AS", "Latin1_General_CI_AS" }, Names(collations));
        Assert.Equal("Chinese_Taiwan_Stroke_CI_AS", collations.InUse);
        Assert.Equal(new[] { "Deutsch", "us_english" }, Names(languages));
        Assert.Equal(new[] { "German", "English" }, languages.Entries.Select(entry => entry.Detail));
        Assert.Equal("us_english", languages.InUse);
        Assert.Equal(new[] { "Taipei Standard Time", "UTC" }, Names(timeZones));
        Assert.Equal("+08:00", timeZones.Entries[0].Detail);
        Assert.Equal("Taipei Standard Time", timeZones.InUse);
    }

    /// <summary>同一台伺服器的第二個資料庫不再重問名單；不同名單各自一份。</summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task 名單在同一台伺服器的目錄之間共用(string name)
    {
        var list = ListOf(name);
        var server = NewServer();
        var first = new ListSource(server, "Library");
        var second = new ListSource(server, "LibArchive");

        await Create(first).GetInstanceListAsync(list, CancellationToken.None);
        await Create(second).GetInstanceListAsync(list, CancellationToken.None);

        Assert.Equal(1, first.EntryQueries);
        Assert.Equal(0, second.EntryQueries);
    }

    [Fact]
    public async Task 不同名單不共用快取()
    {
        var source = new ListSource(NewServer());
        var catalog = Create(source);

        await catalog.GetInstanceListAsync(SqlInstanceList.Collation, CancellationToken.None);
        var languages = await catalog.GetInstanceListAsync(SqlInstanceList.Language, CancellationToken.None);

        Assert.Equal(2, source.EntryQueries);
        Assert.Contains("Deutsch", Names(languages));
    }

    /// <summary>在用的值不共用：資料庫的定序屬於資料庫，不屬於伺服器。</summary>
    [Fact]
    public async Task 在用的值仍然各問各的()
    {
        var server = NewServer();
        var second = new ListSource(server, "LibArchive")
        {
            DatabaseCollation = "Latin1_General_CI_AS"
        };

        await Create(new ListSource(server, "Library"))
            .GetInstanceListAsync(SqlInstanceList.Collation, CancellationToken.None);
        var collations = await Create(second).GetInstanceListAsync(SqlInstanceList.Collation, CancellationToken.None);

        Assert.Equal("Latin1_General_CI_AS", collations.InUse);
    }

    /// <summary>同一份目錄問第二次不再送查詢；名單不會在一次工作階段中途變動。</summary>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task 同一份目錄只問一次(string name)
    {
        var source = new ListSource(NewServer());
        var catalog = Create(source);

        await catalog.GetInstanceListAsync(ListOf(name), CancellationToken.None);
        await catalog.GetInstanceListAsync(ListOf(name), CancellationToken.None);

        Assert.Equal(1, source.Attempts);
    }

    /// <summary>
    /// 舊版問不到在用的時區（<c>CURRENT_TIMEZONE_ID()</c> 要 2022）：查詢自己接住，
    /// 回一列 NULL；名單照樣有，而且這一次不是失敗。
    /// </summary>
    [Fact]
    public async Task 問不到在用的值時名單照樣有()
    {
        var reported = new List<string>();
        var previous = SqlMetadataFailure.Reporter;
        SqlMetadataFailure.Reporter = (operation, _) => reported.Add(operation);
        SqlInstanceListData timeZones;

        try
        {
            timeZones = await Create(new ListSource(NewServer()) { ServerTimeZone = null })
                .GetInstanceListAsync(SqlInstanceList.TimeZone, CancellationToken.None);
        }
        finally
        {
            SqlMetadataFailure.Reporter = previous;
        }

        Assert.Equal(new[] { "Taipei Standard Time", "UTC" }, Names(timeZones));
        Assert.Null(timeZones.InUse);
        Assert.Empty(reported);
    }

    /// <summary>
    /// 舊版的查詢要自己分得出「這一版沒有」：直接 SELECT 不存在的目錄檢視或函式，
    /// 會變成每一次都失敗、每一次都重試的降級，而不是一份成功的空名單。
    /// </summary>
    [Fact]
    public void 時區的查詢在舊版不會失敗()
    {
        Assert.Contains("sys.all_views", SqlMetadataQueries.TimeZones);
        Assert.Contains("EXEC (N'SELECT z.name", SqlMetadataQueries.TimeZones);
        Assert.Contains("BEGIN CATCH", SqlMetadataQueries.ServerTimeZone);
        Assert.Contains("EXEC (N'SELECT CONVERT(nvarchar(128), CURRENT_TIMEZONE_ID())", SqlMetadataQueries.ServerTimeZone);
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task 連不上資料庫時回傳空名單而不是擲例外(string name)
    {
        var data = await Create(new FailingSource()).GetInstanceListAsync(ListOf(name), CancellationToken.None);

        Assert.Empty(data.Entries);
        Assert.Null(data.InUse);
    }

    /// <summary>失敗不進快取，否則連線恢復之後仍然拿到空的。</summary>
    [Fact]
    public async Task 失敗過的名單不會被記住()
    {
        var source = new FailingSource();
        var catalog = Create(source);

        await catalog.GetInstanceListAsync(SqlInstanceList.Language, CancellationToken.None);
        await catalog.GetInstanceListAsync(SqlInstanceList.Language, CancellationToken.None);

        Assert.Equal(2, source.Attempts);
    }

    /// <summary>
    /// 連結伺服器的目錄一律不問名單。
    /// </summary>
    /// <remarks>
    /// 名單屬於執行個體，而使用者正在編輯的指令碼跑在<b>本機</b>那條連線上。
    /// 列出對面那台的名單，選中的每一個名稱都可能在這裡不存在，而畫面上
    /// 看不出差別——那正是「查不到就退回本機同名的東西」的反面。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Lists))]
    public async Task 連結伺服器不問名單(string name)
    {
        var source = new ListSource(NewServer());

        var catalog = new SqlMetadataCatalog(
            source,
            TimeSpan.FromMinutes(5),
            qualifier: SqlCatalogQualifier.ForLinkedServer("LibMirror", "LibArchive"));

        Assert.Empty((await catalog.GetInstanceListAsync(ListOf(name), CancellationToken.None)).Entries);
        Assert.Equal(0, source.Attempts);
    }

    /// <summary>降級不等於一個字都不留；紀錄檔要看得出是哪一條查詢。</summary>
    [Theory]
    [InlineData("Collation", "載入定序名單")]
    [InlineData("Language", "載入語言名單")]
    [InlineData("TimeZone", "載入時區名單")]
    public async Task 查詢失敗會把伺服器說的那句話送出去(string name, string operation)
    {
        var reported = new List<string>();
        var previous = SqlMetadataFailure.Reporter;
        SqlMetadataFailure.Reporter = (title, exception) =>
            reported.Add(title + "｜" + exception.Message);

        try
        {
            await Create(new FailingSource()).GetInstanceListAsync(ListOf(name), CancellationToken.None);
        }
        finally
        {
            SqlMetadataFailure.Reporter = previous;
        }

        Assert.Contains(reported, line => line.StartsWith(operation, StringComparison.Ordinal));
    }

    private static SqlInstanceList ListOf(string name) =>
        SqlInstanceList.All.Single(list => list.Target.ToString() == name);

    private static string[] Names(SqlInstanceListData data) =>
        data.Entries.Select(entry => entry.Name).ToArray();

    private static SqlMetadataCatalog Create(ISqlConnectionSource source) =>
        new(source, TimeSpan.FromMinutes(5));

    /// <summary>名單的快取是跨目錄共用的，每一個案例都要自己那一台伺服器。</summary>
    private static string NewServer() => Guid.NewGuid().ToString("N");

    private sealed class FailingSource : ISqlConnectionSource
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
    private sealed class ListSource : ISqlConnectionSource
    {
        private readonly string _server;
        private readonly Counters _counters = new();

        public ListSource(string server, string database = "Library")
        {
            _server = server;
            DatabaseName = database;
        }

        public string CacheKey => _server + "|" + DatabaseName;

        public string ServerCacheKey => _server;

        public string DatabaseName { get; }

        public string DatabaseCollation { get; set; } = "Chinese_Taiwan_Stroke_CI_AS";

        /// <summary>null 代表這一版沒有 <c>CURRENT_TIMEZONE_ID()</c>，查詢自己回 NULL。</summary>
        public string? ServerTimeZone { get; set; } = "Taipei Standard Time";

        public int Attempts => _counters.Connections;

        /// <summary>名單真的被問了幾次；共用生效時第二份目錄是 0。</summary>
        public int EntryQueries => _counters.Entries;

        public IDbConnection OpenConnection()
        {
            _counters.Connections++;
            return new ListConnection(_counters, this);
        }
    }

    private sealed class Counters
    {
        public int Connections { get; set; }

        public int Entries { get; set; }
    }

    private sealed class ListConnection : IDbConnection
    {
        private readonly Counters _counters;
        private readonly ListSource _source;

        public ListConnection(Counters counters, ListSource source)
        {
            _counters = counters;
            _source = source;
        }

        [AllowNull]
        public string ConnectionString { get; set; } = string.Empty;

        public int ConnectionTimeout => 0;

        public string Database => "Library";

        public ConnectionState State => ConnectionState.Open;

        public IDbCommand CreateCommand() => new ListCommand(_counters, _source);

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

    private sealed class ListCommand : IDbCommand
    {
        private readonly Counters _counters;
        private readonly ListSource _source;

        public ListCommand(Counters counters, ListSource source)
        {
            _counters = counters;
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

        public IDataReader ExecuteReader()
        {
            using var table = new DataTable();
            table.Columns.Add("name", typeof(string));

            switch (CommandText)
            {
                case SqlMetadataQueries.Collations:
                    _counters.Entries++;
                    table.Rows.Add("Chinese_Taiwan_Stroke_CI_AS");
                    table.Rows.Add("Latin1_General_CI_AS");
                    break;
                case SqlMetadataQueries.Languages:
                    _counters.Entries++;
                    table.Columns.Add("alias", typeof(string));
                    table.Rows.Add("Deutsch", "German");
                    table.Rows.Add("us_english", "English");
                    break;
                case SqlMetadataQueries.TimeZones:
                    _counters.Entries++;
                    table.Columns.Add("current_utc_offset", typeof(string));
                    table.Rows.Add("Taipei Standard Time", "+08:00");
                    table.Rows.Add("UTC", "+00:00");
                    break;
                case SqlMetadataQueries.DatabaseCollation:
                    table.Rows.Add(_source.DatabaseCollation);
                    break;
                case SqlMetadataQueries.LoginLanguage:
                    table.Rows.Add("us_english");
                    break;
                case SqlMetadataQueries.ServerTimeZone:
                    table.Rows.Add((object?)_source.ServerTimeZone ?? DBNull.Value);
                    break;
                default:
                    throw new NotSupportedException(CommandText);
            }

            return table.CreateDataReader();
        }
    }
}
