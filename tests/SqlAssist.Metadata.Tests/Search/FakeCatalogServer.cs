using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using SqlAssist.Metadata.Querying;
using SqlAssist.Metadata.Search;

namespace SqlAssist.Metadata.Tests.Search;

/// <summary>
/// 一台假的伺服器：幾個資料庫，每個資料庫有自己的物件、資料行與結構描述。
/// </summary>
/// <remarks>
/// 搜尋索引是照連線走的——決定查哪一個資料庫的是連線不是 SQL，跨資料庫靠
/// <see cref="SqlDatabaseScopedConnectionSource"/> 的 <c>ChangeDatabase</c>。
/// 因此假的那一份也要有「換目錄」這件事，否則跨資料庫那幾條測試量到的
/// 是假物件自己的行為，不是產品的。
///
/// 增量重新整理同理：這一份會真的照
/// <see cref="SqlCatalogSearchQueries.ModifiedAfterParameterName"/> 過濾，
/// 而且參數沒有綁值時當場失敗。回傳全部的話，「重新整理只撈變更的物件」永遠會通過，
/// 而產品其實整份重撈了。
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

    internal FakeCatalogDatabase Add(string databaseName, bool isSystem = false)
    {
        var database = new FakeCatalogDatabase(databaseName) { IsSystem = isSystem };
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

    internal IEnumerable<FakeCatalogDatabase> All() => _databases.Values;

    internal void Record(string commandText) => Commands.Add(commandText);

    /// <summary>某一條查詢被執行過幾次；「第二段有沒有被送出去」靠它。</summary>
    internal int CountCommands(string fragment)
    {
        var count = 0;

        foreach (var command in Commands)
        {
            if (command.IndexOf(fragment, StringComparison.Ordinal) >= 0) count++;
        }

        return count;
    }
}

/// <summary>一個假的資料庫的內容。</summary>
internal sealed class FakeCatalogDatabase
{
    internal FakeCatalogDatabase(string name)
    {
        Name = name;
    }

    internal string Name { get; }

    internal bool IsSystem { get; set; }

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

    /// <summary>改掉一個物件的定義與時間戳；增量重新整理測得出來靠的是這一支。</summary>
    internal FakeCatalogDatabase Touch(int objectId, string? definition, DateTime modifiedAt)
    {
        var index = IndexOf(objectId);
        var previous = Objects[index];
        Objects[index] = new FakeCatalogObject(
            previous.ObjectId, previous.SchemaName, previous.Name, previous.Type,
            definition ?? previous.Definition, modifiedAt);
        return this;
    }

    /// <summary>
    /// 改掉定義本文但<b>不動</b>時間戳。
    /// </summary>
    /// <remarks>
    /// 真實伺服器上做不到，而這正是重點：增量重新整理只撈時間戳變新的那幾個，所以這一份
    /// 改動<b>不應該</b>出現在重新整理後的索引裡。整份重撈的實作會把它撈回來，而那是唯一
    /// 分得出「真的增量」與「號稱增量」的證據——結果筆數兩邊一模一樣。
    /// </remarks>
    internal FakeCatalogDatabase Rewrite(int objectId, string definition)
    {
        var index = IndexOf(objectId);
        var previous = Objects[index];
        Objects[index] = new FakeCatalogObject(
            previous.ObjectId, previous.SchemaName, previous.Name, previous.Type, definition, previous.ModifiedAt);
        return this;
    }

    /// <summary>卸除一個物件；重新整理看不看得出來靠它。</summary>
    internal FakeCatalogDatabase Drop(int objectId)
    {
        Objects.RemoveAt(IndexOf(objectId));
        Columns.RemoveAll(column => column.Key == objectId);
        return this;
    }

    private int IndexOf(int objectId)
    {
        for (var index = 0; index < Objects.Count; index++)
        {
            if (Objects[index].ObjectId == objectId) return index;
        }

        throw new InvalidOperationException($"{Name} 裡沒有 object_id {objectId}。");
    }

    internal DateTime? ModifiedAtOf(int objectId)
    {
        foreach (var entry in Objects)
        {
            if (entry.ObjectId == objectId) return entry.ModifiedAt;
        }

        return null;
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
    private readonly FakeParameterCollection _parameters = new();

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

    public IDataParameterCollection Parameters => _parameters;

    public IDbDataParameter CreateParameter() => new FakeParameter();

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
    /// 認哪一條查詢靠的是各自獨有的片段，而且順序有意義：定義本文那一條是唯一提到
    /// <c>sys.sql_modules</c> 的，資料行那一條是唯一 <c>FROM sys.columns</c> 的
    /// （它也 JOIN 了 <c>sys.objects</c>），物件那一條是剩下唯一 <c>FROM sys.objects AS o</c>
    /// 開頭的，最後才是結構描述。照「有沒有提到 sys.objects」認會把三條混在一起。
    /// </remarks>
    public IDataReader ExecuteReader()
    {
        _server.Record(CommandText);
        RejectUnboundParameters();

        if (_database.FailsOnQueryContaining is { } fragment &&
            CommandText.IndexOf(fragment, StringComparison.Ordinal) >= 0)
        {
            throw new UnreachableServerException();
        }

        using var table = new DataTable();

        if (Mentions("FROM sys.databases"))
        {
            ReadDatabases(table);
        }
        else if (Mentions("sys.sql_modules"))
        {
            ReadDefinitions(table);
        }
        else if (Mentions("FROM sys.columns"))
        {
            ReadColumns(table);
        }
        else if (Mentions("FROM sys.objects AS o"))
        {
            ReadObjects(table);
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

    private bool Mentions(string fragment) => CommandText.IndexOf(fragment, StringComparison.Ordinal) >= 0;

    /// <remarks>
    /// 漏綁值在真實伺服器上是「必須宣告純量變數」，而那是 <see cref="DbException"/>，
    /// 會被降級成「這一輪沒有資料」——搜尋對那個資料庫安靜地空掉。這裡照同一個形狀失敗，
    /// 讓每一條跑過查詢的測試都順便守住這件事。
    /// </remarks>
    private void RejectUnboundParameters()
    {
        if (!Mentions(SqlCatalogSearchQueries.ModifiedAfterParameterName)) return;
        if (_parameters.Contains(SqlCatalogSearchQueries.ModifiedAfterParameterName)) return;

        throw new UnreachableServerException();
    }

    /// <summary>增量界線；沒有綁或綁 NULL 時是 null，表示整份重撈。</summary>
    private DateTime? ModifiedAfter()
    {
        if (!_parameters.Contains(SqlCatalogSearchQueries.ModifiedAfterParameterName)) return null;

        var value = ((IDataParameter)_parameters[SqlCatalogSearchQueries.ModifiedAfterParameterName]).Value;
        return value is DateTime modifiedAfter ? modifiedAfter : null;
    }

    private static bool Keeps(DateTime? modifiedAfter, DateTime? modifiedAt) =>
        modifiedAfter is not { } boundary || (modifiedAt is { } at && at >= boundary);

    private void ReadObjects(DataTable table)
    {
        table.Columns.Add("object_id", typeof(int));
        table.Columns.Add("schema_name", typeof(string));
        table.Columns.Add("object_name", typeof(string));
        table.Columns.Add("type", typeof(string));
        table.Columns.Add("modify_date", typeof(DateTime));

        foreach (var entry in _database.Objects)
        {
            table.Rows.Add(
                entry.ObjectId,
                entry.SchemaName,
                entry.Name,
                entry.Type,
                entry.ModifiedAt is { } modifiedAt ? modifiedAt : (object)DBNull.Value);
        }
    }

    private void ReadDefinitions(DataTable table)
    {
        table.Columns.Add("object_id", typeof(int));
        table.Columns.Add("definition", typeof(string));

        var modifiedAfter = ModifiedAfter();

        foreach (var entry in _database.Objects)
        {
            if (entry.Definition is null || !Keeps(modifiedAfter, entry.ModifiedAt)) continue;

            table.Rows.Add(entry.ObjectId, entry.Definition);
        }
    }

    private void ReadColumns(DataTable table)
    {
        table.Columns.Add("object_id", typeof(int));
        table.Columns.Add("column_name", typeof(string));

        var modifiedAfter = ModifiedAfter();

        foreach (var column in _database.Columns)
        {
            if (!Keeps(modifiedAfter, _database.ModifiedAtOf(column.Key))) continue;

            table.Rows.Add(column.Key, column.Value);
        }
    }

    private void ReadDatabases(DataTable table)
    {
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("is_system", typeof(int));

        foreach (var database in _server.All())
        {
            table.Rows.Add(database.Name, database.IsSystem ? 1 : 0);
        }
    }
}

internal sealed class FakeParameter : IDbDataParameter
{
    public byte Precision { get; set; }

    public byte Scale { get; set; }

    public int Size { get; set; }

    public DbType DbType { get; set; }

    public ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public bool IsNullable => true;

    [AllowNull] public string ParameterName { get; set; } = string.Empty;

    [AllowNull] public string SourceColumn { get; set; } = string.Empty;

    public DataRowVersion SourceVersion { get; set; }

    public object? Value { get; set; }
}

/// <summary>
/// 只夠這幾條查詢用的參數集合。
/// </summary>
/// <remarks>
/// <see cref="IDataParameterCollection"/> 帶著整個 <see cref="IList"/>，但索引那一層只會
/// <c>Add</c>；其餘成員留成 <see cref="NotSupportedException"/>，多寫的那幾行沒有人會執行到，
/// 而它們一旦被叫到就表示產品換了用法，那時候要當場知道。
/// </remarks>
internal sealed class FakeParameterCollection : IDataParameterCollection
{
    private readonly List<IDbDataParameter> _parameters = new();

    public object this[string parameterName]
    {
        get => _parameters[IndexOf(parameterName)];
        set => throw new NotSupportedException();
    }

    public object? this[int index]
    {
        get => _parameters[index];
        set => throw new NotSupportedException();
    }

    public bool IsFixedSize => false;

    public bool IsReadOnly => false;

    public int Count => _parameters.Count;

    public bool IsSynchronized => false;

    public object SyncRoot => _parameters;

    public int Add(object? value)
    {
        _parameters.Add((IDbDataParameter)(value ?? throw new ArgumentNullException(nameof(value))));
        return _parameters.Count - 1;
    }

    public bool Contains(string parameterName) => IndexOf(parameterName) >= 0;

    public bool Contains(object? value) => value is IDbDataParameter parameter && _parameters.Contains(parameter);

    public void Clear() => _parameters.Clear();

    public int IndexOf(string parameterName)
    {
        for (var index = 0; index < _parameters.Count; index++)
        {
            if (string.Equals(_parameters[index].ParameterName, parameterName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    public int IndexOf(object? value) => value is IDbDataParameter parameter ? _parameters.IndexOf(parameter) : -1;

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(string parameterName) => throw new NotSupportedException();

    public void RemoveAt(int index) => _parameters.RemoveAt(index);

    public void CopyTo(Array array, int index) => throw new NotSupportedException();

    public IEnumerator GetEnumerator() => _parameters.GetEnumerator();
}

/// <summary><see cref="DbException"/> 是抽象的，測試要自己給一個具體型別。</summary>
internal sealed class UnreachableServerException : DbException
{
    internal UnreachableServerException()
        : base("連不上伺服器。")
    {
    }
}
