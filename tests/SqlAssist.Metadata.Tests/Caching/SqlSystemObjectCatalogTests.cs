using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Caching;

/// <summary>
/// <c>sys</c> 與 <c>INFORMATION_SCHEMA</c> 底下的名稱怎麼解析。
/// </summary>
/// <remarks>
/// 這一份與第一層分開載入（它有一兩千筆），因此「什麼時候才去載」是行為的一部分：
/// 早了是每一次開啟查詢視窗都多付一輪查詢，晚了是 <c>FROM sys.triggers</c> 的欄位
/// 在每一個位置都列不出來，而畫面上只是什麼都沒發生。
/// </remarks>
public sealed class SqlSystemObjectCatalogTests
{
    [Fact]
    public async Task 限定在系統結構描述時才載入系統物件()
    {
        var source = new CatalogSource();
        var catalog = Create(source);

        var user = await catalog.FindObjectsAsync("Lib_Reader", "dbo", CancellationToken.None);

        Assert.Single(user);
        Assert.DoesNotContain(source.Commands, text => text.Contains("sys.all_objects"));

        var system = await catalog.FindObjectsAsync("triggers", "sys", CancellationToken.None);

        Assert.Equal("sys", Assert.Single(system).SchemaName);
    }

    /// <remarks>
    /// 系統物件跟著 SQL Server 的版本走，不會在一次工作階段中途變動；
    /// 每一次按鍵重查一輪一兩千筆的清單只是讓打字卡住。
    /// </remarks>
    [Fact]
    public async Task 系統物件只查一次()
    {
        var source = new CatalogSource();
        var catalog = Create(source);

        await catalog.FindObjectsAsync("triggers", "sys", CancellationToken.None);
        await catalog.FindObjectsAsync("columns", "sys", CancellationToken.None);

        Assert.Equal(1, Count(source.Commands, "sys.all_objects"));
    }

    /// <remarks>
    /// 併進快照而不是各自留一份：滑鼠停留、F12、欄位建議與 <c>SELECT *</c> 展開
    /// 拿的都是這一份快照，各接一條的症狀是同一個名稱在有些位置答得出來、
    /// 有些位置沒有。
    /// </remarks>
    [Fact]
    public async Task 載入後併進快照()
    {
        var source = new CatalogSource();
        var catalog = Create(source);

        await catalog.FindObjectsAsync("triggers", "sys", CancellationToken.None);

        Assert.Single(catalog.CachedSnapshot.Find("triggers", "sys"));
        Assert.Single(catalog.CachedSnapshot.Objects);
    }

    /// <remarks>
    /// 換連線就是換一台伺服器，那一份要跟著丟掉；留著的症狀是新連線上查得到
    /// 一個那裡沒有的名稱。
    /// </remarks>
    [Fact]
    public async Task 重新整理之後要重新載入()
    {
        var source = new CatalogSource();
        var catalog = Create(source);

        await catalog.FindObjectsAsync("triggers", "sys", CancellationToken.None);
        catalog.Invalidate();

        Assert.Empty(catalog.CachedSnapshot.Find("triggers", "sys"));
        Assert.Single(await catalog.FindObjectsAsync("triggers", "sys", CancellationToken.None));
        Assert.Equal(2, Count(source.Commands, "sys.all_objects"));
    }

    /// <summary>
    /// 系統檢視的資料行不在 <c>sys.columns</c> 上。
    /// </summary>
    /// <remarks>
    /// 拿 <c>sys.columns</c> 去問系統物件的結果是「查詢成功，但一個欄位都沒有」，
    /// 而那與權限不足看起來一模一樣。
    /// </remarks>
    [Theory]
    [InlineData("dbo", false)]
    [InlineData("sys", true)]
    [InlineData("INFORMATION_SCHEMA", true)]
    [InlineData(null, false)]
    public void 系統物件的欄位改問all_columns(string? schemaName, bool system)
    {
        var query = SqlMetadataQueries.ColumnsFor(schemaName);

        Assert.Equal(system, query.Contains("FROM sys.all_columns AS c"));
        Assert.Equal(!system, query.Contains("FROM sys.columns AS c"));
    }

    /// <remarks>
    /// 兩條查詢由同一份本體組出來，差別只有那一個名字；拆成兩份完整查詢的症狀是
    /// 改了一邊另一邊沒改，而少掉的那幾欄在畫面上看不出來。
    /// </remarks>
    [Fact]
    public void 兩條欄位查詢只差在目錄檢視()
    {
        Assert.Equal(
            SqlMetadataQueries.SystemColumns,
            SqlMetadataQueries.Columns.Replace("sys.columns", "sys.all_columns"));
    }

    private static SqlMetadataCatalog Create(CatalogSource source) =>
        new(source, TimeSpan.FromMinutes(5), failureBackoff: TimeSpan.FromMinutes(5));

    private static int Count(IReadOnlyList<string> commands, string fragment)
    {
        var count = 0;

        foreach (var text in commands)
        {
            if (text.Contains(fragment))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>只替代資料庫 I/O；分層、去重與失效仍執行產品實作。</summary>
    private sealed class CatalogSource : ISqlConnectionSource
    {
        private readonly List<string> _commands = new();

        public string CacheKey => "library-server|Library";

        public string ServerCacheKey => "library-server";

        public string DatabaseName => "Library";

        public IReadOnlyList<string> Commands
        {
            get
            {
                lock (_commands)
                {
                    return _commands.ToArray();
                }
            }
        }

        public IDbConnection OpenConnection() => new CatalogConnection(Record);

        private void Record(string commandText)
        {
            lock (_commands)
            {
                _commands.Add(commandText);
            }
        }
    }

    private sealed class CatalogConnection(Action<string> record) : IDbConnection
    {
        [AllowNull] public string ConnectionString { get; set; } = string.Empty;

        public int ConnectionTimeout => 0;

        public string Database => "Library";

        public ConnectionState State => ConnectionState.Open;

        public IDbCommand CreateCommand() => new CatalogCommand(record);

        public void Dispose() { }

        public void Open() { }

        public void Close() { }

        public void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public IDbTransaction BeginTransaction() => throw new NotSupportedException();

        public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotSupportedException();
    }

    private sealed class CatalogCommand(Action<string> record) : IDbCommand
    {
        [AllowNull] public string CommandText { get; set; } = string.Empty;

        public int CommandTimeout { get; set; }

        public CommandType CommandType { get; set; }

        public IDbConnection? Connection { get; set; }

        public IDbTransaction? Transaction { get; set; }

        public UpdateRowSource UpdatedRowSource { get; set; }

        public IDataParameterCollection Parameters => throw new NotSupportedException();

        public IDbDataParameter CreateParameter() => throw new NotSupportedException();

        public void Dispose() { }

        public void Cancel() { }

        public void Prepare() => throw new NotSupportedException();

        public int ExecuteNonQuery() => throw new NotSupportedException();

        public object ExecuteScalar() => throw new NotSupportedException();

        public IDataReader ExecuteReader(CommandBehavior behavior) => ExecuteReader();

        public IDataReader ExecuteReader()
        {
            record(CommandText);

            using var table = new DataTable();

            // 物件清單的形狀與 SqlMetadataReader 一致；其餘目錄清單回傳合法的空結果。
            if (CommandText.Contains("sys.all_objects"))
            {
                AddObjectColumns(table);
                table.Rows.Add(11, "sys", "triggers", "V");
                table.Rows.Add(12, "INFORMATION_SCHEMA", "TABLES", "V");
            }
            else if (CommandText.Contains("FROM sys.objects"))
            {
                AddObjectColumns(table);
                table.Rows.Add(1, "dbo", "Lib_Reader", "U");
            }
            else
            {
                table.Columns.Add("name", typeof(string));
            }

            return table.CreateDataReader();
        }

        private static void AddObjectColumns(DataTable table)
        {
            table.Columns.Add("object_id", typeof(int));
            table.Columns.Add("schema_name", typeof(string));
            table.Columns.Add("name", typeof(string));
            table.Columns.Add("type", typeof(string));
        }
    }
}
