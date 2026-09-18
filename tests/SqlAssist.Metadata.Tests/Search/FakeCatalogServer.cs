using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using SqlAssist.Metadata.Querying;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 一台假的伺服器：幾個資料庫，每個資料庫有自己的物件、資料行與結構描述。
/// </summary>
/// <remarks>
/// 搜尋索引是照連線走的——決定查哪一個資料庫的是連線不是 SQL，跨資料庫靠
/// <see cref="SqlDatabaseScopedConnectionSource"/> 的 <c>ChangeDatabase</c>。
/// 因此假的那一份也要有「換目錄」這件事，否則跨資料庫那幾條測試量到的
/// 是假物件自己的行為，不是產品的。
/// </remarks>
internal sealed class FakeCatalogServer
{
    private readonly Dictionary<string, FakeCatalogDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);

    internal FakeCatalogServer(string serverKey = "server-a")
    {
        ServerKey = serverKey;
    }

    internal string ServerKey { get; }

    /// <summary>開過幾次連線；索引有沒有被重建靠這個數字，不靠結果筆數。</summary>
    internal int Opened { get; private set; }

    /// <summary>每一次執行的命令原文，順序照執行的先後。</summary>
    internal List<string> Commands { get; } = new();

    internal FakeCatalogDatabase Add(string databaseName)
    {
        var database = new FakeCatalogDatabase(databaseName);
        _databases[databaseName] = database;
        return database;
    }

    internal ISqlConnectionSource SourceFor(string databaseName) =>
        new FakeCatalogConnectionSource(this, databaseName);

    internal IDbConnection Open(string databaseName)
    {
        Opened++;
        var database = Find(databaseName);

        if (database.FailsOnOpen)
        {
            throw new UnreachableServerException();
        }

        return new FakeCatalogConnection(this, database);
    }

    /// <remarks>
    /// 找不到就丟 <see cref="DbException"/>：資料庫不存在、離線或這個登入進不去，
    /// 在真實連線上都停在 <c>ChangeDatabase</c>，而那一族正是要被降級成
    /// 「這一輪沒有這個資料庫的資料」的東西。
    /// </remarks>
    internal FakeCatalogDatabase Find(string databaseName) =>
        _databases.TryGetValue(databaseName, out var database)
            ? database
            : throw new UnreachableServerException();

    internal void Record(string commandText) => Commands.Add(commandText);
}

/// <summary>一個假的資料庫的內容。</summary>
internal sealed class FakeCatalogDatabase
{
    internal FakeCatalogDatabase(string name)
    {
        Name = name;
    }

    internal string Name { get; }

    internal List<FakeCatalogObject> Objects { get; } = new();

    /// <summary>資料行：屬於哪個 object_id，叫什麼。</summary>
    internal List<KeyValuePair<int, string>> Columns { get; } = new();

    internal List<string> Schemas { get; } = new();

    /// <summary>開連線就失敗；模擬連不上或沒有權限。</summary>
    internal bool FailsOnOpen { get; set; }

    /// <summary>命令原文含這一段時失敗；模擬單一條查詢在舊版伺服器上不成立。</summary>
    internal string? FailsOnQueryContaining { get; set; }

    internal FakeCatalogDatabase WithObject(
        int objectId,
        string schemaName,
        string name,
        string type,
        string? definition = null,
        DateTime? modifiedAt = null)
    {
        Objects.Add(new FakeCatalogObject(objectId, schemaName, name, type, definition, modifiedAt));
        return this;
    }

    internal FakeCatalogDatabase WithColumn(int objectId, string columnName)
    {
        Columns.Add(new KeyValuePair<int, string>(objectId, columnName));
        return this;
    }

    internal FakeCatalogDatabase WithSchema(string schemaName)
    {
        Schemas.Add(schemaName);
        return this;
    }
}

internal sealed class FakeCatalogObject
{
    internal FakeCatalogObject(
        int objectId, string schemaName, string name, string type, string? definition, DateTime? modifiedAt)
    {
        ObjectId = objectId;
        SchemaName = schemaName;
        Name = name;
        Type = type;
        Definition = definition;
        ModifiedAt = modifiedAt;
    }

    internal int ObjectId { get; }

    internal string SchemaName { get; }

    internal string Name { get; }

    /// <summary><c>sys.objects.type</c> 的代碼，或查詢自己貼的 <c>SN</c>／<c>TT</c>。</summary>
    internal string Type { get; }

    internal string? Definition { get; }

    internal DateTime? ModifiedAt { get; }
}

/// <summary>
/// 指向某一個假資料庫的連線來源。
/// </summary>
/// <remarks>
/// 快取鍵走 <see cref="SqlConnectionCacheKey"/> 而不是自己拼字串：拼法與產品分岔的話，
/// 這一份假物件會讓「同一個資料庫只建一次索引」永遠通過，而產品其實建了兩次。
/// </remarks>
internal sealed class FakeCatalogConnectionSource : ISqlConnectionSource
{
    private readonly FakeCatalogServer _server;

    internal FakeCatalogConnectionSource(FakeCatalogServer server, string databaseName)
    {
        _server = server;
        DatabaseName = databaseName;
        CacheKey = SqlConnectionCacheKey.Compose(server.ServerKey, databaseName);
    }

    public string CacheKey { get; }

    public string ServerCacheKey => _server.ServerKey;

    public string DatabaseName { get; }

    public IDbConnection OpenConnection() => _server.Open(DatabaseName);
}

internal sealed class FakeCatalogConnection : IDbConnection
{
    private readonly FakeCatalogServer _server;
    private FakeCatalogDatabase _database;

    internal FakeCatalogConnection(FakeCatalogServer server, FakeCatalogDatabase database)
    {
        _server = server;
        _database = database;
    }

    [AllowNull] public string ConnectionString { get; set; } = string.Empty;

    public int ConnectionTimeout => 0;

    public string Database => _database.Name;

    public ConnectionState State => ConnectionState.Open;

    public IDbCommand CreateCommand() => new FakeCatalogCommand(_server, _database);

    /// <remarks>
    /// 進不去的資料庫在這裡失敗，與開連線那一步失敗是同一種：跨資料庫走的是
    /// <c>ChangeDatabase</c>，真實連線上「離線、不存在、沒有權限」三種都停在這一步。
    /// </remarks>
    public void ChangeDatabase(string databaseName)
    {
        var target = _server.Find(databaseName);

        if (target.FailsOnOpen)
        {
            throw new UnreachableServerException();
        }

        _database = target;
    }

    public void Dispose()
    {
    }

    public void Open()
    {
    }

    public void Close()
    {
    }

    public IDbTransaction BeginTransaction() => throw new NotSupportedException();

    public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotSupportedException();
}

internal sealed class FakeCatalogCommand : IDbCommand
{
    private readonly FakeCatalogServer _server;
    private readonly FakeCatalogDatabase _database;

    internal FakeCatalogCommand(FakeCatalogServer server, FakeCatalogDatabase database)
    {
        _server = server;
        _database = database;
    }

    [AllowNull] public string CommandText { get; set; } = string.Empty;

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

    /// <remarks>
    /// 認哪一條查詢靠的是各自獨有的片段：物件那一條是唯一提到 <c>sys.sql_modules</c> 的，
    /// 資料行那一條是唯一 <c>FROM sys.columns</c> 的，剩下的就是結構描述。
    /// 照「有沒有提到 sys.schemas」認會把物件那一條也收進來——它 JOIN 了結構描述。
    /// </remarks>
    public IDataReader ExecuteReader()
    {
        _server.Record(CommandText);

        if (_database.FailsOnQueryContaining is { } fragment &&
            CommandText.IndexOf(fragment, StringComparison.Ordinal) >= 0)
        {
            throw new UnreachableServerException();
        }

        using var table = new DataTable();

        if (CommandText.IndexOf("sys.sql_modules", StringComparison.Ordinal) >= 0)
        {
            table.Columns.Add("object_id", typeof(int));
            table.Columns.Add("schema_name", typeof(string));
            table.Columns.Add("object_name", typeof(string));
            table.Columns.Add("type", typeof(string));
            table.Columns.Add("modify_date", typeof(DateTime));
            table.Columns.Add("definition", typeof(string));

            foreach (var entry in _database.Objects)
            {
                table.Rows.Add(
                    entry.ObjectId,
                    entry.SchemaName,
                    entry.Name,
                    entry.Type,
                    entry.ModifiedAt is { } modifiedAt ? modifiedAt : (object)DBNull.Value,
                    entry.Definition ?? (object)DBNull.Value);
            }
        }
        else if (CommandText.IndexOf("FROM sys.columns", StringComparison.Ordinal) >= 0)
        {
            table.Columns.Add("object_id", typeof(int));
            table.Columns.Add("column_name", typeof(string));

            foreach (var column in _database.Columns)
            {
                table.Rows.Add(column.Key, column.Value);
            }
        }
        else
        {
            table.Columns.Add("name", typeof(string));

            foreach (var schema in _database.Schemas)
            {
                table.Rows.Add(schema);
            }
        }

        return table.CreateDataReader();
    }
}

/// <summary><see cref="DbException"/> 是抽象的，測試要自己給一個具體型別。</summary>
internal sealed class UnreachableServerException : DbException
{
    internal UnreachableServerException()
        : base("連不上伺服器。")
    {
    }
}
